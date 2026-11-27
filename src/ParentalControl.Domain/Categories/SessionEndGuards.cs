namespace ParentalControl.Domain.Categories;

/// <summary>A Session End Guard that started: the session, the user and the processes (pid and start time) that it covers.</summary>
/// <param name="Repeated">
/// True if the user had an unconfirmed guard in the last <see cref="SessionEndGuards.RepeatWindow"/>: the child signs out
/// and cancels again and again. The service reports it to the parent. A repeated guard covers the same processes.
/// </param>
public sealed record StartedGuard(int SessionId, int UserId, DateTime StartUtc,
    IReadOnlyList<(int Pid, DateTime StartTimeUtc)> Covered, bool Repeated)
{
    public DateTime EndUtc => StartUtc + SessionEndGuards.Duration;
}

/// <summary>The result of <see cref="SessionEndGuards.Start"/>.</summary>
public enum GuardStartOutcome
{
    /// <summary>A new guard covers at least one process.</summary>
    Started,
    /// <summary>A guard of the session runs. The request does not extend it and adds no process to it.</summary>
    AlreadyRunning,
    /// <summary>No process of the session is eligible. No guard starts, so a later request can still start one.</summary>
    NothingToCover,
}

public sealed record GuardStart(GuardStartOutcome Outcome, StartedGuard? Guard);

/// <summary>A guard whose time is over. <paramref name="Confirmed"/> is true if its session did not exist any more.</summary>
public sealed record ExpiredGuard(StartedGuard Guard, bool Confirmed);

/// <summary>
/// The Session End Guards of the service (issue #47). The service refuses a session-end request of the Session Agent
/// after an unconfirmed Session End Hold, but the agent already resumed the Suspended Apps for the end of the session.
/// A guard keeps these processes running for <see cref="Duration"/>, so that a tick does not freeze them while Windows
/// waits for them.
/// <para>
/// A guard covers only an eligible process: a recorded process that the service suspended, or saw suspended while no
/// guard covered it, and that the service did not resume since. Covering a process makes it not eligible until the
/// service suspends it, or sees it suspended, again. So a request alone gains nothing, and a chain of requests cannot
/// keep a process running.
/// </para>
/// <para>
/// A guard that ends by its time while its session still exists is unconfirmed (the sign-out did not complete). After
/// an unconfirmed guard of the user in the last <see cref="RepeatWindow"/>, the next guard is repeated, and the service
/// alerts the parent. A completed sign-out never counts. The guard does not lock the session: a lock (a disconnect) in
/// the first second of a sign-out makes Windows end a program with unsaved data (VM 102, review of PR #49).
/// </para>
/// The pid and the start time identify a process. The class is not thread-safe: the caller holds a lock.
/// </summary>
public sealed class SessionEndGuards
{
    /// <summary>The length of a guard, the same as a Session End Hold.</summary>
    public static readonly TimeSpan Duration = SessionEndHolds.HoldDuration;

    /// <summary>After an unconfirmed guard, each guard of the user in this time is repeated. The same as the hold limit.</summary>
    public static readonly TimeSpan RepeatWindow = SessionEndHolds.UnconfirmedWindow;

    private readonly HashSet<(int Pid, DateTime StartTimeUtc)> _eligible = new();
    // Guards that did not end yet: active ones and ones whose time is over but that Expire did not process yet.
    private readonly List<StartedGuard> _guards = new();
    // User id -> start times of the unconfirmed guards in the window.
    private readonly Dictionary<int, List<DateTime>> _unconfirmedStarts = new();

    /// <summary>The service suspended the process. A later guard can cover it.</summary>
    public void MarkSuspended(int pid, DateTime startTimeUtc) => _eligible.Add((pid, startTimeUtc));

    /// <summary>The service resumed the process itself. No guard covers it until the service suspends it again.</summary>
    public void MarkResumed(int pid, DateTime startTimeUtc) => _eligible.Remove((pid, startTimeUtc));

    /// <summary>
    /// A tick saw the processes of <paramref name="suspendedKeys"/> suspended (all threads). Each recorded process of
    /// <paramref name="recordKeys"/> among them that no active guard covers at <paramref name="nowUtc"/> becomes eligible:
    /// for example one that stayed suspended under a guard because the agent could not resume it, or one that the
    /// service suspended before a restart.
    /// </summary>
    public void Observe(IEnumerable<(int Pid, DateTime StartTimeUtc)> recordKeys,
        IReadOnlySet<(int Pid, DateTime StartTimeUtc)> suspendedKeys, DateTime nowUtc)
    {
        var covered = Covered(nowUtc);
        foreach (var key in recordKeys)
        {
            if (suspendedKeys.Contains(key) && !covered.Contains(key))
                _eligible.Add(key);
        }
    }

