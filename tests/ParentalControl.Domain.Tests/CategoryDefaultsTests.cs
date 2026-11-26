using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class CategoryDefaultsTests
{
    private static readonly DefaultRuleFolders Folders = new(
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Program Files\ParentalControl");

    [Theory]
    [InlineData(AppCategory.Games)]
    [InlineData(AppCategory.Video)]
    [InlineData(AppCategory.Social)]
    [InlineData(AppCategory.Unknown)]
    public void Limited_categories_get_120_on_weekdays_and_180_on_weekends(AppCategory category) =>
        Assert.Equal(new DailyLimit(120, 180), CategoryDefaults.Limits[category]);

    [Theory]
    [InlineData(AppCategory.Study)]
    [InlineData(AppCategory.Creative)]
    [InlineData(AppCategory.System)]
    public void Other_categories_have_no_limit(AppCategory category) =>
        Assert.True(CategoryDefaults.Limits[category].IsNone);

    [Fact]
    public void Every_category_has_a_default_limit() =>
        Assert.Equal(AppCategories.All.Count, CategoryDefaults.Limits.Count);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe", AppCategory.Games)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteClient-Win64-Shipping.exe", AppCategory.Games)]
    [InlineData(@"C:\Riot Games\League of Legends\LeagueClient.exe", AppCategory.Games)]
    [InlineData(@"C:\Users\Kid\AppData\Local\Roblox\Versions\v1\RobloxPlayerBeta.exe", AppCategory.Games)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.MinecraftUWP\Minecraft.Windows.exe", AppCategory.Games)]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", AppCategory.Study)]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\excel.exe", AppCategory.Study)]
    [InlineData(@"C:\Windows\explorer.exe", AppCategory.System)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", AppCategory.System)]
    [InlineData(@"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe", AppCategory.System)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", AppCategory.Unknown)]
    [InlineData(@"C:\Users\Kid\Downloads\new.exe", AppCategory.Unknown)]
    public void Default_rules_resolve_known_programs(string exePath, AppCategory expected)
    {
        var resolver = new CategoryResolver(CategoryDefaults.Rules(Folders));
        Assert.Equal(expected, resolver.Resolve(exePath).Category);
    }

    [Fact]
    public void Default_rules_have_valid_patterns_and_no_App_Limit()
    {
        foreach (var rule in CategoryDefaults.Rules(Folders))
        {
            Assert.Null(AppRule.Validate(rule.MatchType, rule.Pattern, rule.Category, rule.AppLimit));
            Assert.Equal(rule.Pattern, AppRule.NormalizePattern(rule.MatchType, rule.Pattern));
            Assert.Null(rule.AppLimit);
        }
    }

    [Fact]
    public void Default_rules_have_no_duplicate_pattern()
    {
        var rules = CategoryDefaults.Rules(Folders);
        var keys = rules.Select(r => (r.MatchType, r.Pattern.ToUpperInvariant())).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
