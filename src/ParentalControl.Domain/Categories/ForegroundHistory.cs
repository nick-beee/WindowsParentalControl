namespace ParentalControl.Domain.Categories;

/// <summary>
/// The exe paths that were a Foreground Window of each Restricted User on the current day: the distinct exes of the
/// Read entries of the Session Probe samples (session-probe-and-agent-channel, design D5). Only these exes are suspend
/// candidates. The set is in memory: it is empty after a service start. The set is thread-safe.
/// <para>
/// It holds at most <see cref="MaxPathsPerUser"/> paths per user and day as a bound on memory. A child needs 60,000
/// different exe files, each seen in a sample about every 5 seconds, to fill it. A full set fails closed (N14):
/// <see cref="Record"/> returns false for a new path, and the service applies the Enforced Workstation Lock, because
/// that exe is not a suspend candidate.
/// </para>
/// </summary>
public sealed class ForegroundHistory
{
    public const int MaxPathsPerUser = 60_000;

    private readonly object _gate = new();
    private readonly Dictionary<int, (DateOnly Date, HashSet<string> Paths)> _byUser = new();

    /// <summary>Records a path. A path that is not absolute is ignored. Returns true if the path is in the set now.</summary>
    public bool Record(int userId, DateOnly date, string? exePath)
    {
        var canonical = WindowsPath.Canonicalize(exePath);
        if (canonical is null)
            return false;

        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var entry) || entry.Date != date)
            {
                entry = (date, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                _byUser[userId] = entry;
            }

            if (entry.Paths.Count >= MaxPathsPerUser && !entry.Paths.Contains(canonical))
                return false;
            entry.Paths.Add(canonical);
            return true;
        }
    }

    public bool WasForeground(int userId, DateOnly date, string? exePath)
    {
        var canonical = WindowsPath.Canonicalize(exePath);
        if (canonical is null)
            return false;

        lock (_gate)
            return _byUser.TryGetValue(userId, out var entry) && entry.Date == date && entry.Paths.Contains(canonical);
    }
}
