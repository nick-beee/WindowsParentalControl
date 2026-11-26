using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class CategoryResolverTests
{
    private static AppRule FileRule(long id, string name, AppCategory category) => new(id, RuleMatchType.File, name, category);
    private static AppRule FolderRule(long id, string folder, AppCategory category) => new(id, RuleMatchType.Folder, folder, category);

    // Requirement: App Rules map an exe to a category

    [Fact]
    public void Match_by_exe_file_name_ignores_letter_case()
    {
        var resolver = new CategoryResolver([FileRule(1, "RobloxPlayerBeta.exe", AppCategory.Games)]);
        var result = resolver.Resolve(@"C:\Users\Kid\AppData\Local\Roblox\Versions\v1\robloxplayerbeta.exe");
        Assert.Equal(AppCategory.Games, result.Category);
        Assert.Equal(1, result.Rule!.Id);
    }

    [Fact]
    public void Match_by_folder_prefix()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Program Files (x86)\Steam\steamapps\common\", AppCategory.Games)]);
        var result = resolver.Resolve(@"C:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe");
        Assert.Equal(AppCategory.Games, result.Category);
    }

    [Fact]
    public void Folder_match_ignores_letter_case()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Games\", AppCategory.Games)]);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"c:\games\X.EXE").Category);
    }

    // Requirement: Rule precedence

    [Fact]
    public void File_name_wins_over_folder()
    {
        var resolver = new CategoryResolver([
            FolderRule(1, @"C:\Games\", AppCategory.Games),
            FileRule(2, "editor.exe", AppCategory.Creative),
        ]);
        Assert.Equal(AppCategory.Creative, resolver.Resolve(@"C:\Games\Tool\editor.exe").Category);
    }

    [Fact]
    public void Longest_folder_wins()
    {
        var resolver = new CategoryResolver([
            FolderRule(1, @"C:\Apps\", AppCategory.Study),
            FolderRule(2, @"C:\Apps\Fun\", AppCategory.Games),
        ]);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"C:\Apps\Fun\x.exe").Category);
        Assert.Equal(AppCategory.Study, resolver.Resolve(@"C:\Apps\Work\x.exe").Category);
    }

    [Fact]
    public void Longest_folder_wins_in_any_rule_order()
    {
        var resolver = new CategoryResolver([
            FolderRule(1, @"C:\Apps\Fun\", AppCategory.Games),
            FolderRule(2, @"C:\Apps\", AppCategory.Study),
        ]);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"C:\Apps\Fun\x.exe").Category);
    }

    [Fact]
    public void No_rule_gives_Unknown()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Games\", AppCategory.Games)]);
        var result = resolver.Resolve(@"C:\Users\Kid\Downloads\new.exe");
        Assert.Equal(AppCategory.Unknown, result.Category);
        Assert.Null(result.Rule);
    }

    // Requirement: Folder match on whole folder names

    [Fact]
    public void Similar_folder_name_does_not_match()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Games\", AppCategory.Games)]);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve(@"C:\GamesBackup\x.exe").Category);
    }

    [Fact]
    public void Folder_pattern_without_trailing_separator_still_matches_on_the_boundary()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Games", AppCategory.Games)]);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"C:\Games\x.exe").Category);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve(@"C:\GamesBackup\x.exe").Category);
    }

    [Fact]
    public void Path_with_parent_references_does_not_match_the_folder_it_leaves()
    {
        var resolver = new CategoryResolver([FolderRule(1, @"C:\Windows\", AppCategory.System)]);
        var result = resolver.Resolve(@"C:\Windows\..\Users\Kid\x.exe");
        Assert.Equal(AppCategory.Unknown, result.Category);
        Assert.Equal(@"C:\Users\Kid\x.exe", result.CanonicalPath);
    }

    [Fact]
    public void Relative_path_uses_only_file_name_rules()
    {
        var resolver = new CategoryResolver([
            FolderRule(1, @"C:\Windows\", AppCategory.System),
            FileRule(2, "game.exe", AppCategory.Games),
        ]);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve(@"Windows\x.exe").Category);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"sub\game.exe").Category);
    }

    [Fact]
    public void Empty_path_is_Unknown()
    {
        var resolver = new CategoryResolver([]);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve(null).Category);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve("  ").Category);
    }

    [Fact]
    public void Old_agent_process_name_without_extension_matches_a_file_name_rule()
    {
        var resolver = new CategoryResolver([FileRule(1, "RobloxPlayerBeta.exe", AppCategory.Games)]);
        Assert.Equal(AppCategory.Games, resolver.ResolveFileName("RobloxPlayerBeta").Category);
        Assert.Equal(AppCategory.Games, resolver.ResolveFileName("robloxplayerbeta.exe").Category);
        Assert.Equal(AppCategory.Unknown, resolver.ResolveFileName("notepad").Category);
    }

    [Fact]
    public void Duplicate_pattern_uses_the_lowest_rule_id()
    {
        var resolver = new CategoryResolver([
            FileRule(5, "x.exe", AppCategory.Video),
            FileRule(2, "X.EXE", AppCategory.Games),
        ]);
        Assert.Equal(AppCategory.Games, resolver.Resolve(@"C:\a\x.exe").Category);
    }

    [Fact]
    public void Invalid_patterns_are_ignored()
    {
        var resolver = new CategoryResolver([
            FolderRule(1, @"relative\folder", AppCategory.Games),
            FileRule(2, @"C:\full\path.exe", AppCategory.Games),
        ]);
        Assert.Equal(AppCategory.Unknown, resolver.Resolve(@"C:\full\path.exe").Category);
    }

    [Fact]
    public void Validate_refuses_App_Limit_on_System_and_out_of_range_minutes()
    {
        Assert.Null(AppRule.Validate(RuleMatchType.File, "game.exe", AppCategory.Games, new DailyLimit(60, 90)));
        Assert.NotNull(AppRule.Validate(RuleMatchType.File, "x.exe", AppCategory.System, new DailyLimit(60, null)));
        Assert.Null(AppRule.Validate(RuleMatchType.File, "x.exe", AppCategory.System, DailyLimit.None));
        Assert.NotNull(AppRule.Validate(RuleMatchType.File, "game.exe", AppCategory.Games, new DailyLimit(2000, null)));
        Assert.NotNull(AppRule.Validate(RuleMatchType.File, "game.exe", AppCategory.Games, new DailyLimit(-1, null)));
        Assert.NotNull(AppRule.Validate(RuleMatchType.Folder, "Games", AppCategory.Games, null));
        Assert.NotNull(AppRule.Validate(RuleMatchType.File, @"C:\x.exe", AppCategory.Games, null));
    }

    [Theory]
    [InlineData("Games", true, AppCategory.Games)]
    [InlineData("games", true, AppCategory.Games)]
    [InlineData("3", false, AppCategory.Unknown)]
    [InlineData("Toys", false, AppCategory.Unknown)]
    [InlineData(null, false, AppCategory.Unknown)]
    public void Category_names_parse(string? text, bool ok, AppCategory expected)
    {
        Assert.Equal(ok, AppCategories.TryParse(text, out var category));
        if (ok) Assert.Equal(expected, category);
    }
}
