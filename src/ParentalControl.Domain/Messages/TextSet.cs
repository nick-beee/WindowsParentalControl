namespace ParentalControl.Domain.Messages;

/// <summary>The texts of one catalog for one Language Preset.</summary>
public sealed class TextSet
{
    private readonly IReadOnlyDictionary<string, string> _texts;

    public TextSet(string preset, IReadOnlyDictionary<string, string> texts)
    {
        Preset = preset;
        _texts = texts;
    }

    public string Preset { get; }

    public IEnumerable<string> Keys => _texts.Keys;

    /// <summary>The text of a key. An unknown key gives the key itself, so that the error is visible.</summary>
    public string this[string key] => _texts.TryGetValue(key, out var text) ? text : key;

    public string Format(string key, params (string Name, object Value)[] values) =>
        TextTemplate.Fill(this[key], values);
}

/// <summary>Builds the text sets of a catalog from rows with one text per preset.</summary>
internal static class TextCatalog
{
    public static IReadOnlyDictionary<string, TextSet> Build(IEnumerable<(string Key, string De, string En, string Id)> rows)
    {
        var de = new Dictionary<string, string>(StringComparer.Ordinal);
        var en = new Dictionary<string, string>(StringComparer.Ordinal);
        var id = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            de.Add(row.Key, row.De);
            en.Add(row.Key, row.En);
            id.Add(row.Key, row.Id);
        }
        return new Dictionary<string, TextSet>(StringComparer.Ordinal)
        {
            [LanguagePreset.De] = new TextSet(LanguagePreset.De, de),
            [LanguagePreset.En] = new TextSet(LanguagePreset.En, en),
            [LanguagePreset.Id] = new TextSet(LanguagePreset.Id, id),
        };
    }

    public static TextSet For(IReadOnlyDictionary<string, TextSet> sets, string? preset) =>
        sets[LanguagePreset.Normalize(preset)];
}
