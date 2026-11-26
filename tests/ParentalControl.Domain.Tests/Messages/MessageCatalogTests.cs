using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class MessageCatalogTests
{
    private static string LimitWarn(string preset, int minutes) =>
        MessageCatalog.Defaults(preset).Format(ChildMessageKeys.LimitWarn, ("minutes", minutes));

    [Fact]
    public void GermanWarningForFiveMinutes() =>
        Assert.Equal("Deine Bildschirmzeit für heute ist in 5 Minuten um. Speichere jetzt, was du gerade machst.", LimitWarn("de", 5));

    [Fact]
    public void GermanWarningForOneMinute() =>
        Assert.Equal("Deine Bildschirmzeit für heute ist in 1 Minute um. Speichere jetzt, was du gerade machst.", LimitWarn("de", 1));

    [Fact]
    public void EnglishWarningUsesTheWordForm()
    {
        Assert.Equal("You have 1 minute of daily screen time remaining. Please save your work.", LimitWarn("en", 1));
        Assert.Equal("You have 5 minutes of daily screen time remaining. Please save your work.", LimitWarn("en", 5));
    }

    [Fact]
    public void IndonesianWarningHasOneWordForm()
    {
        Assert.Contains("1 menit", LimitWarn("id", 1));
        Assert.Contains("5 menit", LimitWarn("id", 5));
    }

    [Theory]
    [InlineData(ChildMessageKeys.CurfewWarn)]
    [InlineData(ChildMessageKeys.BonusGranted)]
    [InlineData(ChildMessageKeys.CategoryWarn)]
    public void EachMinutesTextUsesTheWordFormInGermanAndEnglish(string key)
    {
        var values = new (string, object)[] { ("minutes", 1), ("curfew_end", "21:00"), ("name", "Spiele") };
        Assert.Contains("1 Minute", MessageCatalog.Defaults("de").Format(key, values));
        Assert.DoesNotContain("1 Minuten", MessageCatalog.Defaults("de").Format(key, values));
        Assert.Contains("1 minute", MessageCatalog.Defaults("en").Format(key, values));
        Assert.DoesNotContain("1 minutes", MessageCatalog.Defaults("en").Format(key, values));
    }

    [Fact]
    public void GermanTextsEqualTheDesign()
    {
        var de = MessageCatalog.Defaults("de");
        Assert.Equal("Zeit für eine Pause! Deine Eltern sperren gleich den Computer.", de[ChildMessageKeys.RemoteLock]);
        Assert.Equal("Deine Bildschirmzeit für heute ist um. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen.", de[ChildMessageKeys.LimitReached]);
        Assert.Contains("Hausaufgaben", de[ChildMessageKeys.PromptResponsePresets]);
    }

    [Fact]
    public void GermanBlockedProgramMessageEqualsTheSpec() =>
        Assert.Equal("chrome ist auf diesem Computer gesperrt. Frag deine Eltern, wenn du das Programm brauchst.",
            MessageCatalog.Defaults("de").Format(ChildMessageKeys.ProgramBlocked, ("name", "chrome")));

    [Fact]
    public void BlockedProgramMessageHasOnlyTheNamePlaceholderInEachPreset()
    {
        foreach (var preset in new[] { "de", "en", "id" })
        {
            var text = MessageCatalog.Defaults(preset).Format(ChildMessageKeys.ProgramBlocked, ("name", "brave"));
            Assert.StartsWith("brave ", text);
            Assert.DoesNotContain("{", text);
        }
    }

    [Fact]
    public void EarlierEnglishDefaultsAreKnown()
    {
        Assert.Single(MessageCatalog.EarlierDefaults(ChildMessageKeys.LimitWarn));
        Assert.Empty(MessageCatalog.EarlierDefaults(ChildMessageKeys.RemoteLock));
    }
}