    /// <summary>
    /// Starts a guard for the session of <paramref name="userId"/> after a refused session-end request. It covers each
    /// eligible process of <paramref name="sessionProcesses"/> and makes it not eligible. A request while a guard of the
    /// session runs does not extend it. Without an eligible process no guard starts.
    /// </summary>
    public GuardStart Start(int sessionId, int userId, IEnumerable<(int Pid, DateTime StartTimeUtc)> sessionProcesses, DateTime nowUtc)
    {
        if (_guards.Any(g => g.SessionId == sessionId && nowUtc < g.EndUtc))
            return new GuardStart(GuardStartOutcome.AlreadyRunning, null);

        var covered = sessionProcesses.Distinct().Where(_eligible.Contains).ToList();
        if (covered.Count == 0)
            return new GuardStart(GuardStartOutcome.NothingToCover, null);

        foreach (var key in covered)
            _eligible.Remove(key);

        var guard = new StartedGuard(sessionId, userId, nowUtc, covered, HasOpenOrUnconfirmedGuard(userId, nowUtc));
        _guards.Add(guard);
        return new GuardStart(GuardStartOutcome.Started, guard);
    }

    /// <summary>The processes that an active guard covers. The planner does not suspend them again.</summary>
    public IReadOnlySet<(int Pid, DateTime StartTimeUtc)> Covered(DateTime nowUtc) =>
        _guards.Where(g => nowUtc < g.EndUtc).SelectMany(g => g.Covered).ToHashSet();

    /// <summary>The earliest end of a guard that <see cref="Expire"/> did not process yet, or null.</summary>
    public DateTime? NextEndUtc => _guards.Count == 0 ? null : _guards.Min(g => g.EndUtc);

    /// <summary>
    /// Ends each guard whose time is over. If its session still exists, the guard is unconfirmed: the next guard of the
    /// user in <see cref="RepeatWindow"/> is repeated. If the session does not exist any more, the guard is confirmed.
    /// </summary>
    public IReadOnlyList<ExpiredGuard> Expire(DateTime nowUtc, Func<int, bool> sessionExists)
    {
        var result = new List<ExpiredGuard>();
        foreach (var guard in _guards.Where(g => g.EndUtc <= nowUtc).OrderBy(g => g.StartUtc).ToList())
        {
            _guards.Remove(guard);
            var confirmed = !sessionExists(guard.SessionId);
            if (!confirmed)
            {
                if (!_unconfirmedStarts.TryGetValue(guard.UserId, out var starts))
                    _unconfirmedStarts[guard.UserId] = starts = new List<DateTime>();
                starts.Add(guard.StartUtc);
            }
            result.Add(new ExpiredGuard(guard, confirmed));
        }
        return result;
    }

    /// <summary>The session ended: its guards end as confirmed and do not count. Returns true if a guard ended.</summary>
    public bool SessionEnded(int sessionId) => _guards.RemoveAll(g => g.SessionId == sessionId) > 0;

    /// <summary>Forgets each eligible process that is not in <paramref name="liveProcesses"/> (its record was dropped or deleted).</summary>
    public void Retain(IReadOnlySet<(int Pid, DateTime StartTimeUtc)> liveProcesses) =>
        _eligible.RemoveWhere(key => !liveProcesses.Contains(key));

    /// <summary>Tells if a guard can cover the process now.</summary>
    public bool IsEligible(int pid, DateTime startTimeUtc) => _eligible.Contains((pid, startTimeUtc));

    // A guard of the user that did not end yet (of another session, or over but not expired) can still end without
    // the end of its session. So it counts like an unconfirmed guard.
    private bool HasOpenOrUnconfirmedGuard(int userId, DateTime nowUtc)
    {
        if (_guards.Any(g => g.UserId == userId))
            return true;
        if (!_unconfirmedStarts.TryGetValue(userId, out var starts))
            return false;
        starts.RemoveAll(s => s <= nowUtc - RepeatWindow);
        return starts.Count > 0;
    }
}
