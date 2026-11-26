using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class PresetSwitchTests
{
    private static Dictionary<string, string?> Stored(string preset) =>
        ChildMessageKeys.All.ToDictionary(k => k, k => (string?)MessageCatalog.Defaults(preset)[k]);

    [Fact]
    public void MissingEmptyAndWhiteSpaceTextsAreWritten()
    {
        var stored = new Dictionary<string, string?>
        {
            [ChildMessageKeys.LimitWarn] = "",
            [ChildMessageKeys.CurfewWarn] = "   ",
            [ChildMessageKeys.RemoteLock] = null,
        };
        var plan = PresetSwitch.Plan(stored, "de");
        Assert.Equal(ChildMessageKeys.All.Count, plan.Writes.Count);
        Assert.Empty(plan.Kept);
        Assert.Equal(MessageCatalog.Defaults("de")[ChildMessageKeys.LimitWarn], plan.Writes[ChildMessageKeys.LimitWarn]);
    }

    [Fact]
    public void IndonesianDefaultIsReplacedByGerman()
    {
        var plan = PresetSwitch.Plan(Stored("id"), "de");
        Assert.Equal(MessageCatalog.Defaults("de")[ChildMessageKeys.LimitReached], plan.Writes[ChildMessageKeys.LimitReached]);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void EarlierEnglishDefaultIsReplaced()
    {
        var stored = Stored("en");
        stored[ChildMessageKeys.LimitWarn] = "You have {minutes} minutes of daily screen time remaining. Please save your work.";
        var plan = PresetSwitch.Plan(stored, "de");
        Assert.Equal(MessageCatalog.Defaults("de")[ChildMessageKeys.LimitWarn], plan.Writes[ChildMessageKeys.LimitWarn]);
    }

    [Fact]
    public void EditedTextIsKept()
    {
        var stored = Stored("id");
        stored[ChildMessageKeys.RemoteLock] = "Abendessen!";
        var plan = PresetSwitch.Plan(stored, "de");
        Assert.False(plan.Writes.ContainsKey(ChildMessageKeys.RemoteLock));
        Assert.Equal(new[] { ChildMessageKeys.RemoteLock }, plan.Kept);
    }

    [Fact]
    public void PromptReasonsWithWindowsLineBreaksAreADefault()
    {
        var stored = Stored("id");
        stored[ChildMessageKeys.PromptResponsePresets] =
            MessageCatalog.Defaults("id")[ChildMessageKeys.PromptResponsePresets].Replace("\n", "\r\n") + "\r\n";
        var plan = PresetSwitch.Plan(stored, "de");
        Assert.Contains("Hausaufgaben", plan.Writes[ChildMessageKeys.PromptResponsePresets]);
    }

    [Fact]
    public void UpdateWritesMissingKeysAndEarlierDefaultsOnly()
    {
        var stored = Stored("en");
        stored.Remove(ChildMessageKeys.CategoryWarn);
        stored[ChildMessageKeys.LimitWarn] = "You have {minutes} minutes of daily screen time remaining. Please save your work.";
        stored[ChildMessageKeys.RemoteLock] = "Dinner!";
        stored[ChildMessageKeys.BonusGranted] = "";
        var plan = PresetSwitch.PlanUpdate(stored, "en");
        var en = MessageCatalog.Defaults("en");
        Assert.Equal(new[] { ChildMessageKeys.LimitWarn, ChildMessageKeys.CategoryWarn }.OrderBy(k => k), plan.Writes.Keys.OrderBy(k => k));
        Assert.Equal(en[ChildMessageKeys.LimitWarn], plan.Writes[ChildMessageKeys.LimitWarn]);
    }

    [Fact]
    public void UpdateOfIndonesianInstallChangesNothing() =>
        Assert.Empty(PresetSwitch.PlanUpdate(Stored("id"), "id").Writes);
}
