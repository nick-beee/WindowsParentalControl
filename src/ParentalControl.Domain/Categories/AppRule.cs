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
/// If <see cref="IsBlocked"/> is true, each exe that the rule resolves is a Blocked Program. The rule then keeps
/// its category and App Limit, and they apply again when the block ends.
/// </summary>
public sealed record AppRule(long Id, RuleMatchType MatchType, string Pattern, AppCategory Category, DailyLimit? AppLimit = null,
    bool IsBlocked = false)
{
    /// <summary>
    /// The name of the app in a child message: the file name without ".exe" for a file rule, the last folder name
    /// for a folder rule.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = WindowsPath.GetFileName(Pattern.TrimEnd('\\'));
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

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

    /// <summary>
    /// Returns an error text, or null if the rule is valid. A block is refused on a System rule, on a folder rule
    /// for a protected folder or a folder below one, and on a file rule for a protected file name
    /// (<see cref="ProtectedPrograms"/>). Without <paramref name="protectedPrograms"/>, only the file names are checked.
    /// </summary>
    public static string? Validate(RuleMatchType matchType, string? pattern, AppCategory category, DailyLimit? appLimit,
        bool isBlocked = false, ProtectedPrograms? protectedPrograms = null)
    {
        var normalized = NormalizePattern(matchType, pattern);
        if (normalized is null)
            return matchType == RuleMatchType.Folder
                ? "The folder must be an absolute path, for example C:\\Games\\."
                : "The file name must be a name without a folder, for example game.exe.";
        if (appLimit is not null && !appLimit.IsValid)
            return $"An App Limit must be from 0 to {DailyLimit.MaxMinutes} minutes.";
        if (category == AppCategory.System && appLimit is { IsNone: false })
            return "A rule for System cannot have an App Limit.";
        if (isBlocked)
        {
            if (category == AppCategory.System)
                return "A rule for System cannot have a block.";
            if (matchType == RuleMatchType.Folder && protectedPrograms is not null && protectedPrograms.IsProtectedFolder(normalized))
                return "The Windows folder and the Parental Control folder cannot have a block.";
            if (matchType == RuleMatchType.File && ProtectedPrograms.IsProtectedFileName(normalized))
                return $"{normalized} cannot have a block.";
        }
        return null;
    }
}
