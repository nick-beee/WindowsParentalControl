using System.Security.Cryptography;

namespace ParentalControl.Domain.AdminAccess;

/// <summary>
/// The Admin Sessions of the Remote Admin Interface, in memory only: a restart of the service ends them all.
/// A session is valid while its age is less than <see cref="Lifetime"/> and its epoch equals the current
/// session epoch. The Admin app increments the epoch to end all sessions. The store is thread-safe and
/// holds at most <see cref="MaxSessions"/> sessions; a new session removes the oldest one first.
/// </summary>
public sealed class AdminSessionStore
{
    public const int MaxSessions = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset IssuedAt, long Epoch)> _sessions = new(StringComparer.Ordinal);

    public AdminSessionStore(TimeProvider time)
    {
        _time = time;
    }

    public int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>Starts a session for <paramref name="epoch"/> and returns its token (32 random bytes as hex).</summary>
    public string Issue(long epoch)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var expired in _sessions.Where(s => !IsYoung(s.Value.IssuedAt, now)).Select(s => s.Key).ToList())
                _sessions.Remove(expired);

            while (_sessions.Count >= MaxSessions)
            {
                var oldest = _sessions.MinBy(s => s.Value.IssuedAt).Key;
                _sessions.Remove(oldest);
            }
            _sessions[token] = (now, epoch);
        }
        return token;
    }

    public bool IsValid(string? token, long currentEpoch)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var session))
                return false;
            if (IsYoung(session.IssuedAt, now) && session.Epoch == currentEpoch)
                return true;
            _sessions.Remove(token);
            return false;
        }
    }

    public void End(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        lock (_gate)
            _sessions.Remove(token);
    }

    private static bool IsYoung(DateTimeOffset issuedAt, DateTimeOffset now) => now - issuedAt < Lifetime;
}
