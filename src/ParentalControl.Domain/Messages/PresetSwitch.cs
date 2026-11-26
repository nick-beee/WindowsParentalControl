namespace ParentalControl.Domain.Messages;

/// <summary>The texts to write for the child message keys, and the keys that keep a text that the parent edited.</summary>
public sealed record PresetSwitchPlan(IReadOnlyDictionary<string, string> Writes, IReadOnlyList<string> Kept);

/// <summary>
/// The rules that keep the texts of the parent. A text is unedited when it is missing, empty, equal to a default text
/// of a preset or equal to a default text of an earlier program version. Only an unedited text gets a new default.
/// </summary>
public static class PresetSwitch
{
    /// <summary>The plan of a preset switch: each unedited text gets the default of <paramref name="target"/>.</summary>
    public static PresetSwitchPlan Plan(IReadOnlyDictionary<string, string?> stored, string target)
    {
        var defaults = MessageCatalog.Defaults(target);
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var key in ChildMessageKeys.All)
        {
            stored.TryGetValue(key, out var text);
            if (string.IsNullOrWhiteSpace(text) || IsDefault(key, text))
                writes[key] = defaults[key];
            else
                kept.Add(key);
        }
        return new PresetSwitchPlan(writes, kept);
    }

    /// <summary>
    /// The plan at each start of a database with a preset. A key without a row gets the default of
    /// <paramref name="preset"/>. A text that is equal to an earlier default text gets the current default.
    /// No other text changes.
    /// </summary>
    public static PresetSwitchPlan PlanUpdate(IReadOnlyDictionary<string, string?> stored, string preset)
    {
        var defaults = MessageCatalog.Defaults(preset);
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ChildMessageKeys.All)
        {
            if (!stored.TryGetValue(key, out var text) || text is null)
                writes[key] = defaults[key];
            else if (IsEarlierDefault(key, text))
                writes[key] = defaults[key];
        }
        return new PresetSwitchPlan(writes, Array.Empty<string>());
    }

    /// <summary>True if the text is a current default text of the key in a preset, or an earlier default text.</summary>
    public static bool IsDefault(string key, string text)
    {
        var normalized = Normalize(text);
        return LanguagePreset.All.Any(p => Normalize(MessageCatalog.Defaults(p)[key]) == normalized)
            || IsEarlierDefault(key, text);
    }

    private static bool IsEarlierDefault(string key, string text)
    {
        var normalized = Normalize(text);
        return MessageCatalog.EarlierDefaults(key).Any(d => Normalize(d) == normalized);
    }

    // A text box gives "\r\n" and can add a line break at the end.
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
