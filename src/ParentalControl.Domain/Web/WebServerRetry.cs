namespace ParentalControl.Domain.Web;

/// <summary>
/// The wait between two tries to start the web server (require-admin-sign-in, S3). A program of the child can take
/// port 5050 first. The web server then tries again; the limits do not depend on it.
/// </summary>
public static class WebServerRetry
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <param name="failedTries">The number of tries that failed so far (1 after the first failure).</param>
    public static TimeSpan Delay(int failedTries)
    {
        if (failedTries < 1)
            return TimeSpan.Zero;
        var exponent = Math.Min(failedTries - 1, 10);
        var delay = TimeSpan.FromTicks(FirstDelay.Ticks << exponent);
        return delay < MaxDelay ? delay : MaxDelay;
    }

    /// <summary>
    /// True if a process other than the service holds the port (third review, SF3). A failed bind without such a
    /// process, for example a short wait of Windows after an own restart of the web server, is no squat and gives no
    /// alert.
    /// </summary>
    public static bool IsForeignOwner(IEnumerable<int> ownerPids, int servicePid) => ownerPids.Any(pid => pid != servicePid);
}

/// <summary>
/// The alert about a program that holds the port of the web server (third review, SF3). The Telegram settings are in
/// the database, so the alert can go out only after the start gate opened. It goes out one time per squat: the first
/// failed bind with a foreign owner after the gate opened. A successful bind ends the squat.
/// </summary>
public sealed class PortSquatAlert
{
    private bool _squatActive;
    private bool _alerted;

    /// <summary>Records a failed bind. Returns true if the caller must send the alert now.</summary>
    public bool OnBindFailed(bool foreignOwner, bool gateOpen)
    {
        if (!foreignOwner)
            return false;
        _squatActive = true;
        if (!gateOpen || _alerted)
            return false;
        _alerted = true;
        return true;
    }

    /// <summary>Records a successful bind. Returns true if a squat ended, so the caller clears the warning.</summary>
    public bool OnBound()
    {
        var ended = _squatActive;
        _squatActive = false;
        _alerted = false;
        return ended;
    }
}
