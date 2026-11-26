using ParentalControl.Domain.Breaks;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Breaks;

public class BreakTextsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTime Start = new(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc);

    private static string Text(string preset, string key, Dictionary<string, string> values) =>
        TextTemplate.Fill(MessageCatalog.Defaults(preset)[key], values);

    [Fact]
    public void GermanWarning() =>
        Assert.Equal("In 5 Minuten ist Pause. Speichere jetzt, was du gerade machst. Deine Programme bleiben offen.",
            Text("de", ChildMessageKeys.BreakWarn, BreakTexts.WarnValues(5)));

    [Fact]
    public void GermanWarningForOneMinute() =>
        Assert.StartsWith("In 1 Minute ist Pause.", Text("de", ChildMessageKeys.BreakWarn, BreakTexts.WarnValues(1)));

    [Fact]
    public void GermanBreakStart() =>
        Assert.Equal("Zeit für eine Pause! Der Computer wird jetzt für 10 Minuten gesperrt, bis 15:10 Uhr. Deine Programme bleiben offen.",
            Text("de", ChildMessageKeys.BreakStart, BreakTexts.StartValues(Start, Start.AddMinutes(10), Zone)));

    [Fact]
    public void GermanUnlockDuringABreak() =>
        Assert.Equal("Die Pause dauert noch bis 15:10 Uhr (1 Minute). Der Computer wird wieder gesperrt.",
            Text("de", ChildMessageKeys.BreakRunning, BreakTexts.RunningValues(Start.AddMinutes(9), Start.AddMinutes(10), Zone)));

    [Fact]
    public void RunningMinutesAreRoundedUp() =>
        Assert.Equal("6", BreakTexts.RunningValues(Start.AddMinutes(4).AddSeconds(10), Start.AddMinutes(10), Zone)["minutes"]);

    [Fact]
    public void StartClampedToTheScheduleEndNamesTheShorterBreak() =>
        Assert.Contains("für 5 Minuten gesperrt, bis 15:05 Uhr",
            Text("de", ChildMessageKeys.BreakStart, BreakTexts.StartValues(Start, Start.AddMinutes(5), Zone)));

    [Fact]
    public void EndIsLocalTime()
    {
        var plusOne = TimeZoneInfo.CreateCustomTimeZone("plus1", TimeSpan.FromHours(1), "plus1", "plus1");
        Assert.Equal("16:10", BreakTexts.EndText(Start.AddMinutes(10), plusOne));
    }

    [Fact]
    public void EnglishAndIndonesianStart()
    {
        var values = BreakTexts.StartValues(Start, Start.AddMinutes(10), Zone);
        Assert.Equal("Time for a break! The computer locks now for 10 minutes, until 15:10. Your programs stay open.",
            Text("en", ChildMessageKeys.BreakStart, values));
        Assert.Contains("selama 10 menit, sampai pukul 15:10", Text("id", ChildMessageKeys.BreakStart, values));
    }

    [Fact]
    public void BreakTitlePerPreset()
    {
        Assert.Equal("Parental Control — Pause", SessionTexts.For("de")["break_title"]);
        Assert.Equal("Parental Control — Break", SessionTexts.For("en")["break_title"]);
        Assert.Equal("Parental Control — Istirahat", SessionTexts.For("id")["break_title"]);
    }

    [Fact]
    public void UpdateOfAnEarlierInstallWritesTheMissingBreakKeys()
    {
        // An "en" install of an earlier version has no break keys.
        var stored = ChildMessageKeys.All
            .Where(k => !k.StartsWith("msg_break_", StringComparison.Ordinal))
            .ToDictionary(k => k, k => (string?)MessageCatalog.Defaults("en")[k]);
        var plan = PresetSwitch.PlanUpdate(stored, "en");
        Assert.Equal(MessageCatalog.Defaults("en")[ChildMessageKeys.BreakStart], plan.Writes[ChildMessageKeys.BreakStart]);
        Assert.Equal(3, plan.Writes.Count);
    }
}
