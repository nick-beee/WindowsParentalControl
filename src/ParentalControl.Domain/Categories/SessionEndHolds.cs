namespace ParentalControl.Domain.Categories;

public enum HoldRequestResult
{
    /// <summary>A new Session End Hold starts.</summary>
    Granted,
    /// <summary>The session has an active hold. The request does not extend it.</summary>
    AlreadyHeld,
    /// <summary>The rate limit refuses the request. No hold starts.</summary>
    Refused,
}

/// <summary>A Session End Hold whose time is over.</summary>
/// <param name="Confirmed">True if the session did not exist any more at the end of the hold.</param>
/// <param name="Alert">True if the parent gets a Telegram alert for this unconfirmed hold (the first one of the user today).</param>
public sealed record ExpiredHold(int SessionId, int UserId, DateTime StartUtc, bool Trusted, bool Confirmed, bool Alert);

/// <summary>
/// The Session End Holds of the service and their rate limit. During a hold, the service does not suspend a process of
/// the session, because Windows ends the session. The request of the Session Agent has no authentication, so a request
/// that is not trusted is refused while the user has a hold in the last <see cref="UnconfirmedWindow"/> that did not
/// end with the end of its session. A hold ends when the session ends (<see cref="SessionEnded"/>, confirmed) or when
/// its time is over (<see cref="Expire"/>). The class is not thread-safe: the caller holds a lock.
/// </summary>
public sealed class SessionEndHolds
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UnconfirmedWindow = TimeSpan.FromMinutes(15);

    private sealed record Hold(int SessionId, int UserId, DateTime StartUtc, bool Trusted)
    {
        public DateTime EndUtc => StartUtc + HoldDuration;
    }

    // Holds that did not end yet: active ones and ones whose time is over but that Expire did not process yet.
    private readonly List<Hold> _holds = new();
    // User id -> start times of the unconfirmed holds in the window.
    private readonly Dictionary<int, List<DateTime>> _unconfirmedStarts = new();
    // User id -> local date of the last alert.
    private readonly Dictionary<int, DateOnly> _lastAlert = new();

    /// <summary>Starts a hold for the session if the rate limit permits it. A trusted request (Forced Logoff) is never refused.</summary>
    public HoldRequestResult Request(int sessionId, int userId, DateTime nowUtc, bool trusted)
    {
        if (_holds.Any(h => h.SessionId == sessionId && nowUtc < h.EndUtc))
            return HoldRequestResult.AlreadyHeld;

        if (!trusted && HasOpenOrUnconfirmedHold(userId, nowUtc))
            return HoldRequestResult.Refused;

        _holds.Add(new Hold(sessionId, userId, nowUtc, trusted));
        return HoldRequestResult.Granted;
    }

    /// <summary>The session ended: its holds end as confirmed and do not count against the rate limit. Returns true if a hold ended.</summary>
    public bool SessionEnded(int sessionId) => _holds.RemoveAll(h => h.SessionId == sessionId) > 0;

    /// <summary>The sessions with an active hold.</summary>
    public IReadOnlySet<int> HeldSessions(DateTime nowUtc) =>
        _holds.Where(h => nowUtc < h.EndUtc).Select(h => h.SessionId).ToHashSet();

    /// <summary>The earliest end of a hold that <see cref="Expire"/> did not process yet, or null.</summary>
    public DateTime? NextEndUtc => _holds.Count == 0 ? null : _holds.Min(h => h.EndUtc);

    /// <summary>
    /// Ends each hold whose time is over. If its session still exists, the hold is unconfirmed: it counts against the
    /// rate limit, and the first unconfirmed hold of the user on the local date <paramref name="today"/> asks for an alert.
    /// If the session does not exist any more, the hold is confirmed.
    /// </summary>
    public IReadOnlyList<ExpiredHold> Expire(DateTime nowUtc, DateOnly today, Func<int, bool> sessionExists)
    {
        var result = new List<ExpiredHold>();
        foreach (var hold in _holds.Where(h => h.EndUtc <= nowUtc).OrderBy(h => h.StartUtc).ToList())
        {
            _holds.Remove(hold);
            if (!sessionExists(hold.SessionId))
            {
                result.Add(new ExpiredHold(hold.SessionId, hold.UserId, hold.StartUtc, hold.Trusted, Confirmed: true, Alert: false));
                continue;
            }

            if (!_unconfirmedStarts.TryGetValue(hold.UserId, out var starts))
                _unconfirmedStarts[hold.UserId] = starts = new List<DateTime>();
            starts.Add(hold.StartUtc);

            var alert = !_lastAlert.TryGetValue(hold.UserId, out var last) || last != today;
            if (alert)
                _lastAlert[hold.UserId] = today;
            result.Add(new ExpiredHold(hold.SessionId, hold.UserId, hold.StartUtc, hold.Trusted, Confirmed: false, alert));
        }
        return result;
    }

    // A hold that did not end yet (active, or over but not expired) can still end without the end of its session.
    // So it counts like an unconfirmed hold.
    private bool HasOpenOrUnconfirmedHold(int userId, DateTime nowUtc)
    {
        var windowStart = nowUtc - UnconfirmedWindow;
        if (_holds.Any(h => h.UserId == userId))
            return true;
        if (!_unconfirmedStarts.TryGetValue(userId, out var starts))
            return false;
        starts.RemoveAll(s => s <= windowStart);
        return starts.Count > 0;
    }
}
