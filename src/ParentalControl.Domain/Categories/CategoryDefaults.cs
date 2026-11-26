namespace ParentalControl.Domain.Categories;

/// <summary>The result of <see cref="CategoryDefaults.PlanBlockedBrowserSeed"/>: the rules to insert and the rule ids to block.</summary>
public sealed record BlockedBrowserSeedPlan(IReadOnlyList<AppRule> ToInsert, IReadOnlyList<long> ToBlock);

/// <summary>The folders of this computer that the default rules use.</summary>
public sealed record DefaultRuleFolders(string WindowsFolder, string ProgramFiles, string ProgramFilesX86, string InstallFolder);

/// <summary>
/// The default Category Limits and the default App Rules. The service writes them one time,
/// on the first start of the version that has categories.
/// </summary>
public static class CategoryDefaults
{
    public const int WeekdayMinutes = 120;
    public const int WeekendMinutes = 180;

    public static IReadOnlyDictionary<AppCategory, DailyLimit> Limits { get; } = new Dictionary<AppCategory, DailyLimit>
    {
        [AppCategory.Games] = new(WeekdayMinutes, WeekendMinutes),
        [AppCategory.Video] = new(WeekdayMinutes, WeekendMinutes),
        [AppCategory.Social] = new(WeekdayMinutes, WeekendMinutes),
        [AppCategory.Unknown] = new(WeekdayMinutes, WeekendMinutes),
        [AppCategory.Study] = DailyLimit.None,
        [AppCategory.Creative] = DailyLimit.None,
        [AppCategory.System] = DailyLimit.None,
    };

    /// <summary>The default rules, without ids (Id = 0). The patterns are in their stored form.</summary>
    public static IReadOnlyList<AppRule> Rules(DefaultRuleFolders folders)
    {
        var rules = new List<AppRule>();

        void Folder(string path, AppCategory category)
        {
            var pattern = AppRule.NormalizePattern(RuleMatchType.Folder, path);
            if (pattern is not null && !rules.Any(r => r.MatchType == RuleMatchType.Folder &&
                                                       string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
                rules.Add(new AppRule(0, RuleMatchType.Folder, pattern, category));
        }

        void File(string name, AppCategory category) =>
            rules.Add(new AppRule(0, RuleMatchType.File, name, category));

        Folder(folders.WindowsFolder, AppCategory.System);
        Folder(folders.InstallFolder, AppCategory.System);

        Folder(folders.ProgramFilesX86 + @"\Steam\steamapps\common", AppCategory.Games);
        Folder(folders.ProgramFiles + @"\Steam\steamapps\common", AppCategory.Games);
        Folder(folders.ProgramFiles + @"\Epic Games", AppCategory.Games);
        Folder(folders.ProgramFilesX86 + @"\Epic Games", AppCategory.Games);
        Folder(SystemDrive(folders.WindowsFolder) + @"\Riot Games", AppCategory.Games);
        File("RobloxPlayerBeta.exe", AppCategory.Games);
        File("Minecraft.Windows.exe", AppCategory.Games);
        File("MinecraftLauncher.exe", AppCategory.Games);

        File("WINWORD.EXE", AppCategory.Study);
        File("EXCEL.EXE", AppCategory.Study);
        File("POWERPNT.EXE", AppCategory.Study);
        File("ONENOTE.EXE", AppCategory.Study);

        return rules;
    }

    /// <summary>
    /// The file names of the default blocked browsers. Each one is a file rule with the category Unknown and the block on.
    /// <c>firefox.exe</c> covers Tor Browser, and <c>opera.exe</c> covers Opera GX. There is no rule for
    /// <c>launcher.exe</c>: many game launchers have this name.
    /// </summary>
    public static IReadOnlyList<string> BlockedBrowsers { get; } =
    [
        "chrome.exe", "firefox.exe", "msedge.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe", "iexplore.exe",
        "chromium.exe", "waterfox.exe", "librewolf.exe", "floorp.exe", "palemoon.exe", "thorium.exe", "DuckDuckGo.exe",
    ];

    /// <summary>
    /// True for a file rule whose pattern is one of <see cref="BlockedBrowsers"/> (letter case ignored). The admin UI
    /// marks these rules as default browsers. A folder rule is never a default browser.
    /// </summary>
    public static bool IsBlockedBrowser(RuleMatchType matchType, string pattern) =>
        matchType == RuleMatchType.File && BlockedBrowsers.Contains(pattern, StringComparer.OrdinalIgnoreCase);

    /// <summary>The default blocked browser rules, without ids (Id = 0).</summary>
    public static IReadOnlyList<AppRule> BlockedBrowserRules() =>
        BlockedBrowsers.Select(name => new AppRule(0, RuleMatchType.File, name, AppCategory.Unknown, null, IsBlocked: true)).ToList();

    /// <summary>
    /// The one-time seed of the default blocked browsers on a database with <paramref name="existing"/> rules.
    /// A new rule for each browser without a file rule; the block on each existing file rule with the same pattern
    /// (letter case ignored), which keeps its category and App Limit. A System rule keeps no block, because a
    /// block on System is not valid.
    /// </summary>
    public static BlockedBrowserSeedPlan PlanBlockedBrowserSeed(IEnumerable<AppRule> existing)
    {
        var fileRules = existing.Where(r => r.MatchType == RuleMatchType.File).ToList();
        var insert = new List<AppRule>();
        var block = new List<long>();
        foreach (var rule in BlockedBrowserRules())
        {
            var match = fileRules.FirstOrDefault(r => string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                insert.Add(rule);
            else if (!match.IsBlocked && match.Category != AppCategory.System)
                block.Add(match.Id);
        }
        return new BlockedBrowserSeedPlan(insert, block);
    }

    /// <summary>The drive root of a folder, for example "C:" for "C:\Windows".</summary>
    private static string SystemDrive(string folder)
    {
        var canonical = WindowsPath.Canonicalize(folder);
        if (canonical is null || canonical.Length < 2 || canonical[1] != ':')
            return "C:";
        return canonical[..2];
    }
}
