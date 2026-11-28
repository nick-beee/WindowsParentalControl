using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

public class BlockRuleTests
{
    private static readonly ProtectedPrograms Protected = new(@"C:\Windows", [@"C:\Program Files\ParentalControl"], new TrustedWebView2(@"C:\Program Files (x86)", @"C:\Program Files"));

    private static string? Validate(RuleMatchType type, string pattern, AppCategory category, DailyLimit? limit = null) =>
        AppRule.Validate(type, pattern, category, limit, isBlocked: true, Protected);

    // Requirement: Refuse a block that cannot apply

    [Fact]
    public void Block_on_System_is_refused() =>
        Assert.NotNull(Validate(RuleMatchType.File, "tool.exe", AppCategory.System));

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\windows\System32\")]
    [InlineData(@"C:\Program Files\ParentalControl")]
    [InlineData(@"C:\Program Files\ParentalControl\agent")]
    public void Blocked_folder_rule_for_a_protected_folder_is_refused(string folder) =>
        Assert.NotNull(Validate(RuleMatchType.Folder, folder, AppCategory.Unknown));

    [Theory]
    [InlineData("msedgewebview2.exe")]
    [InlineData("MSEdgeWebView2.exe")]
    [InlineData("ParentalControl.Service.exe")]
    [InlineData("parentalcontrol.agent.exe")]
    [InlineData("ParentalControl.Admin.exe")]
    public void Blocked_file_rule_for_a_protected_name_is_refused(string name)
    {
        Assert.NotNull(Validate(RuleMatchType.File, name, AppCategory.Unknown));
        // Without the protected folders, the file names are still checked.
        Assert.NotNull(AppRule.Validate(RuleMatchType.File, name, AppCategory.Unknown, null, isBlocked: true));
    }

    [Fact]
    public void Blocked_notepad_file_rule_is_permitted() =>
        Assert.Null(Validate(RuleMatchType.File, "notepad.exe", AppCategory.Unknown));

    [Fact]
    public void Blocked_rule_keeps_its_App_Limit() =>
        Assert.Null(Validate(RuleMatchType.File, "chrome.exe", AppCategory.Video, new DailyLimit(60, 90)));

    [Fact]
    public void Blocked_folder_rule_outside_the_protected_folders_is_permitted()
    {
        Assert.Null(Validate(RuleMatchType.Folder, @"C:\Tools", AppCategory.Unknown));
        Assert.Null(Validate(RuleMatchType.Folder, @"C:\WindowsApps2", AppCategory.Unknown));
    }

    [Fact]
    public void Rules_without_the_block_are_not_checked_for_protection()
    {
        Assert.Null(AppRule.Validate(RuleMatchType.Folder, @"C:\Windows", AppCategory.System, null, isBlocked: false, Protected));
        Assert.Null(AppRule.Validate(RuleMatchType.File, "msedgewebview2.exe", AppCategory.Study, null));
    }

    // Requirement: The block follows the rule precedence

    [Fact]
    public void File_rule_without_the_block_makes_an_exception_in_a_blocked_folder()
    {
        var resolver = new CategoryResolver([
            new AppRule(1, RuleMatchType.Folder, @"C:\Tools\", AppCategory.Unknown, IsBlocked: true),
            new AppRule(2, RuleMatchType.File, "calc2.exe", AppCategory.Study),
        ]);
        var r = resolver.Resolve(@"C:\Tools\calc2.exe");
        Assert.False(r.Rule!.IsBlocked);
        Assert.Equal(AppCategory.Study, r.Category);
        Assert.True(resolver.Resolve(@"C:\Tools\other.exe").Rule!.IsBlocked);
    }

    [Fact]
    public void Longest_folder_decides_the_block()
    {
        var resolver = new CategoryResolver([
            new AppRule(1, RuleMatchType.Folder, @"C:\Apps\", AppCategory.Unknown, IsBlocked: true),
            new AppRule(2, RuleMatchType.Folder, @"C:\Apps\School\", AppCategory.Study),
        ]);
        Assert.False(resolver.Resolve(@"C:\Apps\School\x.exe").Rule!.IsBlocked);
        Assert.True(resolver.Resolve(@"C:\Apps\Other\y.exe").Rule!.IsBlocked);
    }

    [Fact]
    public void Blocked_file_rule_wins_over_an_unblocked_folder()
    {
        var resolver = new CategoryResolver([
            new AppRule(1, RuleMatchType.Folder, @"C:\Program Files\Google\", AppCategory.Study),
            new AppRule(2, RuleMatchType.File, "chrome.exe", AppCategory.Unknown, IsBlocked: true),
        ]);
        Assert.True(resolver.Resolve(@"C:\Program Files\Google\Chrome\Application\chrome.exe").Rule!.IsBlocked);
        Assert.False(resolver.Resolve(@"C:\Program Files\Google\Drive\drive.exe").Rule!.IsBlocked);
    }

    // Requirement: Block on an App Rule (design D1: the App Limit has no effect while the block is on)

    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public void Blocked_rule_gives_no_App_Limit_and_no_App_Limit_warning()
    {
        var chrome = new AppRule(5, RuleMatchType.File, "chrome.exe", AppCategory.Video, new DailyLimit(60, 60), IsBlocked: true);
        var day = new CategoryLimitCalculator(CategoryDefaults.Limits, [chrome])
            .Evaluate(Tuesday, new Dictionary<AppCategory, int> { [AppCategory.Video] = 57 }, 0);
        var resolution = new CategoryResolver([chrome]).Resolve(@"C:\Chrome\chrome.exe");

        Assert.DoesNotContain(day.All, s => s.IsAppLimit);
        Assert.DoesNotContain(day.DueWarnings([resolution]), s => s.IsAppLimit);
        // The exe uses the Category Limit of Video (120 on a weekday).
        Assert.Equal(120, day.LimitFor(resolution)!.LimitMinutes);
    }

    [Fact]
    public void App_Limit_applies_again_without_the_block()
    {
        var chrome = new AppRule(5, RuleMatchType.File, "chrome.exe", AppCategory.Video, new DailyLimit(60, 60));
        var day = new CategoryLimitCalculator(CategoryDefaults.Limits, [chrome])
            .Evaluate(Tuesday, new Dictionary<AppCategory, int> { [AppCategory.Video] = 57 }, 0);
        var resolution = new CategoryResolver([chrome]).Resolve(@"C:\Chrome\chrome.exe");
        Assert.Equal(60, day.LimitFor(resolution)!.LimitMinutes);
        Assert.Contains(day.DueWarnings([resolution]), s => s.IsAppLimit);
    }
}
