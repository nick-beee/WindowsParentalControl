namespace ParentalControl.Domain.Categories;

public enum RuleMatchType
{
    /// <summary>The exact exe file name, for example "RobloxPlayerBeta.exe".</summary>
    File,

    /// <summary>A folder prefix of the full exe path, on a whole folder boundary.</summary>
    Folder,
}

/// <summary>
/// An App Rule: it maps an exe to one App Category. <see cref="AppLimit"/> is the optional App Limit.
/// </summary>
public sealed record AppRule(long Id, RuleMatchType MatchType, string Pattern, AppCategory Category, DailyLimit? AppLimit = null)
{
    /// <summary>
    /// The stored form of a pattern: a trimmed file name, or a canonical folder with a trailing "\".
    /// Returns null if the pattern is not valid for the match type.
    /// </summary>
    public static string? NormalizePattern(RuleMatchType matchType, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        if (matchType == RuleMatchType.Folder)
            return WindowsPath.CanonicalizeFolder(pattern);

        var name = pattern.Trim().Trim('"');
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':', '\0', '*', '?']) >= 0 || name is "." or "..")
            return null;
        return name;
    }

    /// <summary>Returns an error text, or null if the rule is valid.</summary>
    public static string? Validate(RuleMatchType matchType, string? pattern, AppCategory category, DailyLimit? appLimit)
    {
        if (NormalizePattern(matchType, pattern) is null)
            return matchType == RuleMatchType.Folder
                ? "The folder must be an absolute path, for example C:\\Games\\."
                : "The file name must be a name without a folder, for example game.exe.";
        if (appLimit is not null && !appLimit.IsValid)
            return $"An App Limit must be from 0 to {DailyLimit.MaxMinutes} minutes.";
        if (category == AppCategory.System && appLimit is { IsNone: false })
            return "A rule for System cannot have an App Limit.";
        return null;
    }
}
