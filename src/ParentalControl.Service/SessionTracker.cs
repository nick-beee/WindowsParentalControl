using System.Collections.Concurrent;
using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Core.Services;
using ParentalControl.Domain.Sessions;
using ParentalControl.Domain.Time;
namespace ParentalControl.Service;

/// <summary>
/// A tracked session. <see cref="LastTickUtc"/> and <see cref="LastTickMono"/> (<c>Environment.TickCount64</c>) mark the
/// last count of time (control-clock D3): the elapsed time never comes from the local time.
/// </summary>
public sealed record ActiveSession(int SessionId, string UserSid, string Username, DateTime LastTickUtc, long LastTickMono, bool IsLocked = false)
{
    public static ActiveSession Create(int sessionId, string userSid, string username, bool isLocked = false) =>
        new(sessionId, userSid, username, DateTime.UtcNow, Environment.TickCount64, isLocked);

    /// <summary>The same session with the last tick set to now.</summary>
    public ActiveSession Stamped() => this with { LastTickUtc = DateTime.UtcNow, LastTickMono = Environment.TickCount64 };
}

public sealed class SessionTracker
{
    private readonly ConcurrentDictionary<int, ActiveSession> _activeSessions = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastUnlockRejectNotification = new();
    private volatile bool _isAwake = true;
    private readonly Serilog.ILogger _logger;
    private IAgentStarter? _agentStarter;
    private readonly TelegramBotService? _telegramBotService;
    private readonly BreakEnforcer? _breakEnforcer;
    private readonly UsageLedger _ledger = UsageLedger.Shared;
    private readonly StorageHealth _health = StorageHealth.Shared;
    private readonly SessionLocker _locker;

    public SessionTracker(Serilog.ILogger logger) : this(null, null, new SessionLocker(logger), logger) { }

    public SessionTracker(TelegramBotService? telegramBotService, BreakEnforcer? breakEnforcer, SessionLocker locker, Serilog.ILogger logger)
    {
        _telegramBotService = telegramBotService;
        _breakEnforcer = breakEnforcer;
        _locker = locker;
        _logger = logger;
        // The Lock Deferral of an ended session ends with it; a deferral whose session still exists owes its lock.
        _locker.TrackSessions(id => _activeSessions.ContainsKey(id));
        SessionEnded += _locker.OnSessionEnded;
    }

    public bool IsAwake => _isAwake;

    /// <summary>Raised after an unlock that the service accepts. The Category enforcer checks the session again.</summary>
    public event Action<int>? SessionUnlocked;

    /// <summary>A session ended (a sign-out event or a stale session). The argument is the session id.</summary>
    public event Action<int>? SessionEnded;

    public IReadOnlyDictionary<int, ActiveSession> ActiveSessions => _activeSessions;

    /// <summary>Writes an event row. A failed write is logged and reported, and never stops a lock (storage-failure).</summary>
    private void LogEvent(string userSid, EventType type, string details = "") =>
        _health.TryWrite(() => EventRepository.LogEvent(userSid, type, details), $"event {type}", _logger);

    /// <summary>For a test: tracks a session without a Windows session.</summary>
    internal void TrackForTest(ActiveSession session) => _activeSessions[session.SessionId] = session;

    public ActiveSession? GetConsoleSession()
    {
        var consoleId = SessionManager.GetActiveConsoleSessionId();
        if (consoleId >= 0 && _activeSessions.TryGetValue(consoleId, out var session))
        {
            return session;
        }
        return null;
    }


    public void Initialize()
    {
        var sessions = SessionManager.GetLoggedOnSessions();
        foreach (var (sessionId, username, sid, isDisconnected) in sessions)
        {
            // The start worker calls this before and after the start gate (require-admin-sign-in N-c): a known
            // session is not read again.
            if (sid is null || _activeSessions.ContainsKey(sessionId)) continue;

            // The connect state does not show a Win+L lock. Read the lock flag of Windows too.
            var (flag, rawFlags) = SessionManager.QueryLockFlag(sessionId);
            var isLocked = SessionLockRules.InitialLockState(isDisconnected, flag);

            var user = UserRepository.GetBySid(sid);
            if (user is null)
            {
                user = UserRepository.Upsert(sid, username, false);
            }

            _activeSessions.TryAdd(sessionId, ActiveSession.Create(sessionId, sid, username, isLocked));
            _logger.Information("Recovered existing session: {Username} (SID: {Sid}) on session {SessionId} (Locked: {IsLocked}, Disconnected: {IsDisconnected}, LockFlag: {LockFlag}, SessionFlags: {RawFlags})",
                username, sid, sessionId, isLocked, isDisconnected, flag, rawFlags);

            if (!isLocked)
            {
                EnsureAgentRunning(sessionId);
            }
        }

        ReportServiceStartToBreaks();
    }

