namespace ParentalControl.Domain.Categories;

/// <summary>
/// The exe paths that were a Foreground Window of each Restricted User on the current day.
/// Only these exes are suspend candidates. The set is in memory: it is empty after a service start.
/// The set is thread-safe. It holds at most <see cref="MaxPathsPerUser"/> paths per user and day,
/// because the paths come from the Session Agent without authentication.
/// </summary>
public sealed class ForegroundHistory
{
    public const int MaxPathsPerUser = 1000;

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
