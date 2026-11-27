namespace ParentalControl.Domain.Sessions;

public enum DeferralRequestResult
{
    /// <summary>A new Lock Deferral starts.</summary>
    Granted,
    /// <summary>The session has an active deferral. The request does not extend it.</summary>
    AlreadyDeferred,
    /// <summary>The limit refuses the request. No deferral starts.</summary>
    Refused,
}

/// <summary>A Lock Deferral whose time is over or whose session ended.</summary>
/// <param name="Owed">True if a lock became due during the deferral.</param>
/// <param name="Confirmed">True if the session did not exist any more at the end of the deferral.</param>
public sealed record EndedDeferral(int SessionId, int UserId, DateTime Start, bool Owed, bool Confirmed);

/// <summary>
/// The Lock Deferrals of the service (issue #50). A lock (a disconnect) in the first seconds of a sign-out, a restart or
/// a shutdown makes Windows end a program before it can ask to save. When the Session Agent reports the end of the
/// session, the service does not apply the Enforced Workstation Lock to that session for <see cref="Duration"/>.
/// A lock that becomes due in this time is owed; the service checks it again at the end.
/// <para>
/// The request of the agent has no authentication, so a request during a deferral does not extend it. A deferral that
/// owed a lock and ended by its time while its session still existed is unconfirmed: after it, the user gets no
/// deferral for <see cref="UnconfirmedWindow"/>. A deferral that ended with its session, or that owed no lock, does not
/// count. So a fake request delays a due lock at most once per <see cref="UnconfirmedWindow"/>, and by at most
/// <see cref="Duration"/>. A trusted request (Forced Logoff) is never refused.
/// </para>
/// <para>
/// Each time is a monotonic time stamp, not the wall clock: a change of the system clock must not make a deferral
/// longer or shorter. The service gives the time of <c>Environment.TickCount64</c> as a <see cref="DateTime"/>.
/// </para>
/// The class is not thread-safe: the caller holds a lock.
/// </summary>
public sealed class LockDeferrals
{
    /// <summary>
    /// Windows waits about 65 s for a program that blocks a sign-out, and then cancels the sign-out and locks the
    /// session itself (VM 102, 2026-11-27). The deferral ends after that, with a margin.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(75);

    /// <summary>After an unconfirmed deferral, the user gets no deferral in this time. The same as the hold limit.</summary>
    public static readonly TimeSpan UnconfirmedWindow = TimeSpan.FromMinutes(15);

    private sealed class Deferral(int sessionId, int userId, DateTime start)
    {
        public int SessionId { get; } = sessionId;
        public int UserId { get; } = userId;
        public DateTime Start { get; } = start;
        public DateTime End => Start + Duration;
        public bool Owed { get; set; }
    }

    // Deferrals that did not end yet: active ones and ones whose time is over but that Expire did not process yet.
    private readonly List<Deferral> _deferrals = new();
    // User id -> start times of the unconfirmed deferrals in the window.
    private readonly Dictionary<int, List<DateTime>> _unconfirmedStarts = new();

    /// <summary>Starts a deferral for the session if the limit permits it.</summary>
    public DeferralRequestResult Request(int sessionId, int userId, DateTime now, bool trusted)
    {
        if (_deferrals.Any(d => d.SessionId == sessionId && now < d.End))
            return DeferralRequestResult.AlreadyDeferred;

        if (!trusted && HasOpenOwedOrUnconfirmed(userId, now))
            return DeferralRequestResult.Refused;

        _deferrals.Add(new Deferral(sessionId, userId, now));
        return DeferralRequestResult.Granted;
    }

    /// <summary>
    /// A lock of the session is due. Returns true if an active deferral stops it, and marks that deferral as owing a lock.
    /// </summary>
    public bool TryDefer(int sessionId, DateTime now, out DateTime ends)
    {
        var active = _deferrals.Where(d => d.SessionId == sessionId && now < d.End).MaxBy(d => d.End);
        if (active is null)
        {
            ends = default;
            return false;
        }

        active.Owed = true;
        ends = active.End;
        return true;
    }

    /// <summary>The end of the active deferral of the session, or null. Does not mark it as owing a lock.</summary>
    public DateTime? ActiveEnd(int sessionId, DateTime now) =>
        _deferrals.Where(d => d.SessionId == sessionId && now < d.End).Select(d => (DateTime?)d.End).Max();

    /// <summary>The earliest end of a deferral that <see cref="Expire"/> did not process yet, or null.</summary>
    public DateTime? NextEnd => _deferrals.Count == 0 ? null : _deferrals.Min(d => d.End);

    /// <summary>The session ended: its deferrals end as confirmed and do not count against the limit.</summary>
    public IReadOnlyList<EndedDeferral> SessionEnded(int sessionId)
    {
        var ended = _deferrals.Where(d => d.SessionId == sessionId).ToList();
        _deferrals.RemoveAll(d => d.SessionId == sessionId);
        return ended.Select(d => new EndedDeferral(d.SessionId, d.UserId, d.Start, d.Owed, Confirmed: true)).ToList();
    }

    /// <summary>
    /// Ends each deferral whose time is over. If its session still exists and it owed a lock, it is unconfirmed and counts
    /// against the limit of its user; the caller then checks the locks of the session again.
    /// </summary>
    public IReadOnlyList<EndedDeferral> Expire(DateTime now, Func<int, bool> sessionExists)
    {
        var result = new List<EndedDeferral>();
        foreach (var deferral in _deferrals.Where(d => d.End <= now).OrderBy(d => d.Start).ToList())
        {
            _deferrals.Remove(deferral);
            var confirmed = !sessionExists(deferral.SessionId);
            if (!confirmed && deferral.Owed)
            {
                if (!_unconfirmedStarts.TryGetValue(deferral.UserId, out var starts))
                    _unconfirmedStarts[deferral.UserId] = starts = new List<DateTime>();
                starts.Add(deferral.Start);
            }
            result.Add(new EndedDeferral(deferral.SessionId, deferral.UserId, deferral.Start, deferral.Owed, confirmed));
        }
        return result;
    }

    // A deferral that owes a lock and did not end yet can still end without the end of its session, so it counts like
    // an unconfirmed one. A deferral that owes nothing yet does not count: it delayed no lock.
    private bool HasOpenOwedOrUnconfirmed(int userId, DateTime now)
    {
        if (_deferrals.Any(d => d.UserId == userId && d.Owed))
            return true;
        if (!_unconfirmedStarts.TryGetValue(userId, out var starts))
            return false;
        // now - start, not now - window: a monotonic time can be less than the window after the start of Windows.
        starts.RemoveAll(s => now - s >= UnconfirmedWindow);
        return starts.Count > 0;
    }
}
