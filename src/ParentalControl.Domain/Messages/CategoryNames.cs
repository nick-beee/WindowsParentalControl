using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Messages;

/// <summary>The name of an App Category in a child message, per Language Preset. The logs keep the enum names.</summary>
public static class CategoryNames
{
    private static readonly IReadOnlyDictionary<AppCategory, string> German = new Dictionary<AppCategory, string>
    {
        [AppCategory.Games] = "Spiele",
        [AppCategory.Video] = "Videos",
        [AppCategory.Social] = "Soziale Medien",
        [AppCategory.Study] = "Lernen",
        [AppCategory.Creative] = "Kreativ",
        [AppCategory.System] = "System",
        [AppCategory.Unknown] = "Sonstiges",
    };

    /// <summary>"de" gives the German name. "en" and "id" give the enum name ("Games").</summary>
    public static string For(AppCategory category, string? preset) =>
        LanguagePreset.Normalize(preset) == LanguagePreset.De && German.TryGetValue(category, out var name)
            ? name
            : category.ToString();
}
