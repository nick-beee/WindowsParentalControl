using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

// Requirement: Tell the child about a suspension (category-limits).
public class SuspensionNoticeTests
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    private static readonly AppRule TestGame = new(20, RuleMatchType.File, "TestGame.exe", AppCategory.Games, new DailyLimit(120, 120));
    private static readonly AppRule SteamGame = new(21, RuleMatchType.Folder, @"C:\Steam\steamapps\common\Puzzle\", AppCategory.Games, new DailyLimit(60, 60));

    private static DayLimits Day(int gamesUsage, params AppRule[] rules) =>
        new CategoryLimitCalculator(new Dictionary<AppCategory, DailyLimit> { [AppCategory.Games] = new(180, 180) }, rules)
            .Evaluate(Tuesday, new Dictionary<AppCategory, int> { [AppCategory.Games] = gamesUsage }, 0);

    private static SuspensionNotice Notice(DayLimits day, string exe, params AppRule[] rules)
    {
        var resolution = new CategoryResolver(rules).Resolve(exe);
        return SuspensionNotice.For(resolution.Category, day.LimitFor(resolution), "Spiele");
    }

    [Fact]
    public void A_Category_Limit_names_the_category()
    {
        var rule = new AppRule(22, RuleMatchType.File, "OtherGame.exe", AppCategory.Games);
        var notice = Notice(Day(180, TestGame, rule), @"C:\Games\OtherGame.exe", TestGame, rule);

        Assert.Equal("cat:Games", notice.Key);
        Assert.Equal("Spiele", notice.Name);
        Assert.Equal("Spiele", notice.CategoryName);
        Assert.False(notice.IsAppLimit);
    }

    [Fact]
    public void An_App_Limit_names_the_app()
    {
        var notice = Notice(Day(120, TestGame), @"C:\Games\TestGame.exe", TestGame);

        Assert.Equal("rule:20", notice.Key);
        Assert.Equal("TestGame", notice.Name);
        Assert.Equal("Spiele", notice.CategoryName);
        Assert.True(notice.IsAppLimit);
    }

    [Fact]
    public void An_App_Limit_of_a_folder_rule_names_the_last_folder()
    {
        var notice = Notice(Day(60, SteamGame), @"C:\Steam\steamapps\common\Puzzle\bin\puzzle.exe", SteamGame);

        Assert.Equal("rule:21", notice.Key);
        Assert.Equal("Puzzle", notice.Name);
    }

    [Fact]
    public void Two_processes_of_one_App_Limit_give_one_key()
    {
        var day = Day(60, SteamGame);
        var first = Notice(day, @"C:\Steam\steamapps\common\Puzzle\puzzle.exe", SteamGame);
        var second = Notice(day, @"C:\Steam\steamapps\common\Puzzle\editor.exe", SteamGame);

        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void An_App_Limit_and_the_Category_Limit_of_one_category_give_two_keys()
    {
        var day = Day(180, TestGame);
        var app = Notice(day, @"C:\Games\TestGame.exe", TestGame);
        var category = Notice(day, @"C:\Games\Other.exe", TestGame);

        Assert.NotEqual(app.Key, category.Key);
        Assert.Equal("TestGame", app.Name);
        Assert.Equal("Spiele", category.Name);
    }

    [Fact]
    public void Without_a_limit_the_notice_falls_back_to_the_category()
    {
        var notice = SuspensionNotice.For(AppCategory.Video, null, "Videos");

        Assert.Equal("cat:Video", notice.Key);
        Assert.Equal("Videos", notice.Name);
        Assert.False(notice.IsAppLimit);
    }

    [Theory]
    [InlineData(RuleMatchType.File, "TestGame.exe", "TestGame")]
    [InlineData(RuleMatchType.File, "Roblox.EXE", "Roblox")]
    [InlineData(RuleMatchType.File, "tool", "tool")]
    [InlineData(RuleMatchType.Folder, @"C:\Games\Minecraft\", "Minecraft")]
    public void DisplayName_of_a_rule(RuleMatchType type, string pattern, string expected) =>
        Assert.Equal(expected, new AppRule(1, type, pattern, AppCategory.Games).DisplayName);
}
