using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

/// <summary>Checks each catalog: each preset has each key, no text is empty, and the placeholders match.</summary>
public class CatalogTests
{
    public static TheoryData<string> Catalogs => new() { "messages", "session", "agent", "request" };

    private static TextSet Set(string catalog, string preset) => catalog switch
    {
        "messages" => MessageCatalog.Defaults(preset),
        "session" => SessionTexts.For(preset),
        "agent" => AgentTexts.For(preset),
        "request" => RequestPageTexts.For(preset),
        _ => throw new ArgumentOutOfRangeException(nameof(catalog)),
    };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EachPresetHasTheSameKeys(string catalog)
    {
        var keys = Set(catalog, "de").Keys.OrderBy(k => k).ToArray();
        Assert.NotEmpty(keys);
        foreach (var preset in LanguagePreset.All)
            Assert.Equal(keys, Set(catalog, preset).Keys.OrderBy(k => k).ToArray());
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void NoTextIsEmpty(string catalog)
    {
        foreach (var preset in LanguagePreset.All)
        {
            var set = Set(catalog, preset);
            foreach (var key in set.Keys)
                Assert.False(string.IsNullOrWhiteSpace(set[key]), $"{catalog}/{preset}/{key} is empty");
        }
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void PlaceholdersAreTheSameInEachPreset(string catalog)
    {
        var de = Set(catalog, "de");
        foreach (var key in de.Keys)
        {
            var expected = TextTemplate.Placeholders(de[key]);
            foreach (var preset in LanguagePreset.All)
                Assert.True(expected.SetEquals(TextTemplate.Placeholders(Set(catalog, preset)[key])),
                    $"{catalog}/{preset}/{key} has other placeholders than de");
        }
    }

    [Fact]
    public void MessageCatalogHasEachChildMessageKey()
    {
        foreach (var preset in LanguagePreset.All)
            Assert.Equal(ChildMessageKeys.All.OrderBy(k => k), MessageCatalog.Defaults(preset).Keys.OrderBy(k => k));
    }

    [Fact]
    public void UnknownPresetGivesGerman() =>
        Assert.Equal("de", MessageCatalog.Defaults("fr").Preset);
}
