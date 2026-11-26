using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class CategoryLimitCalculatorTests
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private static readonly DateOnly Saturday = new(2026, 11, 28);
    private static readonly DateOnly Sunday = new(2026, 11, 29);

    private static readonly AppRule Minecraft = new(10, RuleMatchType.File, "Minecraft.Windows.exe", AppCategory.Games, new DailyLimit(180, 240));
    private static readonly AppRule Roblox = new(11, RuleMatchType.File, "RobloxPlayerBeta.exe", AppCategory.Games, new DailyLimit(60, 60));
    private static readonly AppRule Steam = new(12, RuleMatchType.Folder, @"C:\Steam\steamapps\common\", AppCategory.Games);

    private static CategoryLimitCalculator Calculator(params AppRule[] rules) => new(CategoryDefaults.Limits, rules);

    private static CategoryResolution Resolve(string exe, params AppRule[] rules) => new CategoryResolver(rules).Resolve(exe);

    private static readonly Dictionary<AppCategory, int> NoUsage = new();

    private static Dictionary<AppCategory, int> Usage(AppCategory c, int minutes) => new() { [c] = minutes };

    // Requirement: Effective Category Limit of the day

    [Fact]
    public void Saturday_uses_the_weekend_limit()
    {
        var day = Calculator().Evaluate(Saturday, Usage(AppCategory.Games, 0), 0);
        Assert.Equal(180, day.CategoryLimit(AppCategory.Games)!.LimitMinutes);
    }

    [Fact]
    public void Sunday_uses_the_weekend_limit_and_Tuesday_the_weekday_limit()
    {
        Assert.Equal(180, Calculator().Evaluate(Sunday, NoUsage, 0).CategoryLimit(AppCategory.Games)!.LimitMinutes);
        Assert.Equal(120, Calculator().Evaluate(Tuesday, NoUsage, 0).CategoryLimit(AppCategory.Games)!.LimitMinutes);
    }

    [Fact]
    public void Bonus_Minutes_add_to_the_limit()
    {
        var day = Calculator().Evaluate(Tuesday, Usage(AppCategory.Games, 0), 30);
        Assert.Equal(150, day.CategoryLimit(AppCategory.Games)!.LimitMinutes);
    }

    [Fact]
    public void Category_without_a_limit_has_no_Category_Limit()
    {
        var day = Calculator().Evaluate(Tuesday, Usage(AppCategory.Study, 500), 0);
        Assert.Null(day.CategoryLimit(AppCategory.Study));
        Assert.False(day.IsExhausted(Resolve(@"C:\Office\WINWORD.EXE", new AppRule(1, RuleMatchType.File, "WINWORD.EXE", AppCategory.Study))));
    }

    [Fact]
    public void System_never_has_a_limit_also_if_the_store_has_one()
    {
        var limits = new Dictionary<AppCategory, DailyLimit> { [AppCategory.System] = new(10, 10) };
        var rule = new AppRule(1, RuleMatchType.Folder, @"C:\Windows\", AppCategory.System, new DailyLimit(5, 5));
        var day = new CategoryLimitCalculator(limits, [rule]).Evaluate(Tuesday, Usage(AppCategory.System, 999), 0);
        Assert.Empty(day.All);
        Assert.False(day.IsExhausted(Resolve(@"C:\Windows\explorer.exe", rule)));
    }

    [Fact]
    public void Limit_of_zero_is_reached_at_once()
    {
        var limits = new Dictionary<AppCategory, DailyLimit> { [AppCategory.Video] = new(0, 0) };
        var day = new CategoryLimitCalculator(limits, []).Evaluate(Tuesday, NoUsage, 0);
        Assert.True(day.CategoryLimit(AppCategory.Video)!.IsReached);
    }

    // Requirement: Warnings before a Category Limit ends

    [Fact]
    public void Five_minutes_left()
    {
        var status = Calculator().Evaluate(Tuesday, Usage(AppCategory.Games, 115), 0).CategoryLimit(AppCategory.Games)!;
        Assert.Equal(5, status.RemainingMinutes);
        Assert.Equal(LimitWarning.FiveMinutes, status.DueWarning);
        Assert.False(status.IsReached);
    }

    [Theory]
    [InlineData(100, LimitWarning.None)]
    [InlineData(114, LimitWarning.None)]
    [InlineData(115, LimitWarning.FiveMinutes)]
    [InlineData(118, LimitWarning.FiveMinutes)]
    [InlineData(119, LimitWarning.OneMinute)]
    [InlineData(120, LimitWarning.None)]
    [InlineData(130, LimitWarning.None)]
    public void Warnings_by_remaining_minutes(int used, LimitWarning expected) =>
        Assert.Equal(expected, Calculator().Evaluate(Tuesday, Usage(AppCategory.Games, used), 0).CategoryLimit(AppCategory.Games)!.DueWarning);

    [Fact]
    public void App_Limit_has_its_own_warning_with_its_rule()
    {
        var day = Calculator(Minecraft).Evaluate(Tuesday, Usage(AppCategory.Games, 175), 0);
        var appStatus = day.All.Single(s => s.IsAppLimit);
        Assert.Same(Minecraft, appStatus.Rule);
        Assert.Equal(LimitWarning.FiveMinutes, appStatus.DueWarning);
        Assert.Equal("rule:10", appStatus.Key);
        Assert.Equal("cat:Games", day.CategoryLimit(AppCategory.Games)!.Key);
    }

    // An App Limit warning needs a running process of its app (issue #12).

    private static readonly AppRule RobloxAt120 = new(20, RuleMatchType.File, "RobloxPlayerBeta.exe", AppCategory.Games, new DailyLimit(120, 120));
    private static readonly AppRule GamesAt180 = new(21, RuleMatchType.Folder, @"C:\Games\", AppCategory.Games);

    private static CategoryLimitCalculator CalculatorWithGames180(params AppRule[] rules) =>
        new(new Dictionary<AppCategory, DailyLimit> { [AppCategory.Games] = new(180, 180) }, rules);

    [Fact]
    public void App_not_running_gives_no_App_Limit_warning()
    {
        var day = CalculatorWithGames180(RobloxAt120).Evaluate(Tuesday, Usage(AppCategory.Games, 115), 0);
        var running = new[] { Resolve(@"C:\Games\Other\Other.exe", RobloxAt120, GamesAt180) };

        Assert.Empty(day.DueWarnings(running));
        Assert.Empty(day.DueWarnings([]));
    }

    [Fact]
    public void App_running_gives_its_App_Limit_warning()
    {
        var day = CalculatorWithGames180(RobloxAt120).Evaluate(Tuesday, Usage(AppCategory.Games, 117), 0);
        var running = new[] { Resolve(@"C:\Users\Kid\AppData\Local\Roblox\RobloxPlayerBeta.exe", RobloxAt120) };

        var warning = Assert.Single(day.DueWarnings(running));
        Assert.Same(RobloxAt120, warning.Rule);
        Assert.Equal(LimitWarning.FiveMinutes, warning.DueWarning);
        Assert.Equal(3, warning.RemainingMinutes);
    }

    [Fact]
    public void Category_Limit_warning_needs_no_running_program()
    {
        var day = Calculator().Evaluate(Tuesday, Usage(AppCategory.Games, 115), 0);

        var warning = Assert.Single(day.DueWarnings([]));
        Assert.Null(warning.Rule);
        Assert.Equal(AppCategory.Games, warning.Category);
    }

    [Fact]
    public void Category_and_App_Limit_warnings_together()
    {
        var day = Calculator(Minecraft).Evaluate(Tuesday, Usage(AppCategory.Games, 175), 30);
        var running = new[] { Resolve(@"C:\Minecraft\Minecraft.Windows.exe", Minecraft) };

        // With 30 Bonus Minutes: Games 150 is reached (no warning), Minecraft 210 has 35 minutes left (no warning).
        Assert.Empty(day.DueWarnings(running));

        day = Calculator(Minecraft).Evaluate(Tuesday, Usage(AppCategory.Games, 179), 0);
        Assert.Equal(["rule:10"], day.DueWarnings(running).Select(s => s.Key));
        Assert.Empty(day.DueWarnings([]));
    }

    [Fact]
    public void Folder_rule_App_Limit_covers_an_exe_below_its_folder()
    {
        var steam = new AppRule(30, RuleMatchType.Folder, @"C:\Steam\steamapps\common\", AppCategory.Games, new DailyLimit(120, 120));
        var day = CalculatorWithGames180(steam).Evaluate(Tuesday, Usage(AppCategory.Games, 119), 0);
        var running = new[] { Resolve(@"C:\Steam\steamapps\common\Portal\portal.exe", steam) };

        var warning = Assert.Single(day.DueWarnings(running));
        Assert.Same(steam, warning.Rule);
        Assert.Equal(LimitWarning.OneMinute, warning.DueWarning);
    }

    [Fact]
    public void File_rule_that_wins_over_a_folder_rule_takes_its_App_Limit_warning_away()
    {
        var steam = new AppRule(30, RuleMatchType.Folder, @"C:\Steam\steamapps\common\", AppCategory.Games, new DailyLimit(120, 120));
        var portal = new AppRule(31, RuleMatchType.File, "portal.exe", AppCategory.Games);
        var day = CalculatorWithGames180(steam, portal).Evaluate(Tuesday, Usage(AppCategory.Games, 117), 0);
        var running = new[] { Resolve(@"C:\Steam\steamapps\common\Portal\portal.exe", steam, portal) };

        Assert.Empty(day.DueWarnings(running));
    }

    [Fact]
    public void Reached_App_Limit_gives_no_warning_also_if_the_app_runs()
    {
        var day = CalculatorWithGames180(RobloxAt120).Evaluate(Tuesday, Usage(AppCategory.Games, 120), 0);
        var running = new[] { Resolve(@"C:\Roblox\RobloxPlayerBeta.exe", RobloxAt120) };

        Assert.Empty(day.DueWarnings(running));
    }

    // Requirement: App Limit replaces the Category Limit for its app

    [Fact]
    public void Game_with_a_longer_App_Limit_keeps_running_at_the_Category_Limit()
    {
        var day = Calculator(Minecraft, Steam).Evaluate(Tuesday, Usage(AppCategory.Games, 120), 0);
        Assert.True(day.IsExhausted(Resolve(@"C:\Steam\steamapps\common\Portal\portal.exe", Minecraft, Steam)));
        Assert.False(day.IsExhausted(Resolve(@"C:\Apps\Minecraft.Windows.exe", Minecraft, Steam)));
        Assert.Equal(60, day.LimitFor(Resolve(@"C:\Apps\Minecraft.Windows.exe", Minecraft, Steam))!.RemainingMinutes);
    }

    [Fact]
    public void Longer_App_Limit_reached()
    {
        var day = Calculator(Minecraft).Evaluate(Tuesday, Usage(AppCategory.Games, 180), 0);
        Assert.True(day.IsExhausted(Resolve(@"C:\Apps\Minecraft.Windows.exe", Minecraft)));
    }

    [Fact]
    public void App_Limit_uses_its_weekend_value_and_the_Bonus_Minutes()
    {
        var day = Calculator(Minecraft).Evaluate(Saturday, Usage(AppCategory.Games, 240), 15);
        var status = day.LimitFor(Resolve(@"C:\Apps\Minecraft.Windows.exe", Minecraft))!;
        Assert.Equal(255, status.LimitMinutes);
        Assert.False(status.IsReached);
    }

    [Fact]
    public void App_Limit_shorter_than_the_category()
    {
        var day = Calculator(Roblox, Steam).Evaluate(Tuesday, Usage(AppCategory.Games, 60), 0);
        Assert.True(day.IsExhausted(Resolve(@"C:\Roblox\RobloxPlayerBeta.exe", Roblox, Steam)));
        Assert.False(day.IsExhausted(Resolve(@"C:\Steam\steamapps\common\Portal\portal.exe", Roblox, Steam)));
    }

    [Fact]
    public void App_Limit_without_a_value_for_today_falls_back_to_the_Category_Limit()
    {
        var weekendOnly = new AppRule(20, RuleMatchType.File, "game.exe", AppCategory.Games, new DailyLimit(null, 300));
        var day = Calculator(weekendOnly).Evaluate(Tuesday, Usage(AppCategory.Games, 120), 0);
        var status = day.LimitFor(Resolve(@"C:\x\game.exe", weekendOnly))!;
        Assert.False(status.IsAppLimit);
        Assert.True(status.IsReached);
    }

    [Fact]
    public void Unknown_exe_uses_the_Unknown_limit()
    {
        var day = Calculator().Evaluate(Tuesday, Usage(AppCategory.Unknown, 120), 0);
        Assert.True(day.IsExhausted(Resolve(@"C:\Users\Kid\Downloads\new.exe")));
    }
}
