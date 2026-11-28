namespace ParentalControl.Domain.Sessions;

/// <summary>
/// Limits a warning that repeats at each tick, for example a failed start of the Session Agent because
/// <c>CreateEnvironmentBlock</c> fails: one log line per key and <see cref="Interval"/>, with the number of the
/// warnings that it left out since the last line. Monotonic time only. Thread-safe.
/// </summary>
public sealed class WarningLimit
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (TimeSpan LastLog, int Suppressed)> _byKey = new(StringComparer.Ordinal);

    public WarningLimit(TimeSpan? interval = null) => Interval = interval ?? DefaultInterval;

    public TimeSpan Interval { get; }

    /// <summary>
    /// True if the caller must log the warning now. <paramref name="suppressed"/> is the number of warnings of the
    /// key that the limit left out since its last log line.
    /// </summary>
    public bool Take(string key, TimeSpan monotonicNow, out int suppressed)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var entry) && monotonicNow - entry.LastLog < Interval)
            {
                _byKey[key] = (entry.LastLog, entry.Suppressed + 1);
                suppressed = 0;
                return false;
            }
            suppressed = entry.Suppressed;
            _byKey[key] = (monotonicNow, 0);
            return true;
        }
    }
}
