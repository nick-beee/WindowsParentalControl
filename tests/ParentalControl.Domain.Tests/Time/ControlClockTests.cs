using ParentalControl.Domain.Time;
using static ParentalControl.Domain.Tests.Time.TestZones;

namespace ParentalControl.Domain.Tests.Time;

public class ControlClockTests
{
    private static ControlClock At(DateTime utc, TimeZoneInfo? zone = null) => new(zone ?? WEurope, () => utc);

    [Fact]
    public void DayAndTimeInWEuropeForAFixedUtcTime()
    {
        var clock = At(Utc(2026, 11, 26, 19, 30)); // winter: UTC+1
        Assert.Equal(new DateOnly(2026, 11, 26), clock.Today);
        Assert.Equal(new TimeOnly(20, 30), clock.TimeOfDay);
        Assert.Equal(DayOfWeek.Thursday, clock.DayOfWeek);
        Assert.Equal(DateTimeKind.Unspecified, clock.Now.Kind);
        Assert.Equal(new DateTime(2026, 11, 26, 0, 0, 0), clock.StartOfToday);
    }

    [Fact]
    public void LastSevenDaysStartAtMidnightOfTheControlZoneSixDaysBefore()
    {
        // 2026-11-27 00:30 in W. Europe (UTC+1): the 7 days are the 21st to the 27th of the Control Time Zone.
        Assert.Equal(Utc(2026, 11, 20, 23, 0), At(Utc(2026, 11, 26, 23, 30)).StartOfLastDaysUtc(7));
        // Tokyo (UTC+9) at the same instant is the 27th at 08:30.
        Assert.Equal(Utc(2026, 11, 20, 15, 0), At(Utc(2026, 11, 26, 23, 30), Tokyo).StartOfLastDaysUtc(7));
        Assert.Equal(Utc(2026, 11, 26, 23, 0), At(Utc(2026, 11, 26, 23, 30)).StartOfLastDaysUtc(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => At(Utc(2026, 11, 26, 23, 30)).StartOfLastDaysUtc(0));
    }

    [Fact]
    public void DateChangesAtMidnightOfTheControlZoneNotAtMidnightUtc()
    {
        // 23:30 UTC on the 26th is 00:30 on the 27th in W. Europe.
        Assert.Equal(new DateOnly(2026, 11, 27), At(Utc(2026, 11, 26, 23, 30)).Today);
        Assert.Equal(new DateOnly(2026, 11, 26), At(Utc(2026, 11, 26, 22, 59)).Today);
        // Tokyo is a day ahead at 15:00 UTC, but the Control Time Zone decides.
        Assert.Equal(new DateOnly(2026, 11, 27), At(Utc(2026, 11, 26, 15, 0), Tokyo).Today);
        Assert.Equal(new DateOnly(2026, 11, 26), At(Utc(2026, 11, 26, 15, 0)).Today);
    }

    [Fact]
    public void EndOfDaylightSavingTimeLastSundayOfOctober()
    {
        // 2026-10-25: 03:00 CEST goes back to 02:00 CET at 01:00 UTC.
        Assert.Equal(new TimeOnly(2, 59), At(Utc(2026, 10, 25, 0, 59)).TimeOfDay);
        Assert.Equal(new TimeOnly(2, 0), At(Utc(2026, 10, 25, 1, 0)).TimeOfDay);
        Assert.Equal(new DateOnly(2026, 10, 25), At(Utc(2026, 10, 25, 1, 0)).Today);
    }

    [Fact]
    public void StartOfDaylightSavingTimeLastSundayOfMarch()
    {
        // 2026-03-29: 02:00 CET goes to 03:00 CEST at 01:00 UTC.
        Assert.Equal(new TimeOnly(1, 59), At(Utc(2026, 3, 29, 0, 59)).TimeOfDay);
        Assert.Equal(new TimeOnly(3, 0), At(Utc(2026, 3, 29, 1, 0)).TimeOfDay);
    }

    [Fact]
    public void ToUtcOfANormalTime()
    {
        var clock = At(Utc(2026, 11, 26, 12));
        Assert.Equal(Utc(2026, 11, 26, 19), clock.ToUtc(new DateOnly(2026, 11, 26), new TimeOnly(20, 0)));
        Assert.Equal(Utc(2026, 7, 1, 18), clock.ToUtc(new DateOnly(2026, 7, 1), new TimeOnly(20, 0)));
        Assert.Equal(DateTimeKind.Utc, clock.ToUtc(new DateOnly(2026, 7, 1), new TimeOnly(20, 0)).Kind);
    }

    [Fact]
    public void ToUtcOfATimeInTheGapMovesForward()
    {
        // 02:30 does not exist on 2026-03-29; it moves forward by the gap to 03:30 CEST = 01:30 UTC.
        var utc = At(Utc(2026, 3, 29, 0)).ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30));
        Assert.Equal(Utc(2026, 3, 29, 1, 30), utc);
    }

    [Fact]
    public void ToUtcOfATimeInTheOverlapGivesTheEarlierInstant()
    {
        // 02:30 exists two times on 2026-10-25: 00:30 UTC (CEST) and 01:30 UTC (CET). An end time takes the earlier one.
        var utc = At(Utc(2026, 10, 25, 0)).ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), utc);
    }

    [Fact]
    public void ReadGivesTheDayAndTheTimeOfOneInstant()
    {
        var moment = At(Utc(2026, 11, 26, 22, 59)).Read();
        Assert.Equal(new DateOnly(2026, 11, 26), moment.Today);
        Assert.Equal(new TimeOnly(23, 59), moment.TimeOfDay);
        Assert.Equal(Utc(2026, 11, 26, 22, 59), moment.Utc);
    }

    // The Schedule Window end in the repeated hour (harden-storage-lockdown, design D9).

    private static bool Outside(DateTime utc, TimeOnly start, TimeOnly end)
    {
        var clock = new ControlClock(WEurope, () => utc);
        return clock.IsOutsideWindow(clock.Read(), start, end);
    }

    [Fact]
    public void WindowEndInTheRepeatedHourEndsAtTheFirstInstant()
    {
        var start = new TimeOnly(0, 0);
        var end = new TimeOnly(2, 30);
        // 2026-10-25: 00:29 UTC is 02:29 CEST (inside), 00:30 UTC is the first 02:30 (outside).
        Assert.False(Outside(Utc(2026, 10, 25, 0, 29), start, end));
        Assert.True(Outside(Utc(2026, 10, 25, 0, 30), start, end));
        // 01:10 UTC is 02:10 CET, the second time: the time of day is before the end, but the window stays closed.
        Assert.True(Outside(Utc(2026, 10, 25, 1, 10), start, end));
        Assert.True(Outside(Utc(2026, 10, 25, 1, 29), start, end));
    }

    [Fact]
    public void NormalWindowEndOnTheDayOfTheChange()
    {
        var start = new TimeOnly(8, 0);
        var end = new TimeOnly(20, 0);
        // 2026-10-25 is CET after 03:00: 18:59 UTC is 19:59, 19:00 UTC is 20:00.
        Assert.False(Outside(Utc(2026, 10, 25, 18, 59), start, end));
        Assert.True(Outside(Utc(2026, 10, 25, 19, 0), start, end));
    }

    [Fact]
    public void TimeBeforeTheStartIsOutside()
    {
        // 06:59 UTC on 2026-11-26 is 07:59 CET.
        Assert.True(Outside(Utc(2026, 11, 26, 6, 59), new TimeOnly(8, 0), new TimeOnly(20, 0)));
        Assert.False(Outside(Utc(2026, 11, 26, 7, 0), new TimeOnly(8, 0), new TimeOnly(20, 0)));
    }

    [Fact]
    public void WindowEndInTheGapStillEndsByTheTimeOfDay()
    {
        // 2026-03-29: 02:30 does not exist. 01:00 UTC is 03:00 CEST, after the end by the time of day.
        Assert.False(Outside(Utc(2026, 3, 29, 0, 59), new TimeOnly(0, 0), new TimeOnly(2, 30)));
        Assert.True(Outside(Utc(2026, 3, 29, 1, 0), new TimeOnly(0, 0), new TimeOnly(2, 30)));
    }
}
