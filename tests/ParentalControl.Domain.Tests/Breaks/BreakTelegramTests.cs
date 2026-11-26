using System.Text;
using ParentalControl.Domain.Breaks;

namespace ParentalControl.Domain.Tests.Breaks;

public class BreakTelegramTests
{
    private static readonly DateTime Start = new(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CallbackRoundTrip()
    {
        var data = BreakCallback.Format(3, Start.AddMilliseconds(456));
        Assert.Equal($"brk_end:3:{BreakPlanner.ToUnixSeconds(Start)}", data);
        Assert.True(BreakCallback.TryParse(data, out var userId, out var unix));
        Assert.Equal(3, userId);
        Assert.Equal(BreakPlanner.ToUnixSeconds(Start), unix);
    }

    [Fact]
    public void CallbackFitsTheTelegramLimit()
    {
        var data = BreakCallback.Format(int.MaxValue, new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        Assert.True(Encoding.UTF8.GetByteCount(data) <= 32, data);
        Assert.True(Encoding.UTF8.GetByteCount(data) <= BreakCallback.MaxBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("brk_end:3")]
    [InlineData("brk_end:x:1")]
    [InlineData("brk_end:-3:1")]
    [InlineData("brk_end:3:1:2")]
    [InlineData("approve:3:15")]
    public void InvalidCallbackIsRejected(string? data) =>
        Assert.False(BreakCallback.TryParse(data, out _, out _));

    [Fact]
    public void ButtonOfTheBreakEndsOnlyThatBreak()
    {
        var rule = BreakRule.Default;
        var state = BreakState.Empty with { BreakStartedUtc = Start, BreakEndsUtc = Start.AddMinutes(10) };
        Assert.True(BreakCallback.TryParse(BreakCallback.Format(3, Start.AddMinutes(-60)), out _, out var oldStart));
        Assert.Equal(ParentEndOutcome.OtherBreak, BreakPlanner.EndByParent(rule, state, Start.AddMinutes(4), oldStart).Outcome);
        Assert.True(BreakCallback.TryParse(BreakCallback.Format(3, Start), out _, out var thisStart));
        Assert.Equal(ParentEndOutcome.Ended, BreakPlanner.EndByParent(rule, state, Start.AddMinutes(4), thisStart).Outcome);
    }

    [Fact]
    public void StatusLines()
    {
        var zone = TimeZoneInfo.Utc;
        var running = new BreakStatus(3, "Kid", BreakRule.Default, 45, Start, Start.AddMinutes(10));
        Assert.Equal("• Kid: break until 15:10", BreakTelegramTexts.Line(running, "Kid", Start.AddMinutes(1), zone, "en"));
        Assert.Equal("• Kid: istirahat sampai pukul 15:10", BreakTelegramTexts.Line(running, "Kid", Start.AddMinutes(1), zone, "id"));

        var counting = new BreakStatus(3, "Kid", BreakRule.Default, 33, null, null);
        Assert.Equal("• Kid: break in 12 minutes (33 of 45 minutes of use)", BreakTelegramTexts.Line(counting, "Kid", Start, zone, "en"));

        var off = counting with { Rule = BreakRule.Default with { Enabled = false } };
        Assert.Equal("• Kid: breaks are off", BreakTelegramTexts.Line(off, "Kid", Start, zone, "en"));
    }

    [Fact]
    public void NoticeNamesTheUserAndTheEnd() =>
        Assert.Equal("⏸️ *Break started*\nKid has a break of 10 minutes, until 15:10.",
            BreakTelegramTexts.Notice("Kid", Start, Start.AddMinutes(10), TimeZoneInfo.Utc, "en"));
}
