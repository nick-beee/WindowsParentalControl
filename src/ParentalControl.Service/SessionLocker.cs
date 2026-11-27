using ParentalControl.Core.Platform;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Service;

/// <summary>
/// The Enforced Workstation Lock of the limits, the Breaks, the suspension fallback and the Storage Lockdown, with the Lock Deferral
/// (issue #50, change <c>defer-lock-during-session-end</c>). A lock in the first seconds of a sign-out, a restart or a
/// shutdown makes Windows end a program before it can ask to save. When the Session Agent reports the end of the
/// session, the locker does not lock that session for <see cref="LockDeferrals.Duration"/>. A lock that becomes due in
/// this time is owed: at the end of the deferral, <see cref="OwedLockDue"/> makes the usage worker check the session
/// again, so a lock runs only if it is still due.
/// <para>
/// The check and the call of <see cref="SessionManager.LockSession"/> run in one lock, so a session-end request cannot
/// come between them. The locker never calls into <see cref="CategoryEnforcer"/>: its fallback calls the locker inside
/// the lock of the enforcer. The delayed lock of the parent (<see cref="LockForParentAsync"/>) waits for the end of a
/// deferral and locks then. The Data Folder Lockdown before the start gate and the Telegram lock do not use the locker.
/// </para>
/// <para>
/// The time of a deferral is monotonic (<see cref="Environment.TickCount64"/>), so a change of the system clock does not
/// make it longer. The state is in memory only: a restart of the service ends each deferral and resets the window.
/// </para>
/// </summary>
public sealed class SessionLocker : IDisposable
{
    // The timer comes this much after the end of a deferral, so that it never comes a few milliseconds early.
    private static readonly TimeSpan TimerMargin = TimeSpan.FromSeconds(0.5);

    private readonly object _gate = new();
    private readonly LockDeferrals _deferrals = new();
    private readonly Timer _timer;
    private readonly Serilog.ILogger _logger;
    private readonly Func<DateTime> _clock;
    private Func<int, bool> _sessionExists = _ => true;

    public SessionLocker(Serilog.ILogger logger) : this(logger, MonotonicNow) { }

    /// <param name="clock">A monotonic time; a test gives its own clock.</param>
    internal SessionLocker(Serilog.ILogger logger, Func<DateTime> clock)
    {
        _logger = logger;
        _clock = clock;
        _timer = new Timer(_ => OnTimer());
    }

    /// <summary>The time since the start of Windows as a <see cref="DateTime"/>. It does not follow the system clock.</summary>
    internal static DateTime MonotonicNow() => DateTime.MinValue.AddMilliseconds(Environment.TickCount64);

    /// <summary>The number of retries of a delayed parent lock after a Lock Deferral.</summary>
    internal const int ParentLockRetries = 3;

    /// <summary>
    /// A deferral that owed a lock ended by its time, and its session still exists. The argument is the session id.
    /// The handler checks the locks of the session again. It runs on a pool thread, outside the lock of the locker.
    /// </summary>
    public event Action<int>? OwedLockDue;

    /// <summary>Connects the session list of the tracker. <see cref="SessionTracker"/> calls it once.</summary>
    public void TrackSessions(Func<int, bool> sessionExists) => _sessionExists = sessionExists;

    /// <summary>
    /// The session of a Restricted User ends: a request of the Session Agent (not trusted) or a Forced Logoff (trusted).
    /// Starts a Lock Deferral if the limit permits it. Call it before the slow work of the session end.
    /// </summary>
    public DeferralRequestResult OnSessionEnding(int sessionId, int userId, string username, bool trusted, string reason)
    {
        lock (_gate)
        {
            var result = _deferrals.Request(sessionId, userId, _clock(), trusted);
            switch (result)
            {
                case DeferralRequestResult.Granted:
                    _logger.Information("Lock Deferral started for {Username} (session {SessionId}, {Reason}, trusted {Trusted}) for {Seconds} s",
                        username, sessionId, reason, trusted, LockDeferrals.Duration.TotalSeconds);
                    break;
                case DeferralRequestResult.AlreadyDeferred:
                    _logger.Information("Lock Deferral of {Username} (session {SessionId}, {Reason}) runs already: not extended",
                        username, sessionId, reason);
                    break;
                default:
                    _logger.Warning("Lock Deferral refused for {Username} (session {SessionId}, {Reason}): a deferral that delayed a lock ended without the end of the session in the last {Minutes} min",
                        username, sessionId, reason, LockDeferrals.UnconfirmedWindow.TotalMinutes);
                    break;
            }
            Schedule();
            return result;
        }
    }

