using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class SessionTextsTests
{
    [Fact]
    public void EachPresetHasSevenDayNames()
    {
        foreach (var preset in LanguagePreset.All)
        {
            var set = SessionTexts.For(preset);
            var names = Enum.GetValues<DayOfWeek>().Select(d => SessionTexts.DayName(set, d)).ToList();
            Assert.Equal(7, names.Distinct().Count());
            Assert.DoesNotContain(names, n => n.StartsWith("day_", StringComparison.Ordinal));
        }
        Assert.Equal("Montag", SessionTexts.DayName(SessionTexts.For("de"), DayOfWeek.Monday));
        Assert.Equal("Minggu", SessionTexts.DayName(SessionTexts.For("id"), DayOfWeek.Sunday));
    }

    [Theory]
    [InlineData("de", 1, "1 Minute")]
    [InlineData("de", 45, "45 Minuten")]
    [InlineData("de", 60, "1 Stunde")]
    [InlineData("de", 120, "2 Stunden")]
    [InlineData("de", 61, "1 Stunde 1 Minute")]
    [InlineData("de", 150, "2 Stunden 30 Minuten")]
    [InlineData("en", 90, "1 hour 30 minutes")]
    [InlineData("en", 1, "1 minute")]
    [InlineData("id", 120, "2 jam")]
    [InlineData("id", 90, "1 jam 30 menit")]
    [InlineData("id", 1, "1 menit")]
    public void DurationUsesWordForms(string preset, int minutes, string expected) =>
        Assert.Equal(expected, SessionTexts.Duration(SessionTexts.For(preset), minutes));

    [Theory]
    [InlineData("de", 1, "(Der Computer wird in 1 Sekunde gesperrt.)")]
    [InlineData("de", 15, "(Der Computer wird in 15 Sekunden gesperrt.)")]
    [InlineData("en", 1, "(The computer locks in 1 second.)")]
    [InlineData("id", 15, "(Layar akan otomatis dikunci dalam 15 detik)")]
    public void LockCountdownUsesWordForms(string preset, int seconds, string expected) =>
        Assert.Equal(expected, SessionTexts.For(preset).Format("lock_countdown", ("seconds", seconds)));

    [Theory]
    [InlineData("de", "24.12.2026")]
    [InlineData("en", "2026-12-24")]
    [InlineData("id", "24/12/2026")]
    public void DateFormatPerPreset(string preset, string expected) =>
        Assert.Equal(expected, SessionTexts.FormatDate(SessionTexts.For(preset), new DateOnly(2026, 12, 24)));

    [Fact]
    public void GermanDeclineWithReason() =>
        Assert.Equal("Deine Eltern haben deine Anfrage abgelehnt: „Mach zuerst deine Hausaufgaben.“",
            SessionTexts.For("de").Format("declined_with_reason", ("reason", SessionTexts.For("de")["decline_reason_1"])));

    [Theory]
    [InlineData("de", "Parental Control — Computer gesperrt",
        "Du hast pausierte Programme immer wieder fortgesetzt. Darum bleibt der Computer heute gesperrt. Deine Eltern können die Sperre beenden.")]
    [InlineData("en", "Parental Control — Computer Locked",
        "You resumed paused programs again and again. So the computer stays locked today. Your parent can end the lock.")]
    public void ResumeLockTexts(string preset, string title, string body)
    {
        // Spec: category-limits "Messages, event and alert of a Resume Lock".
        Assert.Equal(title, SessionTexts.For(preset)["resume_lock_title"]);
        Assert.Equal(body, SessionTexts.For(preset)["resume_lock_body"]);
        Assert.DoesNotContain("resume_lock", SessionTexts.For("id")["resume_lock_body"]);
    }
}
