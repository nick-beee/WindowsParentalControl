namespace ParentalControl.Domain.Categories;

/// <summary>A Session End Guard that started: the session and the processes (pid and start time) that it covers.</summary>
public sealed record StartedGuard(int SessionId, DateTime StartUtc, IReadOnlyList<(int Pid, DateTime StartTimeUtc)> Covered)
{
    public DateTime EndUtc => StartUtc + SessionEndGuards.Duration;
}

/// <summary>
/// The Session End Guards of the service (issue #47). The service refuses a session-end request of the Session Agent
/// after an unconfirmed Session End Hold, but the agent already resumed the Suspended Apps for the end of the session.
/// A guard keeps these processes running for <see cref="Duration"/>, so that a tick does not freeze them while Windows
/// waits for them. A guard covers only an eligible process: one that the service suspended itself (or saw suspended
/// outside a guard) and did not resume since. Covering a process makes it not eligible until the service suspends it,
/// or sees it suspended, again. So a request alone gains nothing, and a chain of requests cannot keep a process running.
/// The pid and the start time identify a process. The class is not thread-safe: the caller holds a lock.
/// </summary>
public sealed class SessionEndGuards
{
    /// <summary>The length of a guard, the same as a Session End Hold.</summary>
    public static readonly TimeSpan Duration = SessionEndHolds.HoldDuration;

    private readonly HashSet<(int Pid, DateTime StartTimeUtc)> _eligible = new();
    private readonly List<StartedGuard> _guards = new();

    /// <summary>The service suspended the process, or saw it suspended outside a guard. A later guard can cover it.</summary>
    public void MarkSuspended(int pid, DateTime startTimeUtc) => _eligible.Add((pid, startTimeUtc));

    /// <summary>The service resumed the process itself. No guard covers it until the service suspends it again.</summary>
    public void MarkResumed(int pid, DateTime startTimeUtc) => _eligible.Remove((pid, startTimeUtc));

    /// <summary>
    /// Starts a guard for the session after a refused session-end request. It covers each eligible process of
    /// <paramref name="sessionProcesses"/> and makes it not eligible. Returns null if a guard of the session still
    /// runs: a request does not extend a guard and adds no process to it.
    /// </summary>
    public StartedGuard? Start(int sessionId, IEnumerable<(int Pid, DateTime StartTimeUtc)> sessionProcesses, DateTime nowUtc)
    {
        if (_guards.Any(g => g.SessionId == sessionId && nowUtc < g.EndUtc))
            return null;

        var covered = sessionProcesses.Distinct().Where(_eligible.Contains).ToList();
        foreach (var key in covered)
            _eligible.Remove(key);

        var guard = new StartedGuard(sessionId, nowUtc, covered);
        _guards.Add(guard);
        return guard;
    }

    /// <summary>The processes that an active guard covers. The planner does not suspend them again.</summary>
    public IReadOnlySet<(int Pid, DateTime StartTimeUtc)> Covered(DateTime nowUtc) =>
        _guards.Where(g => nowUtc < g.EndUtc).SelectMany(g => g.Covered).ToHashSet();

    /// <summary>The earliest end of a guard that <see cref="Expire"/> did not process yet, or null.</summary>
    public DateTime? NextEndUtc => _guards.Count == 0 ? null : _guards.Min(g => g.EndUtc);

    /// <summary>Ends each guard whose time is over and returns them.</summary>
    public IReadOnlyList<StartedGuard> Expire(DateTime nowUtc)
    {
        var ended = _guards.Where(g => g.EndUtc <= nowUtc).ToList();
        foreach (var guard in ended)
            _guards.Remove(guard);
        return ended;
    }

    /// <summary>The session ended: its guards end. Returns true if a guard ended.</summary>
    public bool SessionEnded(int sessionId) => _guards.RemoveAll(g => g.SessionId == sessionId) > 0;

    /// <summary>Forgets each eligible process that is not in <paramref name="liveProcesses"/> (its record was dropped or deleted).</summary>
    public void Retain(IReadOnlySet<(int Pid, DateTime StartTimeUtc)> liveProcesses) =>
        _eligible.RemoveWhere(key => !liveProcesses.Contains(key));

    /// <summary>Tells if a guard can cover the process now.</summary>
    public bool IsEligible(int pid, DateTime startTimeUtc) => _eligible.Contains((pid, startTimeUtc));
}
