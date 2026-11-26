using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class TextTemplateTests
{
    [Theory]
    [InlineData(1, "1 Minute")]
    [InlineData(0, "0 Minuten")]
    [InlineData(2, "2 Minuten")]
    [InlineData(5, "5 Minuten")]
    [InlineData(15, "15 Minuten")]
    public void WordFormFollowsTheNumber(int minutes, string expected) =>
        Assert.Equal(expected, TextTemplate.Fill("{minutes} {minutes|Minute|Minuten}", ("minutes", minutes)));

    [Fact]
    public void PlainPlaceholderGivesTheValue() =>
        Assert.Equal("+5 Min.", TextTemplate.Fill("+{minutes} Min.", ("minutes", 5)));

    [Fact]
    public void UnknownPlaceholderStays() =>
        Assert.Equal("{name} in 5", TextTemplate.Fill("{name} in {minutes}", ("minutes", 5)));

    [Fact]
    public void UnknownWordFormStays() =>
        Assert.Equal("{seconds|a|b}", TextTemplate.Fill("{seconds|a|b}", ("minutes", 1)));

    [Fact]
    public void ValueIsNotFilledAgain() =>
        Assert.Equal("Grund: {minutes}", TextTemplate.Fill("Grund: {reason}", ("reason", "{minutes}"), ("minutes", 5)));

    [Fact]
    public void EmptyTextGivesEmpty() => Assert.Equal("", TextTemplate.Fill(null, ("minutes", 1)));

    [Fact]
    public void PlaceholdersGiveNamesOfBothForms() =>
        Assert.Equal(new[] { "minutes" }, TextTemplate.Placeholders("in {minutes} {minutes|Minute|Minuten}"));

    [Fact]
    public void PlaceholdersOfEnglishCurfewWarning() =>
        Assert.Equal(new[] { "curfew_end", "minutes" },
            TextTemplate.Placeholders(MessageCatalog.Defaults("en")[ChildMessageKeys.CurfewWarn]));

    [Fact]
    public void TextSetFormatUsesTheTemplate() =>
        Assert.Equal("(Der Computer wird in 1 Sekunde gesperrt.)",
            SessionTexts.For("de").Format("lock_countdown", ("seconds", 1)));
}
