using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class LanguagePresetTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("fr")]
    public void UnknownValueGivesGerman(string? stored) =>
        Assert.Equal("de", LanguagePreset.Normalize(stored));

    [Theory]
    [InlineData("EN", "en")]
    [InlineData(" id ", "id")]
    [InlineData("de", "de")]
    public void KnownValueIgnoresLetterCase(string stored, string expected) =>
        Assert.Equal(expected, LanguagePreset.Normalize(stored));

    [Theory]
    [InlineData("de", "en")]
    [InlineData("en", "en")]
    [InlineData("id", "id")]
    [InlineData(null, "en")]
    public void ParentLanguageIsIndonesianOnlyForId(string? preset, string expected) =>
        Assert.Equal(expected, LanguagePreset.ParentLanguage(preset));

    [Fact]
    public void DefaultIsGerman() => Assert.Equal("de", LanguagePreset.Default);
}
