namespace ParentalControl.Domain.Categories;

/// <summary>
/// The fixed set of App Categories. An exe that no App Rule matches is in <see cref="Unknown"/>.
/// The service never counts and never suspends <see cref="System"/>.
/// </summary>
public enum AppCategory
{
    Games,
    Video,
    Social,
    Study,
    Creative,
    System,
    Unknown,
}

public static class AppCategories
{
    public static IReadOnlyList<AppCategory> All { get; } = Enum.GetValues<AppCategory>();

    /// <summary>Parses the stored name of a category. The parse ignores letter case.</summary>
    public static bool TryParse(string? text, out AppCategory category)
    {
        category = AppCategory.Unknown;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().All(char.IsDigit))
            return false; // Enum.TryParse accepts numbers. A stored category is always a name.
        return Enum.TryParse(text.Trim(), ignoreCase: true, out category) && Enum.IsDefined(category);
    }
}