    /// <summary>
    /// A lock of the session is due. Returns true if a Lock Deferral stops it; then the caller sends no message, writes
    /// no event and does not mark the session as locked. The deferral owes a lock after this call.
    /// </summary>
    public bool ShouldDefer(int sessionId, string reason)
    {
        lock (_gate)
            return Defer(sessionId, reason);
    }

    /// <summary>
    /// Applies the Enforced Workstation Lock (<see cref="SessionManager.LockSession"/>) unless a Lock Deferral stops it.
    /// Returns false for a deferred lock: the caller does not call <see cref="SessionTracker.OnSessionLock"/>.
    /// </summary>
    public bool Lock(int sessionId, string reason)
    {
        lock (_gate)
        {
            if (Defer(sessionId, reason))
                return false;
            SessionManager.LockSession(sessionId);
            return true;
        }
    }

    /// <summary>
    /// The delayed lock of the parent (the admin page, after its countdown). The parent is trusted, but the lock must not
    /// come in the first seconds of a sign-out either: the child can start a sign-out during the countdown to save the
    /// work. During a Lock Deferral it waits until the deferral ends and tries again, at most
    /// <see cref="ParentLockRetries"/> times. A deferred parent lock owes a lock like each other lock, so the deferral
    /// counts against the limit, and a new untrusted request after it is refused. Returns true when the session locked.
    /// </summary>
    /// <param name="sessionTracked">True while the session exists; a session that ended needs no lock.</param>
    /// <param name="wait">The wait; a test gives its own.</param>
    public async Task<bool> LockForParentAsync(int sessionId, string reason, Func<bool> sessionTracked,
        Func<TimeSpan, Task>? wait = null)
    {
        wait ??= delay => Task.Delay(delay);
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? remaining;
            lock (_gate)
            {
                if (!Defer(sessionId, reason))
                {
                    SessionManager.LockSession(sessionId);
                    return true;
                }
                var now = _clock();
                remaining = _deferrals.ActiveEnd(sessionId, now) is { } end ? end - now : null;
            }

            if (attempt >= ParentLockRetries)
            {
                _logger.Warning("Lock of the parent for session {SessionId} not applied: a Lock Deferral stopped it {Count} times", sessionId, attempt + 1);
                return false;
            }

            await wait((remaining ?? TimeSpan.Zero) + TimerMargin);
            if (!sessionTracked())
            {
                _logger.Information("Lock of the parent for session {SessionId} not needed: the session ended during the Lock Deferral", sessionId);
                return false;
            }
        }
    }

    /// <summary>The session ended: its deferrals end and do not count against the limit.</summary>
    public void OnSessionEnded(int sessionId)
    {
        lock (_gate)
        {
            foreach (var ended in _deferrals.SessionEnded(sessionId))
                _logger.Information("Lock Deferral of session {SessionId} ended with the session (owed a lock: {Owed})", ended.SessionId, ended.Owed);
            Schedule();
        }
    }

    public void Dispose() => _timer.Dispose();

    // Call it with the lock.
    private bool Defer(int sessionId, string reason)
    {
        var now = _clock();
        if (!_deferrals.TryDefer(sessionId, now, out var ends))
            return false;
        _logger.Information("Lock of session {SessionId} deferred for {Seconds:F1} s more: the session ends ({Reason})",
            sessionId, (ends - now).TotalSeconds, reason);
        return true;
    }

    /// <summary>Ends the deferrals whose time is over. The timer calls it; a test calls it directly.</summary>
    internal void OnTimer()
    {
        var due = new List<int>();
        lock (_gate)
        {
            foreach (var ended in _deferrals.Expire(_clock(), _sessionExists))
            {
                if (ended.Confirmed)
                {
                    _logger.Information("Lock Deferral of session {SessionId} ended: the session does not exist any more", ended.SessionId);
                }
                else if (ended.Owed)
                {
                    _logger.Warning("Lock Deferral of session {SessionId} ended without the end of the session: checking the owed lock; the user gets no deferral for {Minutes} min",
                        ended.SessionId, LockDeferrals.UnconfirmedWindow.TotalMinutes);
                    due.Add(ended.SessionId);
                }
                else
                {
                    _logger.Information("Lock Deferral of session {SessionId} ended by its time; no lock was due", ended.SessionId);
                }
            }
            Schedule();
        }

        foreach (var sessionId in due)
        {
            try
            {
                OwedLockDue?.Invoke(sessionId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error while checking the owed lock of session {SessionId}", sessionId);
            }
        }
    }

    // Call it with the lock.
    private void Schedule()
    {
        if (_deferrals.NextEnd is not { } next)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = next - _clock();
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        _timer.Change(delay + TimerMargin, Timeout.InfiniteTimeSpan);
    }
}
