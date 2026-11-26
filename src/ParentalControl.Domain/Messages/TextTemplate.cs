using System.Globalization;
using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Messages;

/// <summary>
/// Fills the placeholders of a text. <c>{name}</c> gives the value. <c>{name|one|other}</c> gives the word form
/// for the number in <c>name</c>: <c>one</c> for 1, <c>other</c> for each other value. This rule is correct for
/// German and English. Indonesian has no plural form after a number, so its texts use no word form.
/// </summary>
public static class TextTemplate
{
    private static readonly Regex Placeholder = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\|(?<one>[^{}|]*)\|(?<other>[^{}|]*))?\}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Replaces each placeholder that has a value, in one pass. A value is not filled again, so a value that
    /// contains a placeholder (for example a reason that the child typed) stays as it is.
    /// A placeholder without a value stays in the text, so that a typing error is visible.
    /// </summary>
    public static string Fill(string? text, IReadOnlyDictionary<string, string>? values)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (values is null || values.Count == 0) return text;

        return Placeholder.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            if (!values.TryGetValue(name, out var value)) return m.Value;
            if (!m.Groups["one"].Success) return value;
            return IsOne(value) ? m.Groups["one"].Value : m.Groups["other"].Value;
        });
    }

    public static string Fill(string? text, params (string Name, object Value)[] values) =>
        Fill(text, ToDictionary(values));

    /// <summary>The names of the placeholders of a text. <c>{minutes}</c> and <c>{minutes|a|b}</c> both give "minutes".</summary>
    public static IReadOnlySet<string> Placeholders(string? text)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return names;
        foreach (Match m in Placeholder.Matches(text)) names.Add(m.Groups["name"].Value);
        return names;
    }

    internal static Dictionary<string, string> ToDictionary((string Name, object Value)[] values)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            dict[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return dict;
    }

    private static bool IsOne(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == 1;
}
