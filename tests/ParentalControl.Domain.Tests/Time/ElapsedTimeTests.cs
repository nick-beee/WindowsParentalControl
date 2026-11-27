using ParentalControl.Domain.Time;
using static ParentalControl.Domain.Tests.Time.TestZones;

namespace ParentalControl.Domain.Tests.Time;

public class ElapsedTimeTests
{
    [Fact]
    public void LocalHourBackAtTheEndOfDaylightSavingTimeAddsOneRealMinute()
    {
        // 00:59:30 UTC is 02:59:30 CEST; 01:00:30 UTC is 02:00:30 CET. The local time goes back, UTC does not.
        var lastUtc = new DateTime(2026, 10, 25, 0, 59, 30, DateTimeKind.Utc);
        var nowUtc = lastUtc.AddSeconds(60);
        var clock = new ControlClock(WEurope, () => nowUtc);
        Assert.True(clock.ToControl(nowUtc) < clock.ToControl(lastUtc));

        var result = ElapsedTime.Measure(lastUtc, 1_000_000, nowUtc, 1_060_000);
        Assert.Equal(new ElapsedResult(1, false, false, 1), result);
    }

    [Fact]
    public void StartOfDaylightSavingTimeIsNoGap()
    {
        var lastUtc = Utc(2026, 3, 29, 0, 59);
        var result = ElapsedTime.Measure(lastUtc, 0, lastUtc.AddMinutes(1), 60_000);
        Assert.Equal(1, result.Minutes);
        Assert.False(result.Gap);
    }

    [Fact]
    public void UtcBeforeTheLastTickIsClockBack()
    {
        var lastUtc = Utc(2026, 11, 26, 12);
        var result = ElapsedTime.Measure(lastUtc, 0, lastUtc.AddHours(-2), 60_000);
        Assert.True(result.ClockBack);
        Assert.Equal(0, result.Minutes);
    }

    [Fact]
    public void SmallCorrectionBackIsNotClockBack()
    {
        var lastUtc = Utc(2026, 11, 26, 12);
        var result = ElapsedTime.Measure(lastUtc, 0, lastUtc.AddSeconds(-5), 60_000);
        Assert.False(result.ClockBack);
        Assert.Equal(1, result.Minutes);
    }

    [Fact]
    public void GapOfMoreThanTwoMinutesAddsOneMinute()
    {
        var lastUtc = Utc(2026, 11, 26, 12);
        var result = ElapsedTime.Measure(lastUtc, 0, lastUtc.AddMinutes(45), 45 * 60_000);
        Assert.Equal(new ElapsedResult(1, false, true, 45), result);
    }

    [Fact]
    public void LessThanAMinuteAddsNothing() =>
        Assert.Equal(0, ElapsedTime.Measure(Utc(2026, 11, 26, 12), 0, Utc(2026, 11, 26, 12), 59_999).Minutes);

    [Fact]
    public void SecondsComeFromTheMonotonicClock() =>
        Assert.Equal(42, ElapsedTime.Seconds(1000, 43_500));
}
