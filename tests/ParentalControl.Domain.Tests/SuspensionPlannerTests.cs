using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class SuspensionPlannerTests
{
    private const int KidUser = 2;
    private const int KidSession = 1;
    private const int AdminSession = 3;
    private const string Agent = @"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe";
    private const string Game = @"C:\Steam\steamapps\common\Portal\portal.exe";
    private const string Minecraft = @"C:\Apps\Minecraft.Windows.exe";
    private const string Word = @"C:\Office\WINWORD.EXE";
    private const string Helper = @"C:\Users\Kid\AppData\Local\Updater\helper.exe";
    private const string WebView = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe";
    private const string NewExe = @"C:\Users\Kid\Downloads\new.exe";
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private static readonly DateOnly Wednesday = new(2026, 11, 25);
    private static readonly DateTime T0 = new(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);

    private static readonly List<AppRule> Rules =
    [
        new(1, RuleMatchType.Folder, @"C:\Steam\steamapps\common\", AppCategory.Games),
        new(2, RuleMatchType.File, "Minecraft.Windows.exe", AppCategory.Games, new DailyLimit(180, 180)),
        new(3, RuleMatchType.File, "WINWORD.EXE", AppCategory.Study),
        new(4, RuleMatchType.Folder, @"C:\Program Files\ParentalControl\", AppCategory.System),
    ];

    /// <summary>The verdict as the enforcer builds it: the real resolver and calculator.</summary>
    private static Func<int, string, ExeVerdict> Verdict(List<AppRule> rules, DateOnly date, int gamesUsed, int unknownUsed = 0, int bonus = 0)
    {
        var resolver = new CategoryResolver(rules);
        var day = new CategoryLimitCalculator(CategoryDefaults.Limits, rules).Evaluate(
            date, new Dictionary<AppCategory, int> { [AppCategory.Games] = gamesUsed, [AppCategory.Unknown] = unknownUsed }, bonus);
        return (_, path) =>
        {
            var r = resolver.Resolve(path);
            return new ExeVerdict(r.Category, day.IsExhausted(r));
        };
    }

    private static Func<int, string, bool> Seen(params string[] paths) =>
        (user, path) => user == KidUser && paths.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static SessionProcess Proc(int pid, string? path, int session = KidSession, bool suspended = false, DateTime? start = null) =>
        new(pid, start ?? T0, path, session, suspended);

    private static SuspensionRecord Record(int pid, string path, DateTime? start = null, AppCategory category = AppCategory.Games) =>
        new(pid, start ?? T0, KidSession, KidUser, category, path);

    private static readonly Dictionary<int, int> KidSessions = new() { [KidSession] = KidUser };

    private static SuspensionPlan Plan(
        IEnumerable<SessionProcess> processes,
        Func<int, string, ExeVerdict> verdict,
        Func<int, string, bool>? seen = null,
        IEnumerable<SuspensionRecord>? records = null) =>
        SuspensionPlanner.Plan(new PlannerInput(
            processes.ToList(), KidSessions, verdict, seen ?? Seen(Game, Minecraft, Word, Helper, WebView, NewExe, Agent),
            (records ?? []).ToList(), Agent));

    [Fact]
    public void Exhausted_category_suspends_its_process_and_not_a_Study_program()
    {
        var plan = Plan([Proc(10, Game), Proc(11, Word)], Verdict(Rules, Tuesday, gamesUsed: 120));
        var action = Assert.Single(plan.ToSuspend);
        Assert.Equal(10, action.Process.Pid);
        Assert.Equal(AppCategory.Games, action.Category);
        Assert.False(action.IsRecorded);
        Assert.Empty(plan.ToResume);
    }

    [Fact]
    public void Category_with_time_left_suspends_nothing()
    {
        var plan = Plan([Proc(10, Game)], Verdict(Rules, Tuesday, gamesUsed: 119));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Every_process_of_a_foreground_exe_is_suspended()
    {
        var plan = Plan([Proc(10, Game), Proc(12, Game)], Verdict(Rules, Tuesday, gamesUsed: 120));
        Assert.Equal([10, 12], plan.ToSuspend.Select(a => a.Process.Pid).Order());
    }

    [Fact]
    public void App_Limit_game_keeps_running_while_other_games_stop()
    {
        var plan = Plan([Proc(10, Game), Proc(20, Minecraft)], Verdict(Rules, Tuesday, gamesUsed: 150));
        Assert.Equal([10], plan.ToSuspend.Select(a => a.Process.Pid));
    }

    [Fact]
    public void Background_helper_not_seen_in_front_keeps_running()
    {
        var plan = Plan([Proc(30, Helper), Proc(31, NewExe)], Verdict(Rules, Tuesday, 0, unknownUsed: 120), Seen(NewExe));
        Assert.Equal([31], plan.ToSuspend.Select(a => a.Process.Pid));
    }

    [Fact]
    public void WebView2_is_never_suspended()
    {
        var plan = Plan([Proc(40, WebView)], Verdict(Rules, Tuesday, 0, unknownUsed: 120));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Agent_is_never_suspended_also_without_the_System_rule()
    {
        var rulesWithoutInstallFolder = Rules.Where(r => r.Id != 4).ToList();
        var plan = Plan([Proc(41, Agent)], Verdict(rulesWithoutInstallFolder, Tuesday, 0, unknownUsed: 120));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Process_without_a_path_is_never_suspended()
    {
        var plan = Plan([Proc(42, null), Proc(43, "relative.exe")], Verdict(Rules, Tuesday, 0, unknownUsed: 120));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Admin_session_is_excluded()
    {
        var plan = Plan([Proc(10, Game, session: AdminSession)], Verdict(Rules, Tuesday, gamesUsed: 120));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Recorded_process_that_stays_suspended_needs_nothing()
    {
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(Rules, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.Empty(plan.ToSuspend);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToDrop);
    }

    [Fact]
    public void Recorded_process_running_again_is_suspended_again()
    {
        var plan = Plan([Proc(10, Game, suspended: false)], Verdict(Rules, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        var action = Assert.Single(plan.ToSuspend);
        Assert.True(action.IsRecorded);
        Assert.Empty(plan.ToResume);
    }

    [Fact]
    public void Recorded_process_stays_suspended_after_a_restart_without_foreground_samples()
    {
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(Rules, Tuesday, gamesUsed: 125), Seen(), [Record(10, Game)]);
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToDrop);
    }

    [Fact]
    public void Time_back_after_bonus_resumes()
    {
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(Rules, Tuesday, gamesUsed: 125, bonus: 15), records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToResume.Select(r => r.Pid));
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void New_day_resumes()
    {
        // On the new day the Category Usage starts again at 0.
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(Rules, Wednesday, gamesUsed: 0), Seen(), [Record(10, Game)]);
        Assert.Equal([10], plan.ToResume.Select(r => r.Pid));
    }

    [Fact]
    public void Rule_moved_to_another_category_resumes()
    {
        var moved = Rules.Select(r => r.Id == 1 ? r with { Category = AppCategory.Study } : r).ToList();
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(moved, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToResume.Select(r => r.Pid));
    }

    [Fact]
    public void Rule_moved_to_System_resumes()
    {
        var moved = Rules.Select(r => r.Id == 1 ? r with { Category = AppCategory.System } : r).ToList();
        var plan = Plan([Proc(10, Game, suspended: true)], Verdict(moved, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToResume.Select(r => r.Pid));
    }

    [Fact]
    public void Pid_reused_with_another_start_time_drops_the_record_and_does_not_resume()
    {
        var plan = Plan([Proc(10, Word, start: T0.AddHours(1))], Verdict(Rules, Tuesday, gamesUsed: 0), records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToDrop.Select(r => r.Pid));
        Assert.Empty(plan.ToResume);
        Assert.Empty(plan.ToSuspend);
    }

    [Fact]
    public void Pid_reused_by_an_exhausted_exe_is_a_new_suspension_not_a_recorded_one()
    {
        var plan = Plan([Proc(10, Game, start: T0.AddHours(1))], Verdict(Rules, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.Single(plan.ToDrop);
        var action = Assert.Single(plan.ToSuspend);
        Assert.False(action.IsRecorded);
    }

    [Fact]
    public void Stale_record_of_an_ended_process_is_dropped()
    {
        var plan = Plan([], Verdict(Rules, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToDrop.Select(r => r.Pid));
        Assert.Empty(plan.ToResume);
    }

    [Fact]
    public void Recorded_process_in_a_session_that_is_not_restricted_any_more_is_resumed()
    {
        var plan = Plan([Proc(10, Game, session: AdminSession, suspended: true)], Verdict(Rules, Tuesday, gamesUsed: 125),
            records: [Record(10, Game)]);
        Assert.Equal([10], plan.ToResume.Select(r => r.Pid));
    }

    [Fact]
    public void Recorded_process_whose_path_is_unreadable_now_uses_the_recorded_path()
    {
        var plan = Plan([Proc(10, null, suspended: false)], Verdict(Rules, Tuesday, gamesUsed: 125), records: [Record(10, Game)]);
        Assert.True(Assert.Single(plan.ToSuspend).IsRecorded);
    }
}