    /// <summary>Reports the service start to the Breaks for each Restricted User, with or without an unlocked session.</summary>
    private void ReportServiceStartToBreaks()
    {
        if (_breakEnforcer is null) return;
        try
        {
            foreach (var user in UserRepository.GetAll().Where(u => u.IsRestricted))
            {
                var hasUnlocked = _activeSessions.Values.Any(s => s.UserSid == user.Sid && !s.IsLocked);
                _breakEnforcer.OnServiceStart(user, hasUnlocked);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while reporting the service start to the Breaks");
        }
    }

    /// <summary>A Rest of the user starts or ends. An error in the Breaks must not stop the session handling.</summary>
    private void ReportRest(string userSid, bool start)
    {
        if (_breakEnforcer is null) return;
        try
        {
            var user = UserRepository.GetBySid(userSid);
            if (user is null || !user.IsRestricted) return;
            if (start) _breakEnforcer.OnRestStart(user);
            else _breakEnforcer.OnRestEnd(user);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while reporting a Rest to the Breaks");
        }
    }

    /// <summary>True if a Break of the user runs. Then the service adds no minutes to the Allowance Time or to Continuous Use.</summary>
    private bool IsInBreak(User user) =>
        user.IsRestricted && _breakEnforcer?.GetRunningBreakEnd(user.Id) is not null;

    /// <summary>Adds the elapsed minutes of a Restricted User to Continuous Use, the same minutes as for the Allowance Time.</summary>
    private void CountBreakMinutes(User user, int minutes)
    {
        if (_breakEnforcer is null || !user.IsRestricted) return;
        try
        {
            _breakEnforcer.OnMinutesCounted(user, minutes);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while counting Continuous Use for {Username}", user.Username);
        }
    }

    public void SynchronizeSessions()
    {
        try
        {
            var loggedOn = SessionManager.GetLoggedOnSessions();
            var loggedOnIds = new HashSet<int>();

            foreach (var (sessionId, username, sid, isDisconnected) in loggedOn)
            {
                if (sid is null) continue;
                loggedOnIds.Add(sessionId);

                // The connect state does not show a Win+L lock, so it never sets a session to unlocked (issue #9).
                var (flag, rawFlags) = SessionManager.QueryLockFlag(sessionId);

                if (!_activeSessions.TryGetValue(sessionId, out var existing))
                {
                    var isLocked = SessionLockRules.InitialLockState(isDisconnected, flag);
                    var user = UserRepository.GetBySid(sid) ?? UserRepository.Upsert(sid, username, false);
                    _activeSessions.TryAdd(sessionId, ActiveSession.Create(sessionId, sid, username, isLocked));
                    _logger.Information("Synchronized session for {Username} on session {SessionId} (Locked: {IsLocked}, Disconnected: {IsDisconnected}, LockFlag: {LockFlag}, SessionFlags: {RawFlags})",
                        username, sessionId, isLocked, isDisconnected, flag, rawFlags);

                    if (!isLocked)
                    {
                        EnsureAgentRunning(sessionId);
                    }
                }
                else
                {
                    switch (SessionLockRules.Reconcile(existing.IsLocked, isDisconnected, flag))
                    {
                        case LockReconcileAction.MarkLocked:
                            _logger.Information("Session {SessionId} ({Username}) is locked in Windows but tracked as unlocked (Disconnected: {IsDisconnected}, LockFlag: {LockFlag}, SessionFlags: {RawFlags})",
                                sessionId, existing.Username, isDisconnected, flag, rawFlags);
                            OnSessionLock(sessionId, isDisconnected ? "Windows session disconnected" : "Windows session locked");
                            break;
                        case LockReconcileAction.RunUnlockCheck:
                            _logger.Information("Session {SessionId} ({Username}) is unlocked in Windows but tracked as locked (LockFlag: {LockFlag}, SessionFlags: {RawFlags}); running the unlock check",
                                sessionId, existing.Username, flag, rawFlags);
                            OnSessionUnlock(sessionId);
                            break;
                    }
                }
            }

            foreach (var (sessionId, session) in _activeSessions)
            {
                if (!loggedOnIds.Contains(sessionId))
                {
                    if (_activeSessions.TryRemove(sessionId, out var removed))
                    {
                        FlushSessionTime(removed);
                        _logger.Information("Removed stale session for {Username} (Session {SessionId})", removed.Username, sessionId);
                        SessionEnded?.Invoke(sessionId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error synchronizing sessions from Terminal Services");
        }
    }

    public void OnUserLogon(int sessionId)
    {
        var eventTime = DateTime.Now;
        var username = SessionManager.GetSessionUsername(sessionId);
        var sid = SessionManager.GetSessionUserSid(sessionId);

        if (username is null || sid is null)
        {
            _logger.Warning("Could not resolve user for session {SessionId}", sessionId);
            return;
        }

        var user = UserRepository.GetBySid(sid);
        if (user is null)
        {
            user = UserRepository.Upsert(sid, username, false);
        }

        if (!user.IsRestricted)
        {
            _activeSessions.TryAdd(sessionId, ActiveSession.Create(sessionId, sid, username));
            LogEvent(sid, EventType.LOGIN);
            _logger.Information("User logged in (unrestricted): {Username}", username);
            EnsureAgentRunning(sessionId);

            if (SettingsRepository.IsTelegramNotifyAdminLogonEnabled() && _telegramBotService != null)
            {
                _ = Task.Run(() => _telegramBotService.SendAdminSignInAlertAsync(sessionId, username, Environment.MachineName, eventTime));
            }
            return;
        }

        // control-clock: the day and the time of day come from the Control Time Zone, not from the system time zone.
        var moment = ControlTime.Current.Read();
        var today = moment.Today;
        var limit = ScheduleRepository.GetEffectiveLimit(user.Id, moment.DayOfWeek, today);
        UsageRecord? usage = null;
        var used = 0;
        if (limit is not null)
        {
            // An end in the repeated hour at the end of daylight saving time ends at its first instant (harden-storage-lockdown, D9).
            if (ControlTime.Current.IsOutsideWindow(moment, limit.ScheduleStart, limit.ScheduleEnd))
            {
                _logger.Information("Login denied (outside schedule): {Username}", username);
                Thread.Sleep(300);
                NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgLoginDeniedCurfew), isWarning: true, timeoutSeconds: 5, wait: true);
                SessionManager.ForceLogoff(sessionId);
                _activeSessions.TryRemove(sessionId, out _);
                LogEvent(sid, EventType.LOGIN_DENIED, "Outside allowed schedule");
                return;
            }

            // The stored minutes plus the Pending Usage, read together (storage-failure; harden-storage-lockdown, D7).
            usage = UsageRepository.GetUsage(user.Id, today);
            used = _ledger.AllowanceUsed(user.Id, today);
            var totalAllowed = limit.DailyMinutes + (usage?.BonusMinutes ?? 0);
            if ((usage is not null || used > 0) && used >= totalAllowed)
            {
                _logger.Information("Login denied (limit reached): {Username}", username);
                Thread.Sleep(300);
                NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgLoginDeniedLimit), isWarning: true, timeoutSeconds: 5, wait: true);
                SessionManager.ForceLogoff(sessionId);
                _activeSessions.TryRemove(sessionId, out _);
                LogEvent(sid, EventType.LOGIN_DENIED, "Daily limit already reached");
                return;
            }
        }

        if (_breakEnforcer?.GetRunningBreakEnd(user.Id) is { } breakEndsUtc)
        {
            // A sign-out would close the programs of the autostart (hard rule): lock the new session instead.
            _logger.Information("Sign-in during a Break (until {End:o}): {Username}; locking the session", breakEndsUtc, username);
            var tracked = ActiveSession.Create(sessionId, sid, username, isLocked: true);
            _activeSessions.TryAdd(sessionId, tracked);
            if (_breakEnforcer.LockForRunningBreak(sessionId, breakEndsUtc))
            {
                LogEvent(sid, EventType.LOGIN, "Break running - session locked");
                LogEvent(sid, EventType.SESSION_LOCKED, "Break running on sign-in");
            }
            else
            {
                TrackDeferredLock(sessionId, tracked);
                LogEvent(sid, EventType.LOGIN, "Break running - lock deferred");
            }
            return;
        }

        _activeSessions.TryAdd(sessionId, ActiveSession.Create(sessionId, sid, username));
        ReportRest(sid, start: false);

        var loginDetail = "";
        if (limit is not null)
        {
            var totalAllowed = limit.DailyMinutes + (usage?.BonusMinutes ?? 0);
            var remaining = Math.Max(0, totalAllowed - used);
            loginDetail = $"Remaining: {remaining / 60}h {remaining % 60}m";
            NotificationManager.SendLogonBanner(sessionId, username, remaining, limit.ScheduleStart, limit.ScheduleEnd);
        }
        LogEvent(sid, EventType.LOGIN, loginDetail);
        _logger.Information("User logged in (restricted): {Username}", username);
        EnsureAgentRunning(sessionId);

        if (SettingsRepository.IsTelegramNotifySignInEnabled() && _telegramBotService != null)
        {
            var remaining = limit != null ? Math.Max(0, (limit.DailyMinutes + (usage?.BonusMinutes ?? 0)) - used) : 0;
            var start = limit?.ScheduleStart;
            var end = limit?.ScheduleEnd;
            _ = Task.Run(() => _telegramBotService.SendUserSignInNotificationAsync(username, Environment.MachineName, eventTime, remaining, start, end));
        }
    }

    public void OnUserLogoff(int sessionId)
    {
        var eventTime = DateTime.Now;
        _lastUnlockRejectNotification.TryRemove(sessionId, out _);
        if (_activeSessions.TryRemove(sessionId, out var session))
        {
            FlushSessionTime(session);
            if (!session.IsLocked) ReportRest(session.UserSid, start: true);
            LogEvent(session.UserSid, EventType.LOGOUT);
            _logger.Information("User logged off: {Username}", session.Username);
            var user = UserRepository.GetBySid(session.UserSid);
            if (user != null && user.IsRestricted && SettingsRepository.IsTelegramNotifySignOutEnabled() && _telegramBotService != null)
            {
                var today = ControlTime.Current.Today;
                var used = _ledger.AllowanceUsed(user.Id, today);
                _ = Task.Run(() => _telegramBotService.SendUserSignOutNotificationAsync(session.Username, Environment.MachineName, eventTime, used));
            }
        }

        // Also if the session is not tracked any more: Forced Logoff removes it before Windows ends it.
        SessionEnded?.Invoke(sessionId);
    }

    public void OnSleep()
    {
        _isAwake = false;
        _logger.Information("System entering sleep");

        foreach (var (sessionId, session) in _activeSessions)
        {
            FlushSessionTime(session);
            if (!session.IsLocked) ReportRest(session.UserSid, start: true);
            _activeSessions[sessionId] = session.Stamped();
            LogEvent(session.UserSid, EventType.SLEEP);
        }
    }

    public void OnWake()
    {
        _isAwake = true;
        _logger.Information("System resuming from sleep");

        var nowUtc = DateTime.UtcNow;
        foreach (var (sessionId, session) in _activeSessions)
        {
            var sleepMinutes = (int)(nowUtc - session.LastTickUtc).TotalMinutes;
            var detail = sleepMinutes > 1 ? $"After ~{sleepMinutes} min sleep" : "";
            _activeSessions[sessionId] = session.Stamped();
            if (!session.IsLocked) ReportRest(session.UserSid, start: false);
            LogEvent(session.UserSid, EventType.WAKE, detail);
        }
    }

    public void RemoveSession(int sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
    }

    public void OnSessionLock(int sessionId, string reason = "")
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            // The lock state first: a failed write of the time must not keep the session counted as unlocked.
            _activeSessions[sessionId] = session.Stamped() with { IsLocked = true };
            FlushSessionTime(session);
            // Only a session that was unlocked starts a Rest. A second lock event must not move the start of the Rest.
            if (!session.IsLocked) ReportRest(session.UserSid, start: true);
            LogEvent(session.UserSid, EventType.SESSION_LOCKED, reason);
            _logger.Information("Session {SessionId} ({Username}) locked: {Reason}", sessionId, session.Username, reason);
        }
        else
        {
            var username = SessionManager.GetSessionUsername(sessionId);
            var sid = SessionManager.GetSessionUserSid(sessionId);
            if (username != null && sid != null)
            {
                UserRepository.GetBySid(sid);
                _activeSessions.TryAdd(sessionId, ActiveSession.Create(sessionId, sid, username, isLocked: true));
                LogEvent(sid, EventType.SESSION_LOCKED, reason);
                _logger.Information("Session {SessionId} ({Username}) tracked as locked: {Reason}", sessionId, username, reason);
            }
        }
    }

    public void OnSessionUnlock(int sessionId)
    {
        // A session that the service did not track is restored here; its unlock also ends a Rest.
        var wasLocked = true;
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            wasLocked = session.IsLocked;
        }
        else
        {
            var username = SessionManager.GetSessionUsername(sessionId);
            var sid = SessionManager.GetSessionUserSid(sessionId);
            if (username != null && sid != null)
            {
                var recoveredUser = UserRepository.GetBySid(sid) ?? UserRepository.Upsert(sid, username, false);
                session = ActiveSession.Create(sessionId, sid, username);
                _activeSessions.TryAdd(sessionId, session);
                _logger.Information("Session {SessionId} ({Username}) restored on unlock", sessionId, username);
            }
            else
            {
                _logger.Warning("Session {SessionId} unlocked but user could not be determined", sessionId);
                return;
            }
        }

        var user = UserRepository.GetBySid(session.UserSid);
        if (user is not null && user.IsRestricted)
        {
            var moment = ControlTime.Current.Read();
            var today = moment.Today;
            var limit = ScheduleRepository.GetEffectiveLimit(user.Id, moment.DayOfWeek, today);

            if (limit is not null)
            {
                var usage = UsageRepository.GetUsage(user.Id, today);
                var used = _ledger.AllowanceUsed(user.Id, today);
                var totalAllowed = limit.DailyMinutes + (usage?.BonusMinutes ?? 0);
                var isCurfewExpired = ControlTime.Current.IsOutsideWindow(moment, limit.ScheduleStart, limit.ScheduleEnd);
                var isLimitExpired = (usage is not null || used > 0) && used >= totalAllowed;

                if (isCurfewExpired || isLimitExpired)
                {
                    var reason = isCurfewExpired ? "Outside allowed schedule on unlock" : "Daily limit reached on unlock";
                    var message = isCurfewExpired
                        ? SettingsRepository.GetMessage(SettingsRepository.KeyMsgLoginDeniedCurfew)
                        : SettingsRepository.GetMessage(SettingsRepository.KeyMsgLoginDeniedLimit);

                    // A Lock Deferral: the session stays unlocked for the ticks, so the tick at its end locks it.
                    if (_locker.ShouldDefer(sessionId, reason))
                    {
                        TrackDeferredLock(sessionId, session);
                        return;
                    }

                    _logger.Information("Unlock rejected ({Reason}): {Username} on session {SessionId}", reason, session.Username, sessionId);

                    _activeSessions[sessionId] = session.Stamped() with { IsLocked = true };

                    var now = DateTime.UtcNow;
                    var shouldNotify = !_lastUnlockRejectNotification.TryGetValue(sessionId, out var lastTime) || (now - lastTime) > TimeSpan.FromSeconds(10);

                    if (shouldNotify)
                    {
                        _lastUnlockRejectNotification[sessionId] = now;
                        Thread.Sleep(300);
                        NotificationManager.SendMessage(sessionId, "Parental Control", message, isWarning: true, timeoutSeconds: 5, wait: true);
                    }

                    // A sign-out that started during the message defers the lock: no event either.
                    if (!_locker.Lock(sessionId, reason))
                    {
                        TrackDeferredLock(sessionId, session);
                        return;
                    }
                    LogEvent(session.UserSid, EventType.LOGIN_DENIED, reason);
                    return;
                }
            }

            if (_breakEnforcer?.GetRunningBreakEnd(user.Id) is { } breakEndsUtc)
            {
                // A Lock Deferral: the session stays unlocked for the ticks, so the tick at its end locks it again.
                if (_locker.ShouldDefer(sessionId, "Break running on unlock"))
                {
                    TrackDeferredLock(sessionId, session);
                    return;
                }

                _logger.Information("Unlock rejected (Break until {End:o}): {Username} on session {SessionId}", breakEndsUtc, session.Username, sessionId);
                _activeSessions[sessionId] = session.Stamped() with { IsLocked = true };
                if (!_breakEnforcer.LockForRunningBreak(sessionId, breakEndsUtc))
                {
                    TrackDeferredLock(sessionId, session);
                    return;
                }
                LogEvent(session.UserSid, EventType.LOGIN_DENIED, "Break running on unlock");
                return;
            }

            EnsureAgentRunning(sessionId);
        }

        _activeSessions[sessionId] = session.Stamped() with { IsLocked = false };
        if (wasLocked) ReportRest(session.UserSid, start: false);
        LogEvent(session.UserSid, EventType.SESSION_UNLOCKED, "Session unlocked");
        _logger.Information("Session {SessionId} ({Username}) unlocked", sessionId, session.Username);
        SessionUnlocked?.Invoke(sessionId);
    }

    /// <summary>
    /// A Lock Deferral stopped the lock of the unlock check or of a sign-in during a Break, and Windows shows the session
    /// as unlocked. The tracker marks it as unlocked, so the ticks count its time as used time and check the Allowance
    /// Time, the Schedule Window and the Breaks again: the tick at the end of the deferral locks it if a lock is still
    /// due. A tracked lock would make the ticks skip the session, and only a lock flag of Windows would end that.
    /// It writes no event, ends no Rest and raises no <see cref="SessionUnlocked"/>: the lock is only delayed.
    /// </summary>
    /// <param name="before">The tracked state before the check.</param>
    private void TrackDeferredLock(int sessionId, ActiveSession before)
    {
        // The time counts from now on; a session that was unlocked already keeps its last tick.
        _activeSessions[sessionId] = (before.IsLocked ? before.Stamped() : before) with { IsLocked = false };
        _logger.Information("Session {SessionId} ({Username}) tracked as unlocked: a Lock Deferral delayed its lock", sessionId, before.Username);
    }

    /// <summary>
    /// A console or remote connect. After an Enforced Workstation Lock (a disconnect), the child signs in again and
    /// the session connects. If Windows then shows the session as unlocked, the unlock check runs.
    /// If the session still shows the lock screen, the unlock event comes later.
    /// </summary>
    public void OnSessionReconnect(int sessionId)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session) || !session.IsLocked)
        {
            return;
        }

        var (flag, rawFlags) = SessionManager.QueryLockFlag(sessionId);
        _logger.Information("Session {SessionId} ({Username}) connected while tracked as locked (LockFlag: {LockFlag}, SessionFlags: {RawFlags})",
            sessionId, session.Username, flag, rawFlags);
        if (flag == WtsLockFlag.Unlocked)
        {
            OnSessionUnlock(sessionId);
        }
    }

    /// <summary>Connects the starter of the Session Agent (<see cref="AgentChannelHost"/>), after the host is built.</summary>
    public void AttachAgentStarter(IAgentStarter starter) => _agentStarter = starter;

    /// <summary>
    /// True if the Session Agent that the service started in the session still runs (its launch record, design D9).
    /// A process of the child with the agent name or the agent exe is not the agent.
    /// </summary>
    public bool IsAgentRunning(int sessionId) => _agentStarter?.IsRunning(sessionId) ?? false;

    /// <summary>
    /// Starts the Session Agent with an Agent Channel if the session has no running agent of the service (design D7,
    /// D9). Never throws.
    /// </summary>
    public void EnsureAgentRunning(int sessionId)
    {
        if (_agentStarter is { } starter)
            starter.EnsureRunning(sessionId);
        else
            _logger.Debug("No agent starter attached; the agent of session {SessionId} does not start", sessionId);
    }

    internal static string? GetAgentExecutablePath()
    {
        var baseDir = AppContext.BaseDirectory;
        // 1. Production layout: service is in {app}\service, agent is in {app}\agent
        var path1 = Path.GetFullPath(Path.Combine(baseDir, "..", "agent", "ParentalControl.Agent.exe"));
        if (File.Exists(path1)) return path1;

        // 2. Same directory (tests or flat build)
        var path2 = Path.Combine(baseDir, "ParentalControl.Agent.exe");
        if (File.Exists(path2)) return path2;

        // 3. Standard Program Files installation path
        var path3 = @"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe";
        if (File.Exists(path3)) return path3;

        return null;
    }

    /// <summary>
    /// Counts the elapsed time of each unlocked session into the <see cref="UsageLedger"/> (control-clock D3,
    /// storage-failure D6). The minutes come from the monotonic clock; the day from the Control Time Zone. A failed
    /// write never throws here: the minutes stay in the Pending Usage.
    /// </summary>
    public void TickAllSessions()
    {
        var nowUtc = DateTime.UtcNow;
        var nowMono = Environment.TickCount64;
        var today = ControlTime.Current.Today;
        foreach (var (sessionId, session) in _activeSessions)
        {
            if (session.IsLocked)
            {
                // Screen is locked — do not deduct or consume daily limits
                _activeSessions[sessionId] = session.Stamped();
                continue;
            }

            var elapsed = ElapsedTime.Measure(session.LastTickUtc, session.LastTickMono, nowUtc, nowMono);
            if (elapsed.ClockBack)
            {
                _logger.Warning("Clock manipulation detected for {Username}: current UTC time {Now:o} is before last tick {LastTick:o}",
                    session.Username, nowUtc, session.LastTickUtc);

                var clockUser = UserRepository.GetBySid(session.UserSid);
                if (clockUser is not null && clockUser.IsRestricted)
                {
                    var limit = LimitRepository.GetByUserId(clockUser.Id);
                    if (limit is not null)
                    {
                        // Lock before the writes (storage-failure). A Lock Deferral keeps the session tracked: the
                        // Allowance Time check of the tick at the end of the deferral locks it.
                        if (_locker.Lock(sessionId, "clock went backwards"))
                            _activeSessions.TryRemove(sessionId, out _);
                        else
                            _activeSessions[sessionId] = session.Stamped();
                        _ledger.AddAllowance(clockUser.Id, today, limit.DailyMinutes);
                        LogEvent(session.UserSid, EventType.CLOCK_TAMPER,
                            $"Clock went backwards: {session.LastTickUtc:HH:mm:ss} UTC → {nowUtc:HH:mm:ss} UTC. Locked for today.");
                        continue;
                    }
                }

                LogEvent(session.UserSid, EventType.CLOCK_TAMPER,
                    $"Clock went backwards: {session.LastTickUtc:HH:mm:ss} UTC → {nowUtc:HH:mm:ss} UTC");
                _activeSessions[sessionId] = session.Stamped();
                continue;
            }

            if (elapsed.RealMinutes < 1) continue;

            var user = UserRepository.GetBySid(session.UserSid);
            if (user is null) continue;

            if (IsInBreak(user))
            {
                // A Break uses no Allowance Time, also if the session shows as unlocked before the tick locks it again.
                _activeSessions[sessionId] = session.Stamped();
                continue;
            }

            if (elapsed.Gap)
            {
                _logger.Warning("Time gap detected ({Elapsed} min) for {Username} — possible undetected sleep/hibernate",
                    elapsed.RealMinutes, session.Username);
                LogEvent(session.UserSid, EventType.SLEEP, $"Detected retroactively (~{elapsed.RealMinutes} min gap)");
                LogEvent(session.UserSid, EventType.WAKE, $"Detected retroactively (~{elapsed.RealMinutes} min gap)");
            }

            _ledger.AddAllowance(user.Id, today, elapsed.Minutes);
            CountBreakMinutes(user, elapsed.Minutes);
            _activeSessions[sessionId] = session.Stamped();
        }
    }

    /// <summary>Counts the time of a session up to now, before a lock, a sign-out or a sleep. Never throws on a write.</summary>
    private void FlushSessionTime(ActiveSession session)
    {
        if (session.IsLocked) return;
        var elapsed = ElapsedTime.Measure(session.LastTickUtc, session.LastTickMono, DateTime.UtcNow, Environment.TickCount64);
        if (elapsed.ClockBack || elapsed.RealMinutes < 1) return;

        var user = UserRepository.GetBySid(session.UserSid);
        if (user is null) return;
        if (IsInBreak(user)) return;

        _ledger.AddAllowance(user.Id, ControlTime.Current.Today, elapsed.Minutes);
        CountBreakMinutes(user, elapsed.Minutes);
    }
}
