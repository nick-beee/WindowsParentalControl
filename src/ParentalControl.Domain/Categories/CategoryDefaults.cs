namespace ParentalControl.Domain.Categories;

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

    /// <summary>The drive root of a folder, for example "C:" for "C:\Windows".</summary>
    private static string SystemDrive(string folder)
    {
        var canonical = WindowsPath.Canonicalize(folder);
        if (canonical is null || canonical.Length < 2 || canonical[1] != ':')
            return "C:";
        return canonical[..2];
    }
}
