using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

public class BlockedSuspensionPlannerTests
{
    private const int KidUser = 2;
    private const int KidSession = 1;
    private const int AdminSession = 3;
    private const string Agent = @"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";
    private const string WinNotepad = @"C:\Windows\System32\notepad.exe";
    private const string WebView = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe";
    private const string OwnTool = @"C:\Program Files\ParentalControl\service\tool.exe";
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private static readonly DateTime T0 = new(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly ProtectedPrograms Protected = new(@"C:\Windows", [@"C:\Program Files\ParentalControl"], new TrustedWebView2(@"C:\Program Files (x86)", @"C:\Program Files"));
    private static readonly Dictionary<int, int> KidSessions = new() { [KidSession] = KidUser };

    private static List<AppRule> Rules(bool chromeBlocked = true) =>
    [
        new(1, RuleMatchType.File, "chrome.exe", AppCategory.Unknown, null, chromeBlocked),
        new(2, RuleMatchType.File, "firefox.exe", AppCategory.Unknown, null, IsBlocked: true),
        new(3, RuleMatchType.File, "notepad.exe", AppCategory.Unknown, null, IsBlocked: true),
        new(4, RuleMatchType.Folder, @"C:\Program Files (x86)\Microsoft\", AppCategory.Unknown, null, IsBlocked: true),
        new(5, RuleMatchType.File, "tool.exe", AppCategory.Unknown, null, IsBlocked: true),
    ];

    /// <summary>The verdict as the enforcer builds it.</summary>
    private static Func<int, string, ExeVerdict> Verdict(List<AppRule> rules, int unknownUsed = 0)
    {
        var resolver = new CategoryResolver(rules);
        var day = new CategoryLimitCalculator(CategoryDefaults.Limits, rules)
            .Evaluate(Tuesday, new Dictionary<AppCategory, int> { [AppCategory.Unknown] = unknownUsed }, 0);
        return (_, path) =>
        {
            var r = resolver.Resolve(path);
            return new ExeVerdict(r.Category, day.IsExhausted(r), BlockedStartDecider.IsBlockedProgram(r, Protected));
        };
    }

    private static SessionProcess Proc(int pid, string path, int session = KidSession, bool suspended = false) =>
        new(pid, T0, path, session, suspended);

    private static SuspensionRecord Record(int pid, string path, SuspensionReason reason) =>
        new(pid, T0, KidSession, KidUser, AppCategory.Unknown, path, reason);

    private static SuspensionPlan Plan(IEnumerable<SessionProcess> processes, Func<int, string, ExeVerdict> verdict,
        IEnumerable<SuspensionRecord>? records = null, Func<int, string, bool>? seen = null, Func<SessionProcess, bool>? pendingClose = null,
        ProtectedPrograms? protectedPrograms = null, IReadOnlySet<int>? held = null) =>
        SuspensionPlanner.Plan(new PlannerInput(processes.ToList(), KidSessions, verdict, seen ?? ((_, _) => false),
            (records ?? []).ToList(), Agent, @"C:\Windows", (protectedPrograms ?? Protected).TrustedWebView2, held, pendingClose,
            protectedPrograms ?? Protected));

    [Fact]
    public void Blocked_process_is_suspended_without_a_Foreground_Window()
    {
        var plan = Plan([Proc(10, Chrome)], Verdict(Rules()));
        var action = Assert.Single(plan.ToSuspend);
        Assert.Equal(SuspensionReason.Blocked, action.Reason);
        Assert.False(action.IsRecorded);
    }

    [Fact]
    public void Blocked_browser_in_Unknown_with_time_left_stays_suspended()
    {
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(), unknownUsed: 0),
            [Record(10, Chrome, SuspensionReason.Blocked)]);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToSuspend);
        Assert.Empty(plan.ToRelabel);
    }

    [Fact]
    public void Record_after_a_service_start_with_an_empty_Foreground_history_stays_suspended()
    {
        var plan = Plan([Proc(10, Firefox, suspended: true)], Verdict(Rules()), [Record(10, Firefox, SuspensionReason.Blocked)],
            seen: (_, _) => false);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToDrop);
    }

    [Fact]
    public void Blocked_process_resumed_by_a_tool_is_suspended_again()
    {
        var plan = Plan([Proc(10, Chrome, suspended: false)], Verdict(Rules()), [Record(10, Chrome, SuspensionReason.Blocked)]);
        var action = Assert.Single(plan.ToSuspend);
        Assert.True(action.IsRecorded);
        Assert.Equal(SuspensionReason.Blocked, action.Reason);
    }

    [Fact]
    public void Session_End_Hold_suspends_no_blocked_process_and_keeps_the_blocked_record()
    {
        // The session ends: the service resumed the recorded Chrome, and a new Firefox process runs. Neither is suspended
        // during the hold, and the record of Chrome stays for the suspension after the hold.
        var plan = Plan([Proc(10, Chrome, suspended: false), Proc(11, Firefox)], Verdict(Rules()),
            [Record(10, Chrome, SuspensionReason.Blocked)], held: new HashSet<int> { KidSession });
        Assert.Empty(plan.ToSuspend);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToDrop);
        Assert.Empty(plan.ToRelabel);
    }

    [Fact]
    public void After_the_hold_the_blocked_processes_are_suspended_again()
    {
        var plan = Plan([Proc(10, Chrome, suspended: false), Proc(11, Firefox)], Verdict(Rules()),
            [Record(10, Chrome, SuspensionReason.Blocked)], held: new HashSet<int>());
        Assert.Equal(2, plan.ToSuspend.Count);
        Assert.All(plan.ToSuspend, a => Assert.Equal(SuspensionReason.Blocked, a.Reason));
    }

    [Fact]
    public void Pending_close_is_skipped()
    {
        var plan = Plan([Proc(10, Chrome), Proc(11, Chrome)], Verdict(Rules()), pendingClose: p => p.Pid == 10);
        Assert.Equal(11, Assert.Single(plan.ToSuspend).Process.Pid);
    }

    [Fact]
    public void Block_ended_with_time_left_resumes_the_process()
    {
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(chromeBlocked: false)), [Record(10, Chrome, SuspensionReason.Blocked)]);
        Assert.Equal(10, Assert.Single(plan.ToResume).Pid);
    }

    [Fact]
    public void Block_ended_without_time_left_and_with_a_Foreground_Window_changes_to_Limit()
    {
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(chromeBlocked: false), unknownUsed: 120),
            [Record(10, Chrome, SuspensionReason.Blocked)], seen: (_, path) => path == Chrome);
        Assert.Empty(plan.ToResume);
        Assert.Equal(SuspensionReason.Limit, Assert.Single(plan.ToRelabel).Reason);
    }

    [Fact]
    public void Block_ended_without_time_left_and_without_a_Foreground_Window_resumes()
    {
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(chromeBlocked: false), unknownUsed: 120),
            [Record(10, Chrome, SuspensionReason.Blocked)], seen: (_, _) => false);
        Assert.Single(plan.ToResume);
        Assert.Empty(plan.ToRelabel);
    }

    [Fact]
    public void Limit_record_of_an_exe_that_becomes_blocked_changes_to_Blocked()
    {
        // Without the change, the limit reset of the next day would resume a blocked browser.
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(), unknownUsed: 0), [Record(10, Chrome, SuspensionReason.Limit)]);
        Assert.Empty(plan.ToResume);
        Assert.Equal(SuspensionReason.Blocked, Assert.Single(plan.ToRelabel).Reason);
    }

    [Fact]
    public void Limit_record_without_a_block_keeps_the_stage_1_behaviour()
    {
        var plan = Plan([Proc(10, Chrome, suspended: true)], Verdict(Rules(chromeBlocked: false), unknownUsed: 120),
            [Record(10, Chrome, SuspensionReason.Limit)], seen: (_, _) => false);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToRelabel);
    }

    [Fact]
    public void Service_stop_resume_is_not_part_of_the_plan_but_a_dropped_record_is()
    {
        // A blocked record of an ended process is dropped.
        var plan = Plan([], Verdict(Rules()), [Record(10, Chrome, SuspensionReason.Blocked)]);
        Assert.Single(plan.ToDrop);
    }

    [Fact]
    public void Admin_session_is_never_suspended_for_a_block()
    {
        var plan = Plan([Proc(10, Chrome, session: AdminSession)], Verdict(Rules()));
        Assert.Empty(plan.ToSuspend);
    }

    // Task 3.3: a protected exe is never in ToSuspend because of a block, also if a blocked rule matches it.

    [Theory]
    [InlineData(WinNotepad)]
    [InlineData(WebView)]
    [InlineData(OwnTool)]
    [InlineData(Agent)]
    public void Protected_exe_is_never_suspended_for_a_block(string path)
    {
        // A verdict that says "blocked" for every exe: the planner itself must check the protection.
        Func<int, string, ExeVerdict> everythingBlocked = (_, _) => new ExeVerdict(AppCategory.Unknown, false, IsBlocked: true);
        Assert.Empty(Plan([Proc(10, path)], everythingBlocked).ToSuspend);
        Assert.Empty(Plan([Proc(10, path)], Verdict(Rules())).ToSuspend);
    }

    [Fact]
    public void Protected_exe_with_a_blocked_record_is_resumed()
    {
        Func<int, string, ExeVerdict> everythingBlocked = (_, _) => new ExeVerdict(AppCategory.Unknown, false, IsBlocked: true);
        var plan = Plan([Proc(10, OwnTool, suspended: true)], everythingBlocked, [Record(10, OwnTool, SuspensionReason.Blocked)]);
        Assert.Single(plan.ToResume);
    }

    [Fact]
    public void Blocked_System_verdict_is_ignored()
    {
        Func<int, string, ExeVerdict> systemBlocked = (_, _) => new ExeVerdict(AppCategory.System, false, IsBlocked: true);
        Assert.Empty(Plan([Proc(10, Chrome)], systemBlocked).ToSuspend);
    }
}
