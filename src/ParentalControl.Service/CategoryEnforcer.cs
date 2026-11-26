using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Core.Services;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Service;

/// <summary>
/// Enforces the Category Limits and App Limits. It sends the warnings, suspends the processes of an exe
/// that has no time left, keeps them suspended and resumes them when time is available again.
/// It never closes or kills a process. If Windows refuses a suspension, it locks the session (fallback).
/// Each suspension starts as a Pending Suspension: the Session Agent minimizes the windows of the process, and the
/// enforcer suspends it <see cref="PendingSuspensions.Delay"/> later. During a Session End Hold of a session, it
/// suspends no process of that session.
/// <see cref="UsageMonitorWorker"/> calls <see cref="Tick"/> every 60 seconds. A bonus approval, an admin
/// change, an unlock and the follow-up timer (the end of a Pending Suspension or of a hold) call <see cref="RequestTick"/>.
/// </summary>
public sealed class CategoryEnforcer : IDisposable
{
    /// <summary>
    /// Test hook for the VM: an exe file name. A suspension of this exe fails as "access denied".
    /// Set it in the service environment only on a test machine.
    /// </summary>
    public const string TestSuspendFailEnv = "PARENTAL_CONTROL_TEST_SUSPEND_FAIL";

    private const string MessageTitle = "Parental Control";

    // The follow-up tick comes this much after the due time, so that it never comes a few milliseconds early.
    private static readonly TimeSpan FollowUpMargin = TimeSpan.FromSeconds(0.5);

    private readonly SessionTracker _sessionTracker;
    private readonly ForegroundHistory _foregroundHistory;
    private readonly TelegramBotService _telegram;
    private readonly Serilog.ILogger _logger;
    private readonly string? _testSuspendFailExe;

    private readonly object _gate = new();
    private bool _stopped;
    private DateOnly _sentDate;
    private readonly HashSet<string> _sentToday = new(StringComparer.OrdinalIgnoreCase);
    private int _tickRequested;
    private readonly SessionEndHolds _holds = new();
    private readonly PendingSuspensions _pending = new();
    private readonly Timer _followUp;
    // A copy of the Pending Suspensions for the status route, which reads it without the lock.
    private volatile IReadOnlyList<SuspendAction> _pendingSnapshot = [];

    public CategoryEnforcer(SessionTracker sessionTracker, ForegroundHistory foregroundHistory, TelegramBotService telegram, Serilog.ILogger logger)
    {
        _sessionTracker = sessionTracker;
        _foregroundHistory = foregroundHistory;
        _telegram = telegram;
        _logger = logger;
        _followUp = new Timer(_ => RequestTick());
        _testSuspendFailExe = Environment.GetEnvironmentVariable(TestSuspendFailEnv);
        if (!string.IsNullOrWhiteSpace(_testSuspendFailExe))
            _logger.Warning("Category test hook active: a suspension of {Exe} fails", _testSuspendFailExe);

        UsageRepository.BonusMinutesAdded += RequestTick;
        CategoryConfigCache.Changed += RequestTick;
        _sessionTracker.SessionUnlocked += OnSessionUnlocked;
        _sessionTracker.SessionEnded += OnSessionEnded;
    }

    public void Dispose()
    {
        UsageRepository.BonusMinutesAdded -= RequestTick;
        CategoryConfigCache.Changed -= RequestTick;
        _sessionTracker.SessionUnlocked -= OnSessionUnlocked;
        _sessionTracker.SessionEnded -= OnSessionEnded;
        _followUp.Dispose();
    }

    private void OnSessionUnlocked(int sessionId) => RequestTick();

