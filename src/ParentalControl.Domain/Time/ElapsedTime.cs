namespace ParentalControl.Domain.Time;

/// <summary>The result of one measure of elapsed session time.</summary>
/// <param name="Minutes">The minutes to add to the Allowance Time (0 if <see cref="ClockBack"/>).</param>
/// <param name="ClockBack">The UTC clock went back by more than the tolerance since the last tick.</param>
/// <param name="Gap">More than 2 minutes passed (sleep or hibernation that the service did not see): only 1 minute counts.</param>
/// <param name="RealMinutes">The whole minutes of the monotonic clock.</param>
public readonly record struct ElapsedResult(int Minutes, bool ClockBack, bool Gap, int RealMinutes);

/// <summary>
/// The elapsed time of a session (design D3). The minutes come from the monotonic clock
/// (<c>Environment.TickCount64</c>). The check "clock went back" compares UTC times. The local time and the time zone
/// have no effect, so the end of daylight saving time adds no hour.
/// </summary>
public static class ElapsedTime
{
    /// <summary>A UTC step back of up to this length is a clock correction, not tampering (the same as the watchdog).</summary>
    public static readonly TimeSpan BackTolerance = TimeSpan.FromSeconds(60);

    public const int GapMinutes = 2;

    public static ElapsedResult Measure(DateTime lastUtc, long lastMonoMs, DateTime nowUtc, long nowMonoMs)
    {
        if (lastUtc - nowUtc > BackTolerance)
            return new ElapsedResult(0, ClockBack: true, Gap: false, RealMinutes: 0);

        var monoMs = Math.Max(0, nowMonoMs - lastMonoMs);
        var minutes = (int)(monoMs / 60_000);
        if (minutes > GapMinutes)
            return new ElapsedResult(1, false, Gap: true, RealMinutes: minutes);
        return new ElapsedResult(minutes, false, false, minutes);
    }

    /// <summary>The whole seconds of the monotonic clock since the last tick, at least 0.</summary>
    public static int Seconds(long lastMonoMs, long nowMonoMs) => (int)(Math.Max(0, nowMonoMs - lastMonoMs) / 1000);
}
