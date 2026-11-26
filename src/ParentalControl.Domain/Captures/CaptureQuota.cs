namespace ParentalControl.Domain.Captures;

public enum CaptureQuotaOutcome
{
    Allow,
    /// <summary>The user has <see cref="CaptureQuota.MaxFilesPerDay"/> captures today.</summary>
    RefuseFiles,
    /// <summary>The captures of the user today have <see cref="CaptureQuota.MaxBytesPerDay"/> bytes or more.</summary>
    RefuseBytes,
}

/// <param name="MaxBytes">For <see cref="CaptureQuotaOutcome.Allow"/>: the largest body that the route accepts now.</param>
public sealed record CaptureQuotaDecision(CaptureQuotaOutcome Outcome, long MaxBytes)
{
    public bool Allowed => Outcome == CaptureQuotaOutcome.Allow;
}

/// <summary>
/// The daily quota of the capture route of the Session Agent (require-admin-sign-in, CB1 d). The route needs no
/// sign-in (issue #6), so the child can send many files. Each file makes the walk through the data folder at the
/// service start longer, and each byte fills the disk. The quota counts the captures of one user since local midnight,
/// from the database, so a restart does not reset it.
/// </summary>
public static class CaptureQuota
{
    /// <summary>The Session Agent sends a capture when the parent asks for one, or in watch mode.</summary>
    public const int MaxFilesPerDay = 200;
    public const long MaxBytesPerDay = 50L * 1024 * 1024;
    /// <summary>A JPEG of 1920 pixels at quality 70 has less than 1 MB.</summary>
    public const long MaxBytesPerCapture = 5L * 1024 * 1024;

    /// <param name="filesToday">The captures of the user since local midnight.</param>
    /// <param name="bytesToday">The bytes of these captures.</param>
    public static CaptureQuotaDecision Check(int filesToday, long bytesToday)
    {
        if (filesToday >= MaxFilesPerDay)
            return new CaptureQuotaDecision(CaptureQuotaOutcome.RefuseFiles, 0);
        if (bytesToday >= MaxBytesPerDay)
            return new CaptureQuotaDecision(CaptureQuotaOutcome.RefuseBytes, 0);
        return new CaptureQuotaDecision(CaptureQuotaOutcome.Allow, Math.Min(MaxBytesPerCapture, MaxBytesPerDay - bytesToday));
    }
}

/// <summary>
/// Writes a refusal of the quota to the log only one time per user and day, so a flood of requests does not fill
/// the disk with log lines.
/// </summary>
public sealed class CaptureRefusalLog
{
    private readonly Dictionary<int, (DateOnly Day, int Count)> _refusals = new();
    private readonly object _gate = new();

    /// <summary>Counts one refusal. Returns true for the first refusal of the user on <paramref name="today"/>.</summary>
    public bool Count(int userId, DateOnly today)
    {
        lock (_gate)
        {
            if (_refusals.TryGetValue(userId, out var entry) && entry.Day == today)
            {
                _refusals[userId] = (today, entry.Count + 1);
                return false;
            }
            _refusals[userId] = (today, 1);
            return true;
        }
    }

    public int RefusalsToday(int userId, DateOnly today)
    {
        lock (_gate)
            return _refusals.TryGetValue(userId, out var entry) && entry.Day == today ? entry.Count : 0;
    }
}
