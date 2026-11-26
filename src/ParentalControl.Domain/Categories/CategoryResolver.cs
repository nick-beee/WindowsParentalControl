namespace ParentalControl.Domain.Categories;

/// <summary>The category of an exe and the App Rule that gave it (null for Unknown without a rule).</summary>
public sealed record CategoryResolution(AppCategory Category, AppRule? Rule, string? CanonicalPath, string FileName);

/// <summary>
/// Finds the App Category of an exe. A file-name rule wins over a folder rule.
/// Of the folder rules, the longest matching folder wins. An exe without a matching rule is in Unknown.
/// </summary>
public sealed class CategoryResolver
{
    private readonly Dictionary<string, AppRule> _fileRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Folder, AppRule Rule)> _folderRules = [];

    public CategoryResolver(IEnumerable<AppRule> rules)
    {
        // Lowest id first, so that a duplicate pattern gives the same result each time.
        foreach (var rule in rules.OrderBy(r => r.Id))
        {
            var pattern = AppRule.NormalizePattern(rule.MatchType, rule.Pattern);
            if (pattern is null)
                continue;

            if (rule.MatchType == RuleMatchType.File)
                _fileRules.TryAdd(pattern, rule);
            else if (!_folderRules.Any(f => string.Equals(f.Folder, pattern, StringComparison.OrdinalIgnoreCase)))
                _folderRules.Add((pattern, rule));
        }

        _folderRules.Sort((a, b) => b.Folder.Length.CompareTo(a.Folder.Length));
    }

    /// <summary>
    /// Resolves a full exe path. If the path is not absolute, only the file-name rules apply.
    /// </summary>
    public CategoryResolution Resolve(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new CategoryResolution(AppCategory.Unknown, null, null, "");

        var canonical = WindowsPath.Canonicalize(exePath);
        var fileName = WindowsPath.GetFileName(canonical ?? exePath);

        if (_fileRules.TryGetValue(fileName, out var fileRule))
            return new CategoryResolution(fileRule.Category, fileRule, canonical, fileName);

        if (canonical is not null)
        {
            foreach (var (folder, rule) in _folderRules)
            {
                if (WindowsPath.IsBelow(canonical, folder))
                    return new CategoryResolution(rule.Category, rule, canonical, fileName);
            }
        }

        return new CategoryResolution(AppCategory.Unknown, null, canonical, fileName);
    }

    /// <summary>
    /// Resolves a process name of an old Session Agent that sends no path.
    /// The old agent sends the name without ".exe"; this method adds it.
    /// </summary>
    public CategoryResolution ResolveFileName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return new CategoryResolution(AppCategory.Unknown, null, null, "");

        var name = WindowsPath.GetFileName(processName);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        return _fileRules.TryGetValue(name, out var rule)
            ? new CategoryResolution(rule.Category, rule, null, name)
            : new CategoryResolution(AppCategory.Unknown, null, null, name);
    }
}
