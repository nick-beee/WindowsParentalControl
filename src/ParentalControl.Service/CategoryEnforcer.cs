using ParentalControl.Core;
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
/// It also suspends a Blocked Program that runs, and gives each Blocked Start of the tick to
/// <see cref="BlockEnforcer"/>, the only code that closes a process.
/// It never closes or kills a process itself. If Windows refuses a suspension, it locks the session (fallback) through
/// <see cref="SessionLocker"/>, so a Lock Deferral can delay the lock.
/// Each suspension (also a blocked one) starts as a Pending Suspension: the Session Agent minimizes the windows of the
/// process, and the enforcer suspends it <see cref="PendingSuspensions.Delay"/> later. During a Session End Hold of a
/// session, it suspends no process of that session. After a refused hold, a Session End Guard keeps the processes
/// that the Session Agent resumed for the end of the session from a new suspension (<see cref="SessionEndGuards"/>).
/// A process that the child resumes again and again (a Child Resume) leads to a Resume Lock: after
/// <see cref="ResumeLocks.Threshold"/> Child Resumes of one user in <see cref="ResumeLocks.Window"/>, the enforcer locks each
/// unlocked session of that user through <see cref="SessionLocker"/> until the lock ends (<see cref="ResumeLocks"/>).
/// <see cref="UsageMonitorWorker"/> calls <see cref="Tick"/> every 60 seconds. A bonus approval, an admin
/// change, an unlock and the follow-up timer (the end of a Pending Suspension, a hold or a guard) call <see cref="RequestTick"/>.
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
    private readonly BlockEnforcer _blockEnforcer;
    private readonly StorageHealth _health = StorageHealth.Shared;
    private readonly SessionLocker _locker;

    private readonly object _gate = new();
    private bool _stopped;
    private DateOnly _sentDate;
    private readonly HashSet<string> _sentToday = new(StringComparer.OrdinalIgnoreCase);
    private int _tickRequested;
    private readonly SessionEndHolds _holds = new();
    private readonly SessionEndGuards _guards = new();
    private readonly PendingSuspensions _pending = new();
    private readonly ResumeLocks _resumes = new();
    // The last message of a Resume Lock per session. The locks run outside the gate, so two ticks can overlap.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastResumeLockMessage = new();
    private static readonly TimeSpan ResumeLockMessageInterval = TimeSpan.FromSeconds(10);
    private readonly Timer _followUp;
    // A copy of the Pending Suspensions for the status route, which reads it without the lock.
    private volatile IReadOnlyList<SuspendAction> _pendingSnapshot = [];

    /// <summary>For a test: a locker of its own, without a Lock Deferral from the web routes.</summary>
    public CategoryEnforcer(SessionTracker sessionTracker, ForegroundHistory foregroundHistory, TelegramBotService telegram, Serilog.ILogger logger)
        : this(sessionTracker, foregroundHistory, telegram, new SessionLocker(logger), logger) { }

    public CategoryEnforcer(SessionTracker sessionTracker, ForegroundHistory foregroundHistory, TelegramBotService telegram,
        SessionLocker locker, Serilog.ILogger logger)
    {
        _sessionTracker = sessionTracker;
        _locker = locker;
        _foregroundHistory = foregroundHistory;
        _telegram = telegram;
        _logger = logger;
        _followUp = new Timer(_ => RequestTick());
        _testSuspendFailExe = Environment.GetEnvironmentVariable(TestSuspendFailEnv);
        if (!string.IsNullOrWhiteSpace(_testSuspendFailExe))
            _logger.Warning("Category test hook active: a suspension of {Exe} fails", _testSuspendFailExe);
        _blockEnforcer = new BlockEnforcer(this, sessionTracker, logger);
        _ = Task.Run(_blockEnforcer.EnsureRunning); // at service start, before the first tick

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
        _blockEnforcer.Dispose();
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
        // The process start watcher runs also while no Restricted User is signed in, so that it sees the first
        // starts of a new session. After an error it starts again here. Outside the lock: a slow WMI start
        // must not stop the tick, which is the fallback of the watcher.
        _blockEnforcer.EnsureRunning();

        // The sessions of a Resume Lock. They are locked after the gate, because the message before the lock waits.
        var resumeLockSessions = new List<int>();
        lock (_gate)
        {
            if (_stopped)
                return;

            var nowUtc = DateTime.UtcNow;
            var today = ControlTime.Current.Today;
            if (_sentDate != today)
            {
                _sentToday.Clear();
                _sentDate = today;
            }

            var config = CategoryConfigCache.Current;

            foreach (var hold in _holds.Expire(nowUtc, today, id => _sessionTracker.ActiveSessions.ContainsKey(id)))
                OnHoldExpired(hold);
            foreach (var expired in _guards.Expire(nowUtc, id => _sessionTracker.ActiveSessions.ContainsKey(id)))
                _logger.Information("Session end guard of session {SessionId} ended by its time{Unconfirmed}", expired.Guard.SessionId,
                    expired.Confirmed ? "" : " without the end of the session: the next guard of the user in 15 min is repeated");

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
                    UsageLedger.Shared.CategoryUsage(id, today), // stored plus Pending Usage (storage-failure)
                    UsageRepository.GetUsage(id, today)?.BonusMinutes ?? 0));

            var records = SuspensionRepository.GetAll();
            var recordKeys = records.Select(r => (r.Pid, r.StartTimeUtc)).ToHashSet();
            _guards.Retain(recordKeys);
            _resumes.Retain(recordKeys);
            if (restrictedSessions.Count == 0 && records.Count == 0)
            {
                _pending.Clear();
                PublishAndSchedule();
                return;
            }

            var sessionIds = restrictedSessions.Keys.Concat(records.Select(r => r.SessionId)).ToHashSet();
            var processes = ProcessControl.ListProcesses(sessionIds);
            var suspendedNow = processes.Where(p => p.IsSuspended).Select(p => (p.Pid, p.StartTimeUtc)).ToHashSet();
            _guards.Observe(records.Select(r => (r.Pid, r.StartTimeUtc)), suspendedNow, nowUtc);
            var guarded = _guards.Covered(nowUtc);
            var held = _holds.HeldSessions(nowUtc);

            // A recorded process that is suspended now, outside a hold and a guard, is watched: a later run of it again
            // is a Child Resume (lock-after-repeated-resume D2). This also covers the first tick after a service start.
            foreach (var record in records)
            {
                var key = (record.Pid, record.StartTimeUtc);
                if (suspendedNow.Contains(key) && !held.Contains(record.SessionId) && !guarded.Contains(key))
                    _resumes.MarkSuspended(record.Pid, record.StartTimeUtc);
            }

            SendWarnings(users, dayLimits, RunningExes(processes, restrictedSessions, config.Resolver));

            // Blocks: the decision "at its start" runs before the planner. A Blocked Start is closed here and
            // skipped by the planner; each other blocked process is suspended by the planner.
            var pendingClose = CloseBlockedStarts(processes, restrictedSessions, users, records, config);

            ExeVerdict VerdictFor(int userId, string path)
            {
                var resolution = config.Resolver.Resolve(path);
                var exhausted = dayLimits.TryGetValue(userId, out var day) && day.IsExhausted(resolution);
                return new ExeVerdict(resolution.Category, exhausted, _blockEnforcer.IsBlockedProgram(resolution));
            }

            var plan = SuspensionPlanner.Plan(new PlannerInput(
                processes,
                restrictedSessions,
                VerdictFor,
                (userId, path) => _foregroundHistory.WasForeground(userId, today, path),
                records,
                SessionTracker.GetAgentExecutablePath(),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                held,
                p => pendingClose.Contains((p.Pid, p.StartTimeUtc)),
                _blockEnforcer.ProtectedPrograms,
                guarded));

            foreach (var record in plan.ToDrop)
            {
                if (_health.TryWrite(() => SuspensionRepository.Delete(record.Pid, record.StartTimeUtc), "drop of a suspension record", _logger))
                    _logger.Information("Dropped stale suspension record {Pid} ({Exe})", record.Pid, record.ExePath);
            }

            foreach (var record in plan.ToRelabel)
            {
                if (_health.TryWrite(() => SuspensionRepository.UpdateReason(record.Pid, record.StartTimeUtc, record.Reason, record.Category),
                        "new reason of a suspension record", _logger))
                    _logger.Information("Suspension of {Exe} (pid {Pid}) now has the reason {Reason}", record.ExePath, record.Pid, record.Reason);
            }

            foreach (var record in plan.ToResume)
                ResumeRecord(record, record.Reason == SuspensionReason.Blocked ? "block removed" : "time available again");

            CheckResumeLocks(plan, today, users, restrictedSessions, VerdictFor, resumeLockSessions);

            // Two phases: the agent minimizes the windows of a pending process, and the next steps suspend it.
            var step = _pending.Step(plan.ToSuspend, nowUtc);
            foreach (var action in step.Started)
                _logger.Information("Pending suspension of {Exe} (pid {Pid}, {Category}){Again}: suspend in {Delay} s",
                    action.ExePath, action.Process.Pid, action.Category, action.IsRecorded ? " again" : "", PendingSuspensions.Delay.TotalSeconds);

            foreach (var action in step.Ready)
                Suspend(action, users, dayLimits, config.Resolver);

            PublishAndSchedule();
        }

        foreach (var sessionId in resumeLockSessions)
            LockForResumeLock(sessionId);
    }

    /// <summary>
    /// Ends the Resume Locks whose reason ended, counts the Child Resumes of this tick, writes the event and sends the
    /// alert for each new Resume Lock, and collects the unlocked sessions of each user with a Resume Lock. Call it with the
    /// lock, after the plan.
    /// </summary>
    private void CheckResumeLocks(SuspensionPlan plan, DateOnly today, IReadOnlyDictionary<int, User> users,
        IReadOnlyDictionary<int, int> restrictedSessions, Func<int, string, ExeVerdict> verdict, List<int> lockSessions)
    {
        static int Bonus(int userId, DateOnly day) => UsageRepository.GetUsage(userId, day)?.BonusMinutes ?? 0;
        string Name(int userId) => users.TryGetValue(userId, out var u) ? u.Username : UserRepository.GetById(userId)?.Username ?? userId.ToString();

        // A resumed exe keeps the lock due while it has no time left or is still a Blocked Program. A user without a
        // session has no limits in this tick: keep the lock; the next day or Bonus Minutes still end it.
        var ended = _resumes.Release(today, userId => Bonus(userId, today), (userId, path) =>
        {
            if (!restrictedSessions.Values.Contains(userId))
                return true;
            var v = verdict(userId, path);
            return v.Category != AppCategory.System
                   && (v.IsExhausted || (v.IsBlocked && !_blockEnforcer.ProtectedPrograms.IsProtected(path)));
        });
        foreach (var end in ended)
            _logger.Information("Resume Lock of {Username} ended: {Reason}", Name(end.UserId), end.Reason);

        var resumed = plan.ToSuspend.Where(a => a.IsRecorded)
            .Select(a => new ResumedProcess(a.Process.Pid, a.Process.StartTimeUtc, a.UserId, a.ExePath));
        var check = _resumes.Check(resumed, SessionLocker.MonotonicNow(), today, userId => Bonus(userId, today));
        foreach (var userId in check.Counted)
        {
            var started = check.Started.Any(s => s.UserId == userId);
            _logger.Warning("Child Resume of {Username}: a paused program runs again ({State})", Name(userId),
                started ? $"{ResumeLocks.Threshold} of {ResumeLocks.Threshold} in {ResumeLocks.Window.TotalMinutes} min: Resume Lock starts"
                : _resumes.IsLocked(userId) ? "during the Resume Lock"
                : $"{_resumes.ChildResumes(userId)} of {ResumeLocks.Threshold} in {ResumeLocks.Window.TotalMinutes} min");
        }

        foreach (var start in check.Started)
        {
            var username = Name(start.UserId);
            var exes = string.Join(", ", start.ExePaths.Select(WindowsPath.GetFileName));
            _logger.Warning("Resume Lock of {Username} started: {Exes} resumed again and again. The sessions lock until the next day, Bonus Minutes, or no reason left",
                username, exes);
            var sid = users.TryGetValue(start.UserId, out var user) ? user.Sid : UserRepository.GetById(start.UserId)?.Sid;
            if (sid is not null)
                _health.TryWrite(() => EventRepository.LogEvent(sid, EventType.SECURITY_ALERT,
                    $"Paused programs resumed again and again ({exes}): session locked (Resume Lock)"), "security alert event", _logger);
            if (_sentToday.Add($"resumelock:{start.UserId}"))
                SendParentAlert($"Parental Control: {username} on {Environment.MachineName} resumed paused programs again and again ({exes}). " +
                                "The session is locked for the rest of the day. Bonus Minutes end the lock.");
        }

        foreach (var (sessionId, userId) in restrictedSessions)
        {
            if (_resumes.IsLocked(userId)
                && _sessionTracker.ActiveSessions.TryGetValue(sessionId, out var session) && !session.IsLocked)
                lockSessions.Add(sessionId);
        }
    }

    /// <summary>
    /// Locks a session of a Resume Lock: first the message (at most one time in 10 seconds per session), then the Enforced
    /// Workstation Lock. During a Lock Deferral it sends no message and does not lock; the lock is owed, and the tick at
    /// the end of the deferral locks again if the Resume Lock still runs. It never closes a program. Call it without the lock.
    /// </summary>
    private void LockForResumeLock(int sessionId)
    {
        const string reason = "Resume Lock";
        if (_stopped || _locker.ShouldDefer(sessionId, reason))
            return;

        var now = DateTime.UtcNow;
        if (!_lastResumeLockMessage.TryGetValue(sessionId, out var last) || now - last > ResumeLockMessageInterval)
        {
            _lastResumeLockMessage[sessionId] = now;
            var texts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
            NotificationManager.SendMessage(sessionId, texts["resume_lock_title"], texts["resume_lock_body"],
                isWarning: true, timeoutSeconds: 5, wait: true);
        }

        if (_locker.Lock(sessionId, reason))
        {
            _sessionTracker.OnSessionLock(sessionId, $"{reason} - workstation locked");
            _logger.Information("Session {SessionId} locked: {Reason}", sessionId, reason);
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

            // The service, or after a refused hold the Session Agent, resumes these processes for the end of the session.
            // A run of them again is no Child Resume until the service suspends them again (lock-after-repeated-resume D2).
            foreach (var record in SuspensionRepository.GetAll().Where(r => r.SessionId == sessionId))
                _resumes.MarkResumedByService(record.Pid, record.StartTimeUtc);
            switch (result)
            {
                case HoldRequestResult.Refused:
                    _logger.Warning("Session end hold refused for {Username} (session {SessionId}, {Reason}): an unconfirmed hold in the last {Minutes} min",
                        user.Username, sessionId, reason, SessionEndHolds.UnconfirmedWindow.TotalMinutes);
                    // A failed write must not stop the Session End Guard (storage-failure).
                    _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT,
                        $"Session end request refused ({reason}): an earlier sign-out did not complete"),
                        "security alert event", _logger);
                    StartGuard(sessionId, user);
                    PublishAndSchedule();
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
                if (resumed is ProcessOpResult.Ok or ProcessOpResult.NotFound)
                    _guards.MarkResumed(record.Pid, record.StartTimeUtc);
                _logger.Information("Resumed {Exe} (pid {Pid}, {Category}) for the session end: {Result} {Detail}",
                    record.ExePath, record.Pid, record.Category, resumed, detail);
            }

            PublishAndSchedule();
            return result;
        }
    }

    /// <summary>
    /// The hold was refused, but the Session Agent resumed the Suspended Apps already. Starts a Session End Guard that
    /// covers each eligible record of the session. A repeated guard (an earlier guard of the user in 15 min ended
    /// without the end of the session) writes an event and alerts the parent once per day. The guard never locks the
    /// session: a lock in the first second of a sign-out makes Windows end a program with unsaved data. Call it with the lock.
    /// </summary>
    private void StartGuard(int sessionId, User user)
    {
        var keys = SuspensionRepository.GetAll().Where(r => r.SessionId == sessionId).Select(r => (r.Pid, r.StartTimeUtc));
        var start = _guards.Start(sessionId, user.Id, keys, DateTime.UtcNow);
        switch (start.Outcome)
        {
            case GuardStartOutcome.AlreadyRunning:
                _logger.Information("Session end guard of {Username} (session {SessionId}) runs already: not extended", user.Username, sessionId);
                return;
            case GuardStartOutcome.NothingToCover:
                _logger.Information("No session end guard for {Username} (session {SessionId}): no process to cover", user.Username, sessionId);
                return;
        }

        var guard = start.Guard!;
        _logger.Information("Session end guard started for {Username} (session {SessionId}) for {Seconds} s, covers pids [{Pids}]{Repeated}",
            user.Username, sessionId, SessionEndGuards.Duration.TotalSeconds, string.Join(", ", guard.Covered.Select(c => c.Pid)),
            guard.Repeated ? ", repeated: an earlier guard in the last 15 min ended without the end of the session" : "");
        if (!guard.Repeated)
            return;

        _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT,
            "Repeated sign-out that did not complete: the paused programs ran for about one minute again"),
            "security alert event", _logger);
        if (_sentToday.Add($"guard:{user.Id}"))
            SendParentAlert($"Parental Control: {user.Username} on {Environment.MachineName} started a sign-out again and again " +
                            "without completing it. Each time the paused programs run for about one minute, and then they are paused again.");
    }

    private void OnSessionEnded(int sessionId)
    {
        lock (_gate)
        {
            if (_holds.SessionEnded(sessionId))
                _logger.Information("Session end hold of session {SessionId} confirmed: the session ended", sessionId);
            if (_guards.SessionEnded(sessionId))
                _logger.Information("Session end guard of session {SessionId} ended: the session ended", sessionId);
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
            _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT,
                "A sign-out did not complete: the paused programs ran for the end of the session and are paused again"),
                "security alert event", _logger);
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
        if (_guards.NextEndUtc is { } guardEnd && (next is null || guardEnd < next))
            next = guardEnd;

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

    /// <summary>
    /// Stops the block enforcer, resumes every recorded process (also a blocked one) and stops further suspensions.
    /// The service calls it when it stops.
    /// </summary>
    public void ResumeAll()
    {
        _blockEnforcer.Stop();
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
            _guards.MarkResumed(record.Pid, record.StartTimeUtc);
            _resumes.MarkResumedByService(record.Pid, record.StartTimeUtc);
            // A failed delete keeps the record; the next tick resumes the process again (no effect) and tries the delete.
            _health.TryWrite(() => SuspensionRepository.Delete(record.Pid, record.StartTimeUtc), "delete of a suspension record", _logger);
            _logger.Information("Resumed {Exe} (pid {Pid}, {Category}): {Reason} ({Result})",
                record.ExePath, record.Pid, record.Category, reason, result);
        }
        else
        {
            // Keep the record: the next tick tries again.
            _logger.Warning("Could not resume {Exe} (pid {Pid}): {Result} {Detail}", record.ExePath, record.Pid, result, detail);
        }
    }

    private void Suspend(SuspendAction action, IReadOnlyDictionary<int, User> users,
        IReadOnlyDictionary<int, DayLimits> dayLimits, CategoryResolver resolver)
    {
        var p = action.Process;
        var record = new SuspensionRecord(p.Pid, p.StartTimeUtc, p.SessionId, action.UserId, action.Category, action.ExePath, action.Reason);

        // Record before the suspension: a crash between the two then leaves no suspended process without a record.
        // No suspension without a record (storage-failure): a failed write skips this process, and the tick counts as
        // failed. The Storage Lockdown covers the gap.
        if (!action.IsRecorded &&
            !_health.TryWrite(() => SuspensionRepository.Insert(record), $"suspension record of {action.ExePath}", _logger))
            return;

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
                _guards.MarkSuspended(p.Pid, p.StartTimeUtc);
                _resumes.MarkSuspended(p.Pid, p.StartTimeUtc);
                _logger.Information("Suspended {Exe} (pid {Pid}, {Category}, {Reason}) of {Username}{Again}",
                    action.ExePath, p.Pid, action.Category, action.Reason, username, action.IsRecorded ? " again" : "");
                if (action.Reason == SuspensionReason.Blocked)
                {
                    if (!action.IsRecorded && user is not null)
                        _blockEnforcer.OnBlockedSuspended(p.Pid, action.ExePath, user);
                }
                else if (!action.IsRecorded && user is not null)
                {
                    // One message per limit and day. An App Limit names the app, a Category Limit names the category.
                    var limit = dayLimits.GetValueOrDefault(action.UserId)?.LimitFor(resolver.Resolve(action.ExePath));
                    var notice = SuspensionNotice.For(action.Category,
                        limit, CategoryNames.For(action.Category, SettingsRepository.GetLanguagePreset()));
                    if (_sentToday.Add($"suspended:{action.UserId}:{notice.Key}"))
                    {
                        var eventText = notice.IsAppLimit
                            ? $"{notice.Name} App Limit reached ({action.Category}): programs paused"
                            : $"{action.Category} limit reached: programs paused";
                        _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.LIMIT_REACHED, eventText),
                            "category limit event", _logger);
                        SendToUser(action.UserId, SettingsRepository.GetMessage(SettingsRepository.KeyMsgCategorySuspended,
                            new Dictionary<string, string> { ["name"] = notice.Name, ["category"] = notice.CategoryName }));
                    }
                }
                break;

            case ProcessOpResult.NotFound:
                if (!action.IsRecorded)
                    _health.TryWrite(() => SuspensionRepository.Delete(record.Pid, record.StartTimeUtc), "delete of a suspension record", _logger);
                break;

            default:
                if (!action.IsRecorded)
                    _health.TryWrite(() => SuspensionRepository.Delete(record.Pid, record.StartTimeUtc), "delete of a suspension record", _logger);
                Fallback(action, user, username, $"{result} {detail}");
                break;
        }
    }

    /// <summary>
    /// The suspension failed: lock the session and tell the parent (one alert per exe and day). During a Lock Deferral
    /// (the session ends) it neither locks nor alerts: the tick at the end of the deferral tries the suspension again.
    /// </summary>
    private void Fallback(SuspendAction action, User? user, string username, string reason)
    {
        var sessionId = action.Process.SessionId;
        var alreadyLocked = _sessionTracker.ActiveSessions.TryGetValue(sessionId, out var session) && session.IsLocked;
        var lockReason = $"{action.Category} program cannot be paused";

        // Check the deferral before the alert: the alert says "locked", and its daily marker would hide the alert of the
        // real lock later.
        if (!alreadyLocked && _locker.ShouldDefer(sessionId, lockReason))
        {
            _logger.Warning("Could not suspend {Exe} (pid {Pid}) of {Username}: {Reason}. The lock of session {SessionId} is deferred",
                action.ExePath, action.Process.Pid, username, reason, sessionId);
            return;
        }

        _logger.Warning("Could not suspend {Exe} (pid {Pid}) of {Username}: {Reason}. Locking session {SessionId}",
            action.ExePath, action.Process.Pid, username, reason, sessionId);

        // The lock first, then the writes (storage-failure). A sign-out that starts between the check above and this
        // call still defers the lock: then no alert either, and the next tick tries the suspension again.
        if (!alreadyLocked)
        {
            if (!_locker.Lock(sessionId, lockReason))
                return;
            _sessionTracker.OnSessionLock(sessionId, $"{lockReason} - workstation locked");
        }

        if (_sentToday.Add($"alert:{action.UserId}:{action.ExePath}"))
        {
            if (user is not null)
                _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.SECURITY_ALERT, $"Cannot pause {WindowsPath.GetFileName(action.ExePath)} ({action.Category}): session locked"),
                    "security alert event", _logger);
            SendParentAlert($"Parental Control: cannot pause {WindowsPath.GetFileName(action.ExePath)} ({action.Category}) " +
                            $"of {username} on {Environment.MachineName}. The session is locked.\nPath: {action.ExePath}\nReason: {reason}");
        }
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
                var name = status.Rule?.DisplayName ?? categoryName;
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

    /// <summary>
    /// Decides the block of each process of a restricted session that has no suspension record, and closes each
    /// Blocked Start through the block enforcer. Returns the Blocked Starts, so that the planner skips them.
    /// </summary>
    private HashSet<(int Pid, DateTime Start)> CloseBlockedStarts(IReadOnlyList<SessionProcess> processes,
        IReadOnlyDictionary<int, int> restrictedSessions, IReadOnlyDictionary<int, User> users,
        IReadOnlyList<SuspensionRecord> records, CategoryConfigCache.Snapshot config)
    {
        var pending = new HashSet<(int Pid, DateTime Start)>();
        var recorded = records.Select(r => (r.Pid, r.StartTimeUtc)).ToHashSet();
        foreach (var process in processes)
        {
            if (process.ExePath is null || recorded.Contains((process.Pid, process.StartTimeUtc)))
                continue;
            if (!restrictedSessions.TryGetValue(process.SessionId, out var userId) || !users.TryGetValue(userId, out var user))
                continue;
            if (_blockEnforcer.Decide(process, isRestrictedSession: true, config) != BlockDecision.Close)
                continue;

            pending.Add((process.Pid, process.StartTimeUtc));
            _blockEnforcer.Close(process.Pid, process.StartTimeUtc, process.ExePath, user);
        }
        return pending;
    }

    /// <summary>True if the user has a session that is not locked, so that a session message can show.</summary>
    internal bool HasUnlockedSession(int userId)
    {
        foreach (var (_, session) in _sessionTracker.ActiveSessions)
        {
            if (!session.IsLocked && UserRepository.GetBySid(session.UserSid)?.Id == userId)
                return true;
        }
        return false;
    }

    /// <summary>Sends a session message to each unlocked session of the user. Returns the number of sessions.</summary>
    internal int SendToUser(int userId, string message)
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

    internal void SendParentAlert(string text)
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
            _logger.Information("Parent alert to Telegram: {Result}", ok ? "sent" : "failed");
        });
    }
}
