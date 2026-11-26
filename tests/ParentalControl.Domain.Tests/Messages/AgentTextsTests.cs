using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class AgentTextsTests
{
    [Fact]
    public void TrayTooltipsFitTheLimit()
    {
        foreach (var preset in LanguagePreset.All)
        {
            var set = AgentTexts.For(preset);
            foreach (var key in set.Keys.Where(k => k.StartsWith("tray_", StringComparison.Ordinal)))
            {
                var text = set.Format(key, ("hours", 23), ("minutes", 59), ("username", "Kid"));
                Assert.True(text.Length <= AgentTexts.TrayTooltipMaxLength, $"{preset}/{key}: {text.Length} characters");
            }
        }
    }

    [Theory]
    [InlineData("de", 3600 * 2 + 300, "Parental Control: noch 2 Std. 5 Min.")]
    [InlineData("de", 60, "Parental Control: noch 1 Minute")]
    [InlineData("de", 300, "Parental Control: noch 5 Minuten")]
    [InlineData("en", 60, "Parental Control: 1 minute left")]
    [InlineData("id", 300, "Parental Control: Sisa 5m")]
    [InlineData("id", 3600 + 120, "Parental Control: Sisa 1j 2m")]
    public void RemainingTooltip(string preset, int seconds, string expected) =>
        Assert.Equal(expected, AgentTexts.RemainingTooltip(AgentTexts.For(preset), seconds));

    [Fact]
    public void GermanWidgetTexts()
    {
        var de = AgentTexts.For("de");
        Assert.Equal("RESTZEIT HEUTE", de["widget_remaining_label"]);
        Assert.Equal("🎮 Mehr Zeit wünschen", de["widget_request_button"]);
        Assert.Equal("SISA WAKTU HARI INI", AgentTexts.For("id")["widget_remaining_label"]);
    }
}
