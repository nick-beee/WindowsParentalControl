using System.Reflection;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

public class BlockedStartDeciderTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private static readonly ProtectedPrograms Protected = new(@"C:\Windows", [@"C:\Program Files\ParentalControl"]);
    private static readonly DateTime Epoch = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    private static readonly CategoryResolver Resolver = new([
        new AppRule(1, RuleMatchType.File, "chrome.exe", AppCategory.Unknown, IsBlocked: true),
        new AppRule(2, RuleMatchType.File, "notepad.exe", AppCategory.Unknown, IsBlocked: true),
        new AppRule(3, RuleMatchType.File, "msedgewebview2.exe", AppCategory.Unknown, IsBlocked: true),
        new AppRule(4, RuleMatchType.File, "game.exe", AppCategory.Games),
        new AppRule(5, RuleMatchType.File, "systool.exe", AppCategory.System, IsBlocked: true),
    ]);

    private static BlockDecision Decide(string? path, double createdAfterEpochSeconds = 5, double detectedAfterCreationSeconds = 1,
        bool restricted = true, BlockParent? parent = null)
    {
        var created = Epoch.AddSeconds(createdAfterEpochSeconds);
        return BlockedStartDecider.Decide(new BlockedStartInput(
            Resolver.Resolve(path), restricted, created, created.AddSeconds(detectedAfterCreationSeconds), Epoch, parent), Protected);
    }

    [Fact]
    public void Start_after_the_epoch_detected_after_1_s_is_closed() =>
        Assert.Equal(BlockDecision.Close, Decide(Chrome));

    [Fact]
    public void Start_detected_after_exactly_10_s_is_closed() =>
        Assert.Equal(BlockDecision.Close, Decide(Chrome, detectedAfterCreationSeconds: 10));

    [Fact]
    public void Start_detected_after_11_s_is_suspended() =>
        Assert.Equal(BlockDecision.Suspend, Decide(Chrome, detectedAfterCreationSeconds: 11));

    [Fact]
    public void Start_before_the_epoch_is_suspended() =>
        Assert.Equal(BlockDecision.Suspend, Decide(Chrome, createdAfterEpochSeconds: -2));

    [Fact]
    public void Start_at_the_epoch_is_suspended() =>
        Assert.Equal(BlockDecision.Suspend, Decide(Chrome, createdAfterEpochSeconds: 0));

    [Fact]
    public void Protected_exe_is_ignored()
    {
        Assert.Equal(BlockDecision.Ignore, Decide(@"C:\Windows\System32\notepad.exe"));
        Assert.Equal(BlockDecision.Ignore, Decide(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1\msedgewebview2.exe"));
    }

    [Fact]
    public void Blocked_notepad_outside_the_Windows_folder_is_closed() =>
        Assert.Equal(BlockDecision.Close, Decide(@"C:\Users\Kid\Downloads\notepad.exe"));

    [Fact]
    public void Unrestricted_session_is_ignored() =>
        Assert.Equal(BlockDecision.Ignore, Decide(Chrome, restricted: false));

    [Fact]
    public void Null_path_is_ignored() => Assert.Equal(BlockDecision.Ignore, Decide(null));

    [Fact]
    public void Exe_without_a_block_is_ignored()
    {
        Assert.Equal(BlockDecision.Ignore, Decide(@"C:\Games\game.exe"));
        Assert.Equal(BlockDecision.Ignore, Decide(@"C:\Users\Kid\other.exe"));
    }

    [Fact]
    public void Blocked_System_rule_is_ignored() => Assert.Equal(BlockDecision.Ignore, Decide(@"C:\Tools\systool.exe"));

    [Fact]
    public void New_process_of_a_blocked_browser_that_ran_before_the_epoch_is_suspended() =>
        Assert.Equal(BlockDecision.Suspend, Decide(Chrome, parent: new BlockParent(100, Epoch.AddMinutes(-30), IsBlockedProgram: true)));

    [Fact]
    public void Parent_that_started_at_the_epoch_is_old() =>
        Assert.Equal(BlockDecision.Suspend, Decide(Chrome, parent: new BlockParent(100, Epoch, IsBlockedProgram: true)));

    [Fact]
    public void Reused_parent_pid_with_a_later_start_time_does_not_match() =>
        // The pid of the old browser now belongs to a process that started after the new process: no match.
        Assert.Equal(BlockDecision.Close, Decide(Chrome, createdAfterEpochSeconds: 5,
            parent: new BlockParent(100, Epoch.AddSeconds(6), IsBlockedProgram: true)));

    [Fact]
    public void New_process_whose_parent_is_a_Blocked_Start_is_closed() =>
        Assert.Equal(BlockDecision.Close, Decide(Chrome, createdAfterEpochSeconds: 5,
            parent: new BlockParent(100, Epoch.AddSeconds(4), IsBlockedProgram: true)));

    [Fact]
    public void Old_parent_that_is_not_blocked_does_not_matter() =>
        Assert.Equal(BlockDecision.Close, Decide(Chrome, parent: new BlockParent(100, Epoch.AddHours(-2), IsBlockedProgram: false)));

    // Spec: Process data for a block is untrusted. The decider has no command line input.

    [Fact]
    public void Decider_input_has_no_command_line()
    {
        var names = typeof(BlockedStartInput).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        Assert.Equal(["Resolution", "IsRestrictedSession", "CreatedUtc", "DetectedUtc", "BlockEpochUtc", "Parent"], names);
        Assert.Single(typeof(BlockedStartDecider).GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "Decide");
    }
}

public class SilentStartClassifierTests
{
    [Fact]
    public void Edge_Startup_Boost_is_silent_and_counts_as_a_start()
    {
        var kind = SilentStartClassifier.Classify(
            "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --no-startup-window --win-session-start /prefetch:5");
        Assert.True(kind.IsSilent);
        Assert.False(kind.IsSubProcess);
    }

    [Fact]
    public void Chrome_renderer_is_a_silent_sub_process()
    {
        var kind = SilentStartClassifier.Classify(
            "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --type=renderer --lang=de --renderer-client-id=7 /prefetch:1");
        Assert.True(kind.IsSilent);
        Assert.True(kind.IsSubProcess);
    }

    [Fact]
    public void Normal_start_is_not_silent() =>
        Assert.Equal(new StartKind(false, false),
            SilentStartClassifier.Classify("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_command_line_is_not_silent(string? commandLine) =>
        Assert.Equal(new StartKind(false, false), SilentStartClassifier.Classify(commandLine));

    [Fact]
    public void Flag_inside_a_URL_argument_is_not_silent() =>
        Assert.False(SilentStartClassifier.Classify("chrome.exe https://example.com/?q=--no-startup-window").IsSilent);

    [Fact]
    public void Flag_text_inside_a_quoted_argument_with_spaces_is_not_silent() =>
        Assert.False(SilentStartClassifier.Classify("chrome.exe \"see --no-startup-window here\"").IsSilent);

    [Fact]
    public void Quoted_flag_and_upper_case_are_silent()
    {
        Assert.True(SilentStartClassifier.Classify("msedge.exe \"--no-startup-window\"").IsSilent);
        Assert.True(SilentStartClassifier.Classify("chrome.exe --TYPE=gpu-process").IsSilent);
    }

    [Fact]
    public void Firefox_content_process_is_a_sub_process_but_not_silent()
    {
        var kind = SilentStartClassifier.Classify("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -contentproc -childID 1 -isForBrowser 8 tab");
        Assert.False(kind.IsSilent);
        Assert.True(kind.IsSubProcess);
    }
}
