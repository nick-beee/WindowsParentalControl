namespace ParentalControl.Domain.Messages;

/// <summary>
/// A Language Preset: a named set of default child texts. It sets the language of the service messages,
/// the Session Agent and the child request page. It does not change the language of the admin UIs.
/// </summary>
public static class LanguagePreset
{
    public const string De = "de";
    public const string En = "en";
    public const string Id = "id";

    /// <summary>The preset of a new install.</summary>
    public const string Default = De;

    public static IReadOnlyList<string> All { get; } = new[] { De, En, Id };

    /// <summary>Returns the stored preset if it is known (letter case ignored), and <see cref="Default"/> for each other value.</summary>
    public static string Normalize(string? stored)
    {
        var value = stored?.Trim().ToLowerInvariant();
        return value is not null && All.Contains(value) ? value : Default;
    }

    /// <summary>
    /// The language of the Telegram texts for the parent. Telegram has Indonesian and English texts only:
    /// "id" gives "id", each other preset gives "en".
    /// </summary>
    public static string ParentLanguage(string? preset) => Normalize(preset) == Id ? Id : En;
}
