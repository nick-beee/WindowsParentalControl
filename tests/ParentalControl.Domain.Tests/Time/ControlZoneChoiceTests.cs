using ParentalControl.Domain.Time;
using static ParentalControl.Domain.Tests.Time.TestZones;

namespace ParentalControl.Domain.Tests.Time;

public class ControlZoneChoiceTests
{
    private static TimeZoneInfo? Find(string id) =>
        id == WEurope.Id ? WEurope : id == Tokyo.Id ? Tokyo : null;

    [Fact]
    public void AbsentValueUsesAndWritesTheSystemZoneWithTheFirstStartMessage()
    {
        var d = ControlZoneChoice.Choose(new ControlZoneReading(null, null), null, WEurope, Find);
        Assert.Same(WEurope, d.Zone);
        Assert.True(d.WriteValue);
        Assert.True(d.FirstStartMessage);
        Assert.Null(d.Error);
    }

    [Fact]
    public void ValidValueIsUsedAndNotWritten()
    {
        var d = ControlZoneChoice.Choose(new ControlZoneReading("Tokyo Standard Time", null), null, WEurope, Find);
        Assert.Same(Tokyo, d.Zone);
        Assert.False(d.WriteValue);
        Assert.False(d.FirstStartMessage);
        Assert.Null(d.Error);
    }

    [Fact]
    public void UnknownIdWithALastValidZoneKeepsThatZone()
    {
        var d = ControlZoneChoice.Choose(new ControlZoneReading("Mars Standard Time", null), Tokyo, WEurope, Find);
        Assert.Same(Tokyo, d.Zone);
        Assert.False(d.WriteValue);
        Assert.NotNull(d.Error);
    }

    [Fact]
    public void UnknownIdWithoutALastValidZoneUsesTheSystemZoneAndDoesNotWrite()
    {
        var d = ControlZoneChoice.Choose(new ControlZoneReading("Mars Standard Time", null), null, WEurope, Find);
        Assert.Same(WEurope, d.Zone);
        Assert.False(d.WriteValue);
        Assert.False(d.FirstStartMessage);
        Assert.NotNull(d.Error);
    }

    [Fact]
    public void UntrustedKeyIsTheSameAsUnknown()
    {
        var withLast = ControlZoneChoice.Choose(new ControlZoneReading("Tokyo Standard Time", "Users can write"), WEurope, Tokyo, Find);
        Assert.Same(WEurope, withLast.Zone);
        Assert.False(withLast.WriteValue);
        Assert.NotNull(withLast.Error);

        var without = ControlZoneChoice.Choose(new ControlZoneReading(null, "Users can write"), null, Tokyo, Find);
        Assert.Same(Tokyo, without.Zone);
        Assert.False(without.WriteValue);
        Assert.False(without.FirstStartMessage);
        Assert.NotNull(without.Error);
    }
}
