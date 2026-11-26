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
}
