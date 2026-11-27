using ParentalControl.Domain.Time;

namespace ParentalControl.Domain.Tests.Time;

public class TimeZoneChangeReportTests
{
    private static readonly SystemZoneSetting Berlin = new("W. Europe Standard Time", false);
    private static readonly SystemZoneSetting Tokyo = new("Tokyo Standard Time", false);
    private static readonly DateOnly Day = new(2026, 11, 26);

    [Fact]
    public void NoChangeGivesNothing() =>
        Assert.Equal(TimeZoneChangeOutcome.None, new TimeZoneChangeReport().Check(Berlin, Berlin, Day));

    [Fact]
    public void NewKeyNameGivesAnEventAndAnAlert() =>
        Assert.Equal(new TimeZoneChangeOutcome(true, true), new TimeZoneChangeReport().Check(Berlin, Tokyo, Day));

    [Fact]
    public void SecondChangeOnTheSameDayGivesAnEventAndNoAlert()
    {
        var report = new TimeZoneChangeReport();
        report.Check(Berlin, Tokyo, Day);
        Assert.Equal(new TimeZoneChangeOutcome(true, false), report.Check(Tokyo, Berlin, Day));
    }

    [Fact]
    public void ChangeOnTheNextDayAlertsAgain()
    {
        var report = new TimeZoneChangeReport();
        report.Check(Berlin, Tokyo, Day);
        Assert.Equal(new TimeZoneChangeOutcome(true, true), report.Check(Tokyo, Berlin, Day.AddDays(1)));
    }

    [Fact]
    public void ChangeOfOnlyTheDaylightSavingFlagGivesAnEvent() =>
        Assert.True(new TimeZoneChangeReport().Check(Berlin, Berlin with { DaylightSavingDisabled = true }, Day).WriteEvent);

    [Fact]
    public void FirstStartWithoutALastSettingGivesNoEvent() =>
        Assert.Equal(TimeZoneChangeOutcome.None, new TimeZoneChangeReport().Check(null, Tokyo, Day));

    [Fact]
    public void StoredFormRoundTrips()
    {
        Assert.Equal("W. Europe Standard Time|0", Berlin.Format());
        Assert.Equal(Berlin, SystemZoneSetting.Parse(Berlin.Format()));
        var off = Berlin with { DaylightSavingDisabled = true };
        Assert.Equal(off, SystemZoneSetting.Parse(off.Format()));
        Assert.Null(SystemZoneSetting.Parse(null));
        Assert.Null(SystemZoneSetting.Parse("no flag"));
        Assert.Null(SystemZoneSetting.Parse("Zone|2"));
        Assert.Null(SystemZoneSetting.Parse("|1"));
    }
}
