namespace ParentalControl.Service;

/// <summary>
/// The write failures of the service (harden-clock-and-disk, design D7). Each write path reports a failure here.
/// It records failures only: a success of a route that a process of the child can call never resets the count.
/// <see cref="UsageMonitorWorker"/> takes the count once per tick for the Storage Lockdown.
/// </summary>
public sealed class StorageHealth
{
    /// <summary>The one instance of the service.</summary>
    public static StorageHealth Shared { get; } = new();

    private int _failures;

    public void ReportFailure() => Interlocked.Increment(ref _failures);

    /// <summary>The failures since the last call.</summary>
    public int TakeFailures() => Interlocked.Exchange(ref _failures, 0);

    /// <summary>
    /// Runs a database write. On an error, it logs it, reports it, and returns false. It never throws, so a failed
    /// write never stops a lock or a limit check.
    /// </summary>
    public bool TryWrite(Action write, string what, Serilog.ILogger logger)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure();
            logger.Error(ex, "Database write failed: {What}", what);
            return false;
        }
    }
}