    /// <summary>Runs <see cref="Tick"/> soon on a pool thread. Calls while one waits to start are merged.</summary>
    public void RequestTick()
    {
        if (Interlocked.Exchange(ref _tickRequested, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _tickRequested, 0);
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during the requested category check");
            }
        });
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_stopped)
                return;

            var nowUtc = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_sentDate != today)
            {
                _sentToday.Clear();
                _sentDate = today;
            }

            var config = CategoryConfigCache.Current;

            foreach (var hold in _holds.Expire(nowUtc, today, id => _sessionTracker.ActiveSessions.ContainsKey(id)))
                OnHoldExpired(hold);

            // The sessions of Restricted Users, locked or not.
            var restrictedSessions = new Dictionary<int, int>();
            var users = new Dictionary<int, User>();
            foreach (var (sessionId, session) in _sessionTracker.ActiveSessions)
            {
                var user = UserRepository.GetBySid(session.UserSid);
                if (user is null || !user.IsRestricted)
                    continue;
                restrictedSessions[sessionId] = user.Id;
                users[user.Id] = user;
            }

            var dayLimits = users.Keys.ToDictionary(
                id => id,
                id => config.Calculator.Evaluate(
                    today,
                    CategoryUsageRepository.GetForUserAndDate(id, today),
                    UsageRepository.GetUsage(id, today)?.BonusMinutes ?? 0));

            var records = SuspensionRepository.GetAll();
            if (restrictedSessions.Count == 0 && records.Count == 0)
            {
                _pending.Clear();
                PublishAndSchedule();
                return;
            }

            var sessionIds = restrictedSessions.Keys.Concat(records.Select(r => r.SessionId)).ToHashSet();
            var processes = ProcessControl.ListProcesses(sessionIds);

            SendWarnings(users, dayLimits, RunningExes(processes, restrictedSessions, config.Resolver));

            var plan = SuspensionPlanner.Plan(new PlannerInput(
                processes,
                restrictedSessions,
                (userId, path) =>
                {
                    var resolution = config.Resolver.Resolve(path);
                    var exhausted = dayLimits.TryGetValue(userId, out var day) && day.IsExhausted(resolution);
                    return new ExeVerdict(resolution.Category, exhausted);
                },
                (userId, path) => _foregroundHistory.WasForeground(userId, today, path),
                records,
                SessionTracker.GetAgentExecutablePath(),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                _holds.HeldSessions(nowUtc)));

            foreach (var record in plan.ToDrop)
            {
                SuspensionRepository.Delete(record.Pid, record.StartTimeUtc);
                _logger.Information("Dropped stale suspension record {Pid} ({Exe})", record.Pid, record.ExePath);
            }

            foreach (var record in plan.ToResume)
                ResumeRecord(record, "time available again");

            // Two phases: the agent minimizes the windows of a pending process, and the next steps suspend it.
            var step = _pending.Step(plan.ToSuspend, nowUtc);
            foreach (var action in step.Started)
                _logger.Information("Pending suspension of {Exe} (pid {Pid}, {Category}){Again}: suspend in {Delay} s",
                    action.ExePath, action.Process.Pid, action.Category, action.IsRecorded ? " again" : "", PendingSuspensions.Delay.TotalSeconds);

            foreach (var action in step.Ready)
                Suspend(action, users);

            PublishAndSchedule();
        }
    }

    /// <summary>
    /// The Pending Suspensions of the user (pid and start time). The Session Agent minimizes their windows.
    /// It reads a copy, so it does not wait for a tick.
    /// </summary>
    public IReadOnlyList<(int Pid, DateTime StartTimeUtc)> GetPendingApps(int userId) =>
        _pendingSnapshot.Where(a => a.UserId == userId).Select(a => (a.Process.Pid, a.Process.StartTimeUtc)).ToList();

    /// <summary>
    /// The session ends (sign-out, restart, shutdown or Forced Logoff). Starts a Session End Hold if the rate limit permits it,
    /// and then resumes every recorded process of the session. The records stay: after a completed sign-out the planner
    /// drops them, and if the session goes on, the processes are suspended again after the hold.
    /// A <paramref name="trusted"/> request (Forced Logoff from the admin page) is never refused.
    /// </summary>
    public HoldRequestResult ResumeSessionForEnd(int sessionId, User user, bool trusted, string reason)
    {
        lock (_gate)
        {
            if (_stopped)
                return HoldRequestResult.Refused;

            var result = _holds.Request(sessionId, user.Id, DateTime.UtcNow, trusted);
            switch (result)
            {
                case HoldRequestResult.Refused:
                    _logger.Warning("Session end hold refused for {Username} (session {SessionId}, {Reason}): an unconfirmed hold in the last {Minutes} min",
                        user.Username, sessionId, reason, SessionEndHolds.UnconfirmedWindow.TotalMinutes);
                    EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT,
                        $"Session end request refused ({reason}): an earlier sign-out did not complete");
                    return result;
                case HoldRequestResult.AlreadyHeld:
                    _logger.Information("Session end request for {Username} (session {SessionId}, {Reason}): the session is already held",
                        user.Username, sessionId, reason);
                    break;
                default:
                    _logger.Information("Session end hold started for {Username} (session {SessionId}, {Reason}, trusted {Trusted}) for {Seconds} s",
                        user.Username, sessionId, reason, trusted, SessionEndHolds.HoldDuration.TotalSeconds);
                    break;
            }

            foreach (var record in SuspensionRepository.GetAll().Where(r => r.SessionId == sessionId))
            {
                var resumed = ProcessControl.Resume(record.Pid, record.StartTimeUtc, out var detail);
                _logger.Information("Resumed {Exe} (pid {Pid}, {Category}) for the session end: {Result} {Detail}",
                    record.ExePath, record.Pid, record.Category, resumed, detail);
            }

            PublishAndSchedule();
            return result;
        }
    }

    private void OnSessionEnded(int sessionId)
    {
        lock (_gate)
        {
            if (_holds.SessionEnded(sessionId))
                _logger.Information("Session end hold of session {SessionId} confirmed: the session ended", sessionId);
            PublishAndSchedule();
        }
    }

    private void OnHoldExpired(ExpiredHold hold)
    {
        if (hold.Confirmed)
        {
            _logger.Information("Session end hold of session {SessionId} confirmed: the session does not exist any more", hold.SessionId);
            return;
        }

        var user = UserRepository.GetById(hold.UserId);
        var username = user?.Username ?? hold.UserId.ToString();
        _logger.Warning("Session end hold of {Username} (session {SessionId}) ended without the end of the session; alert {Alert}",
            username, hold.SessionId, hold.Alert);
        if (user is not null)
            EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT,
                "A sign-out did not complete: the paused programs ran for the end of the session and are paused again");
        if (hold.Alert)
            SendParentAlert($"Parental Control: a sign-out of {username} on {Environment.MachineName} did not complete. " +
                            "The paused programs ran for about one minute and are paused again.");
    }

    /// <summary>Publishes the Pending Suspensions for the status route and sets the follow-up timer. Call it with the lock.</summary>
    private void PublishAndSchedule()
    {
        _pendingSnapshot = _pending.Pending;

        var next = _pending.NextDueUtc;
        if (_holds.NextEndUtc is { } holdEnd && (next is null || holdEnd < next))
            next = holdEnd;

        if (_stopped || next is null)
        {
            _followUp.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = next.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        _followUp.Change(delay + FollowUpMargin, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Resumes every recorded process and stops further suspensions. The service calls it when it stops.</summary>
    public void ResumeAll()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending.Clear();
            PublishAndSchedule();
            foreach (var record in SuspensionRepository.GetAll())
                ResumeRecord(record, "service stop");
        }
    }

    private void ResumeRecord(SuspensionRecord record, string reason)
    {
        var result = ProcessControl.Resume(record.Pid, record.StartTimeUtc, out var detail);
        if (result is ProcessOpResult.Ok or ProcessOpResult.NotFound)
        {
            SuspensionRepository.Delete(record.Pid, record.StartTimeUtc);
            _logger.Information("Resumed {Exe} (pid {Pid}, {Category}): {Reason} ({Result})",
                record.ExePath, record.Pid, record.Category, reason, result);
        }
        else
        {
            // Keep the record: the next tick tries again.
            _logger.Warning("Could not resume {Exe} (pid {Pid}): {Result} {Detail}", record.ExePath, record.Pid, result, detail);
        }
    }

    private void Suspend(SuspendAction action, IReadOnlyDictionary<int, User> users)
    {
        var p = action.Process;
        var record = new SuspensionRecord(p.Pid, p.StartTimeUtc, p.SessionId, action.UserId, action.Category, action.ExePath);

        // Record before the suspension: a crash between the two then leaves no suspended process without a record.
        if (!action.IsRecorded)
            SuspensionRepository.Insert(record);

        ProcessOpResult result;
        string detail;
        if (!string.IsNullOrWhiteSpace(_testSuspendFailExe) &&
            string.Equals(WindowsPath.GetFileName(action.ExePath), _testSuspendFailExe.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            result = ProcessOpResult.AccessDenied;
            detail = $"forced by {TestSuspendFailEnv}";
        }
        else
        {
            result = ProcessControl.Suspend(p.Pid, p.StartTimeUtc, out detail);
        }

        var username = users.TryGetValue(action.UserId, out var user) ? user.Username : action.UserId.ToString();
        switch (result)
        {
            case ProcessOpResult.Ok:
                _logger.Information("Suspended {Exe} (pid {Pid}, {Category}) of {Username}{Again}",
                    action.ExePath, p.Pid, action.Category, username, action.IsRecorded ? " again" : "");
                if (!action.IsRecorded && user is not null && _sentToday.Add($"suspended:{action.UserId}:{action.Category}"))
                {
                    EventRepository.LogEvent(user.Sid, EventType.LIMIT_REACHED, $"{action.Category} limit reached: programs paused");
                    var categoryName = CategoryNames.For(action.Category, SettingsRepository.GetLanguagePreset());
                    SendToUser(action.UserId, SettingsRepository.GetMessage(SettingsRepository.KeyMsgCategorySuspended,
                        new Dictionary<string, string> { ["name"] = categoryName, ["category"] = categoryName }));
                }
                break;

            case ProcessOpResult.NotFound:
                if (!action.IsRecorded)
                    SuspensionRepository.Delete(record.Pid, record.StartTimeUtc);
                break;

            default:
                if (!action.IsRecorded)
                    SuspensionRepository.Delete(record.Pid, record.StartTimeUtc);
                Fallback(action, user, username, $"{result} {detail}");
                break;
        }
    }

    /// <summary>The suspension failed: lock the session and tell the parent (one alert per exe and day).</summary>
    private void Fallback(SuspendAction action, User? user, string username, string reason)
    {
        var sessionId = action.Process.SessionId;
        _logger.Warning("Could not suspend {Exe} (pid {Pid}) of {Username}: {Reason}. Locking session {SessionId}",
            action.ExePath, action.Process.Pid, username, reason, sessionId);

        if (_sentToday.Add($"alert:{action.UserId}:{action.ExePath}"))
        {
            if (user is not null)
                EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT, $"Cannot pause {WindowsPath.GetFileName(action.ExePath)} ({action.Category}): session locked");
            SendParentAlert($"Parental Control: cannot pause {WindowsPath.GetFileName(action.ExePath)} ({action.Category}) " +
                            $"of {username} on {Environment.MachineName}. The session is locked.\nPath: {action.ExePath}\nReason: {reason}");
        }

        if (_sessionTracker.ActiveSessions.TryGetValue(sessionId, out var session) && session.IsLocked)
            return;

        SessionManager.LockSession(sessionId);
        _sessionTracker.OnSessionLock(sessionId, $"{action.Category} program cannot be paused - workstation locked");
    }

    /// <summary>
    /// The resolved exes of the processes that run in the sessions of each Restricted User, from the process list
    /// of the service. An App Limit warning needs a running process of its app.
    /// </summary>
    private static Dictionary<int, List<CategoryResolution>> RunningExes(
        IReadOnlyList<SessionProcess> processes, IReadOnlyDictionary<int, int> restrictedSessions, CategoryResolver resolver)
    {
        var running = new Dictionary<int, List<CategoryResolution>>();
        foreach (var process in processes)
        {
            if (process.ExePath is null || !restrictedSessions.TryGetValue(process.SessionId, out var userId))
                continue;
            if (!running.TryGetValue(userId, out var list))
                running[userId] = list = [];
            list.Add(resolver.Resolve(process.ExePath));
        }
        return running;
    }

    private void SendWarnings(IReadOnlyDictionary<int, User> users, IReadOnlyDictionary<int, DayLimits> dayLimits,
        IReadOnlyDictionary<int, List<CategoryResolution>> runningExes)
    {
        foreach (var (userId, day) in dayLimits)
        {
            foreach (var status in day.DueWarnings(runningExes.GetValueOrDefault(userId) ?? []))
            {
                if (!_sentToday.Add($"warn:{userId}:{status.Key}:{status.DueWarning}"))
                    continue;

                // A category message names the category in the language of the preset. An App Limit names the app.
                var categoryName = CategoryNames.For(status.Category, SettingsRepository.GetLanguagePreset());
                var name = status.Rule is null ? categoryName : AppName(status.Rule);
                var message = SettingsRepository.GetMessage(SettingsRepository.KeyMsgCategoryWarn, new Dictionary<string, string>
                {
                    ["name"] = name,
                    ["category"] = categoryName,
                    ["minutes"] = status.RemainingMinutes.ToString(),
                });
                var sent = SendToUser(userId, message);
                if (sent == 0)
                {
                    // All sessions of the user are locked. Try again at the next tick (also the tick after an unlock).
                    _sentToday.Remove($"warn:{userId}:{status.Key}:{status.DueWarning}");
                    continue;
                }
                _logger.Information("Category warning {Key} ({Remaining} min left) to {Username}: sent to {Sessions} session(s)",
                    status.Key, status.RemainingMinutes, users[userId].Username, sent);
            }
        }
    }

    /// <summary>Sends a session message to each unlocked session of the user. Returns the number of sessions.</summary>
    private int SendToUser(int userId, string message)
    {
        var count = 0;
        foreach (var (sessionId, session) in _sessionTracker.ActiveSessions)
        {
            if (session.IsLocked)
                continue;
            var user = UserRepository.GetBySid(session.UserSid);
            if (user?.Id != userId)
                continue;
            NotificationManager.SendMessage(sessionId, MessageTitle, message, isWarning: true, timeoutSeconds: 30);
            count++;
        }
        return count;
    }

    private void SendParentAlert(string text)
    {
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(chatId))
        {
            _logger.Warning("No Telegram chat configured; alert not sent: {Text}", text);
            return;
        }

        // The alert is sent as Telegram Markdown. Exe names and paths are untrusted text: escape the markup characters.
        var escaped = text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");
        _ = Task.Run(async () =>
        {
            var ok = await _telegram.SendTextMessageAsync(chatId, escaped);
            _logger.Information("Category alert to Telegram: {Result}", ok ? "sent" : "failed");
        });
    }

    /// <summary>The display name of the app of an App Limit: the file name without ".exe", or the last folder name.</summary>
    private static string AppName(AppRule rule)
    {
        var text = rule.Pattern.TrimEnd('\\');
        var name = WindowsPath.GetFileName(text);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
