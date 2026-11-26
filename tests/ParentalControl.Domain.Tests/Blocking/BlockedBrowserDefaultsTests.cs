using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

public class BlockedBrowserDefaultsTests
{
    [Fact]
    public void List_has_the_15_browsers_of_the_design_and_no_launcher()
    {
        Assert.Equal(15, CategoryDefaults.BlockedBrowsers.Count);
        Assert.DoesNotContain(CategoryDefaults.BlockedBrowsers, n => n.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(15, CategoryDefaults.BlockedBrowsers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("DuckDuckGo.exe", CategoryDefaults.BlockedBrowsers);
    }

    [Fact]
    public void Each_pattern_is_in_its_stored_form_and_valid_as_a_blocked_rule()
    {
        foreach (var rule in CategoryDefaults.BlockedBrowserRules())
        {
            Assert.Equal(rule.Pattern, AppRule.NormalizePattern(RuleMatchType.File, rule.Pattern));
            Assert.Equal(AppCategory.Unknown, rule.Category);
            Assert.True(rule.IsBlocked);
            Assert.Null(AppRule.Validate(rule.MatchType, rule.Pattern, rule.Category, rule.AppLimit, rule.IsBlocked));
        }
    }

    [Fact]
    public void Tor_Browser_and_Opera_GX_are_blocked()
    {
        var resolver = new CategoryResolver(CategoryDefaults.BlockedBrowserRules());
        Assert.True(resolver.Resolve(@"C:\Users\Kid\Desktop\Tor Browser\Browser\firefox.exe").Rule!.IsBlocked);
        Assert.True(resolver.Resolve(@"C:\Users\Kid\AppData\Local\Programs\Opera GX\opera.exe").Rule!.IsBlocked);
    }

    [Fact]
    public void Empty_database_gets_all_rules()
    {
        var plan = CategoryDefaults.PlanBlockedBrowserSeed([]);
        Assert.Equal(15, plan.ToInsert.Count);
        Assert.Empty(plan.ToBlock);
    }

    [Fact]
    public void Existing_Chrome_rule_to_Video_gets_the_block_and_keeps_Video_and_its_App_Limit()
    {
        var chrome = new AppRule(7, RuleMatchType.File, "chrome.exe", AppCategory.Video, new DailyLimit(60, 90));
        var plan = CategoryDefaults.PlanBlockedBrowserSeed([chrome]);
        Assert.Equal([7L], plan.ToBlock);
        Assert.DoesNotContain(plan.ToInsert, r => r.Pattern == "chrome.exe");
        Assert.Equal(14, plan.ToInsert.Count);
    }

    [Fact]
    public void Existing_rule_with_another_letter_case_gets_the_block()
    {
        var plan = CategoryDefaults.PlanBlockedBrowserSeed([new AppRule(8, RuleMatchType.File, "CHROME.EXE", AppCategory.Unknown)]);
        Assert.Equal([8L], plan.ToBlock);
        Assert.DoesNotContain(plan.ToInsert, r => r.Pattern.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Existing_blocked_rule_and_folder_rules_add_nothing_and_block_nothing_new()
    {
        var plan = CategoryDefaults.PlanBlockedBrowserSeed([
            new AppRule(1, RuleMatchType.File, "firefox.exe", AppCategory.Unknown, null, IsBlocked: true),
            new AppRule(2, RuleMatchType.Folder, @"C:\chrome.exe\", AppCategory.Games),
        ]);
        Assert.Empty(plan.ToBlock);
        Assert.Equal(14, plan.ToInsert.Count);
        Assert.Contains(plan.ToInsert, r => r.Pattern == "chrome.exe");
    }

    [Fact]
    public void Existing_System_rule_keeps_no_block()
    {
        var plan = CategoryDefaults.PlanBlockedBrowserSeed([new AppRule(3, RuleMatchType.File, "browser.exe", AppCategory.System)]);
        Assert.Empty(plan.ToBlock);
        Assert.DoesNotContain(plan.ToInsert, r => r.Pattern == "browser.exe");
    }
}
