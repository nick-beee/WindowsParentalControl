using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class CountedProcessesTests
{
    private static readonly DateTime T0 = new(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);

    private const string Game = @"C:\Games\game.exe";
    private const string Study = @"C:\Study\study.exe";
    private const string Notepad = @"C:\Windows\notepad.exe";
    private const string Explorer = @"C:\Windows\explorer.exe";
    private const string Dwm = @"C:\Windows\System32\dwm.exe";
    private const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
    private const string Calculator = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_1\CalculatorApp.exe";
    private const string Agent = @"C:\Program Files\ParentalControl\ParentalControl.Agent.exe";
    private const string WebView2 = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\158.0.4370.45\msedgewebview2.exe";
    private const string FakeWebView2 = @"C:\Users\Kid\Downloads\msedgewebview2.exe";

    private static readonly ProcessIdentity GameP = new(10, T0);
    private static readonly ProcessIdentity StudyP = new(20, T0);
    private static readonly ProcessIdentity NotepadP = new(30, T0);
    private static readonly ProcessIdentity ExplorerP = new(40, T0);
    private static readonly ProcessIdentity DwmP = new(50, T0);
    private static readonly ProcessIdentity AgentP = new(60, T0);
    private static readonly ProcessIdentity WebView2P = new(70, T0);
    private static readonly ProcessIdentity OtherP = new(80, T0);

    private static readonly TrustedWebView2 Trusted = new(@"C:\Program Files (x86)", @"C:\Program Files");

    private static AppCategory Categorize(string path) =>
        path.StartsWith(@"C:\Games\", StringComparison.OrdinalIgnoreCase) ? AppCategory.Games
        : path.StartsWith(@"C:\Study\", StringComparison.OrdinalIgnoreCase) ? AppCategory.Study
        : path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) ? AppCategory.System
        : path.StartsWith(@"C:\Program Files\ParentalControl\", StringComparison.OrdinalIgnoreCase) ? AppCategory.System
        : AppCategory.Unknown;

    private static SelectionContext Context(
        bool prompt = false,
        ForegroundProcess? last = null,
        Func<ProcessIdentity, bool>? running = null,
        Func<ProcessIdentity, bool>? suspended = null,
        Func<ProcessIdentity, string, bool>? agent = null) =>
        new(Categorize, @"C:\Windows", agent ?? ((p, _) => p == AgentP), prompt, suspended ?? (_ => false), running ?? (_ => true), last);

    private static SampleEntry E(ProbeRole role, ProcessIdentity p, string exe, WebView2Creator? creator = null) =>
        new(role, EntryOutcome.Read, p, exe, creator);

    private static SampleSelection Select(SampleReading reading, SelectionContext? context = null) =>
        CountedProcesses.Select(reading, context ?? Context(), Trusted);

    private static SampleSelection Select(params SampleEntry[] entries) => Select(new SampleReading(entries));

    private static AppCategory[] Cats(SampleSelection s) => s.Categories.OrderBy(c => c).ToArray();

    [Fact]
    public void Game_child_in_a_System_window_with_the_focus_on_the_host_counts_in_Games()
    {
        var s = Select(E(ProbeRole.Foreground, NotepadP, Notepad), E(ProbeRole.Child, GameP, Game));
        Assert.Equal([AppCategory.Games], Cats(s));
        Assert.Contains(Game, s.ExesToRecord);
    }

    [Fact]
    public void System_window_in_a_game_counts_in_Games()
    {
        var s = Select(E(ProbeRole.Foreground, GameP, Game), E(ProbeRole.Focus, NotepadP, Notepad), E(ProbeRole.Child, NotepadP, Notepad));
        Assert.Equal([AppCategory.Games], Cats(s));
    }

    [Fact]
    public void Study_window_in_a_game_counts_in_Games_and_Study()
    {
        var s = Select(E(ProbeRole.Foreground, GameP, Game), E(ProbeRole.Focus, StudyP, Study), E(ProbeRole.Child, StudyP, Study));
        Assert.Equal([AppCategory.Games, AppCategory.Study], Cats(s));
        Assert.Equal(2, s.Counted.Count);
    }

    [Fact]
    public void NoActivate_game_under_the_cursor_with_Study_in_front_counts_both()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, StudyP, Study), E(ProbeRole.Cursor, GameP, Game)], CursorNoActivate: true));
        Assert.Equal([AppCategory.Games, AppCategory.Study], Cats(s));
    }

    [Fact]
    public void Normal_game_window_under_the_cursor_with_Study_in_front_counts_Study_only_and_records_the_game()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, StudyP, Study), E(ProbeRole.Cursor, GameP, Game)]));
        Assert.Equal([AppCategory.Study], Cats(s));
        Assert.Contains(Game, s.ExesToRecord);
    }

    [Fact]
    public void Game_inside_a_noActivate_Notepad_under_the_cursor_counts_Games_and_Study()
    {
        var s = Select(new SampleReading(
            [E(ProbeRole.Foreground, StudyP, Study), E(ProbeRole.Cursor, GameP, Game), E(ProbeRole.Cursor, NotepadP, Notepad)],
            CursorNoActivate: true));
        Assert.Equal([AppCategory.Games, AppCategory.Study], Cats(s));
    }

    [Fact]
    public void Game_inside_a_Notepad_without_noActivate_under_the_cursor_counts_Study_only()
    {
        var s = Select(new SampleReading(
            [E(ProbeRole.Foreground, StudyP, Study), E(ProbeRole.Cursor, GameP, Game), E(ProbeRole.Cursor, NotepadP, Notepad)]));
        Assert.Equal([AppCategory.Study], Cats(s));
        Assert.Contains(Game, s.ExesToRecord);
    }

    [Fact]
    public void Trusted_WebView2_in_a_Study_app_counts_Study_only()
    {
        var s = Select(E(ProbeRole.Foreground, StudyP, Study),
            E(ProbeRole.Focus, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, StudyP, Study)),
            E(ProbeRole.Child, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, StudyP, Study)));
        Assert.Equal([AppCategory.Study], Cats(s));
        Assert.Single(s.Counted);
    }

    [Fact]
    public void Trusted_WebView2_popup_of_a_Study_app_in_front_counts_Study()
    {
        var s = Select(E(ProbeRole.Foreground, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, StudyP, Study)));
        Assert.Equal([AppCategory.Study], Cats(s));
        Assert.Equal(ProbeRole.Foreground, s.Counted[0].Role);
    }

    [Fact]
    public void Trusted_WebView2_control_of_a_Games_app_moved_into_Notepad_counts_Games_and_records_the_app()
    {
        var s = Select(E(ProbeRole.Foreground, NotepadP, Notepad),
            E(ProbeRole.Child, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, GameP, Game)));
        Assert.Equal([AppCategory.Games], Cats(s));
        Assert.Contains(Game, s.ExesToRecord);
        Assert.Contains(WebView2, s.ExesToRecord);
    }

    [Fact]
    public void Trusted_WebView2_in_explorer_counts_nothing()
    {
        var s = Select(E(ProbeRole.Foreground, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, ExplorerP, Explorer)));
        Assert.Empty(s.Counted);
        Assert.False(s.Blind);
        Assert.False(s.EndedItem);
    }

    [Fact]
    public void Trusted_WebView2_whose_parent_ended_counts_nothing_and_is_no_item()
    {
        var s = Select(E(ProbeRole.Foreground, WebView2P, WebView2, WebView2Creator.NotAttributed));
        Assert.Empty(s.Counted);
        Assert.False(s.Blind);
        Assert.False(s.EndedItem);
        Assert.Equal([WebView2], s.NotAttributed);
    }

    [Fact]
    public void Trusted_WebView2_without_a_creator_counts_nothing_and_is_not_blind()
    {
        // The start-time test failed: the service gives no creator. Not attributed, never a blind sample.
        var s = Select(E(ProbeRole.Focus, WebView2P, WebView2), E(ProbeRole.Foreground, NotepadP, Notepad));
        Assert.Empty(s.Counted);
        Assert.False(s.Blind);
        Assert.Single(s.NotAttributed);
    }

    [Fact]
    public void WebView2_name_in_the_Downloads_folder_counts_by_its_rule_in_Unknown()
    {
        Assert.False(Trusted.IsTrusted(FakeWebView2));
        var s = Select(E(ProbeRole.Foreground, StudyP, Study), E(ProbeRole.Child, OtherP, FakeWebView2));
        Assert.Equal([AppCategory.Study, AppCategory.Unknown], Cats(s));
    }

    [Fact]
    public void Trusted_folder_needs_the_file_name_and_the_folder()
    {
        Assert.True(Trusted.IsTrusted(WebView2));
        Assert.True(Trusted.IsTrusted(@"C:\Program Files\Microsoft\EdgeWebView\Application\1.0.0.0\MSEDGEWEBVIEW2.EXE"));
        Assert.False(Trusted.IsTrusted(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1.0.0.0\game.exe"));
        Assert.False(Trusted.IsTrusted(@"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe"));
        Assert.False(Trusted.IsTrusted(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\..\..\x\msedgewebview2.exe"));
    }

    [Fact]
    public void Agent_as_cursor_counts_nothing()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, NotepadP, Notepad), E(ProbeRole.Cursor, AgentP, Agent)], CursorNoActivate: true));
        Assert.Empty(s.Counted);
    }

    [Fact]
    public void Agent_as_a_child_of_Notepad_counts_in_Unknown()
    {
        var s = Select(E(ProbeRole.Foreground, NotepadP, Notepad), E(ProbeRole.Child, AgentP, Agent));
        Assert.Equal([AppCategory.Unknown], Cats(s));
        Assert.True(s.Counted[0].IsAgent);
    }

    [Fact]
    public void Agent_as_creator_of_a_trusted_WebView2_counts_in_Unknown()
    {
        var s = Select(E(ProbeRole.Foreground, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, AgentP, Agent)));
        Assert.Equal([AppCategory.Unknown], Cats(s));
        Assert.True(s.Counted[0].IsAgent);
    }

    // Review of group 4, item 3: the service names the agent by its exe path. A WebView2 of the agent that a script moved
    // into another window has the agent as its creator, but no window of the agent is in the sample.
    private static bool AgentByPath(ProcessIdentity _, string path) => string.Equals(path, Agent, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Moved_agent_WebView2_child_without_an_agent_window_counts_in_Unknown_as_the_agent()
    {
        var s = Select(new SampleReading([
                E(ProbeRole.Foreground, NotepadP, Notepad),
                E(ProbeRole.Child, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, AgentP, Agent))]),
            Context(agent: AgentByPath));
        Assert.Equal([AppCategory.Unknown], Cats(s));
        Assert.Single(s.Counted);
        Assert.True(s.Counted[0].IsAgent);
        Assert.Equal(ProbeRole.Child, s.Counted[0].Role);
    }

    [Fact]
    public void Moved_agent_WebView2_child_uses_the_Agent_Window_Budget_and_then_gets_the_never_suspended_lock()
    {
        var context = Context(agent: AgentByPath);
        var reading = new SampleReading([
            E(ProbeRole.Foreground, NotepadP, Notepad),
            E(ProbeRole.Child, WebView2P, WebView2, new WebView2Creator(CreatorOutcome.Read, AgentP, Agent))]);
        var budget = new AgentWindowBudget();
        // The agent is protected by its rule (IsAgent), not by ProtectedPrograms.
        var allowance = new NeverSuspendedAllowance(_ => false);
        var unlocked = TimeSpan.Zero;
        var exemptTotal = 0;
        IReadOnlyList<string> over = [];
        var seconds = 0;
        while (over.Count == 0 && seconds < 3600)
        {
            var s = CountedProcesses.Select(reading, context, Trusted);
            var agentCounted = s.Counted.Any(c => c.IsAgent);
            Assert.True(agentCounted);
            var exempt = budget.Sample(TimeSpan.FromSeconds(seconds), 1, agentCounted, unlocked: true);
            exemptTotal += exempt;
            unlocked += TimeSpan.FromSeconds(1);
            seconds++;
            over = allowance.Sample(unlocked, true, s, TimeSpan.FromSeconds(1), c => c == AppCategory.Unknown, TimeSpan.FromSeconds(exempt));
        }
        Assert.Equal(AgentWindowBudget.MaxExemptSeconds, exemptTotal);
        Assert.Equal([Agent], over);
        // The budget first, then more than the 60 seconds of the allowance.
        Assert.InRange(seconds, AgentWindowBudget.MaxExemptSeconds + NeverSuspendedAllowance.AllowedSeconds,
            AgentWindowBudget.MaxExemptSeconds + NeverSuspendedAllowance.AllowedSeconds + 2);
    }

    [Fact]
    public void Agent_in_front_during_an_active_prompt_counts_nothing()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, AgentP, Agent)]), Context(prompt: true));
        Assert.Empty(s.Counted);
    }

    [Fact]
    public void Process_with_the_agent_exe_that_is_not_the_launched_agent_counts_by_its_rule()
    {
        var s = Select(E(ProbeRole.Foreground, OtherP, Agent));
        Assert.Empty(s.Counted);
    }

    [Fact]
    public void Store_app_counts_the_app_only()
    {
        var calc = new ProcessIdentity(90, T0);
        var s = Select(E(ProbeRole.Foreground, new ProcessIdentity(91, T0), FrameHost), E(ProbeRole.Focus, calc, Calculator), E(ProbeRole.Child, calc, Calculator));
        Assert.Equal([AppCategory.Unknown], Cats(s));
        Assert.Single(s.Counted);
        Assert.Equal(Calculator, s.Counted[0].ExePath);
    }

    [Fact]
    public void Same_process_in_fg_and_focus_counts_once()
    {
        var s = Select(E(ProbeRole.Foreground, GameP, Game), E(ProbeRole.Focus, GameP, Game));
        Assert.Single(s.Counted);
    }

    [Fact]
    public void Ended_focus_entry_keeps_the_fg_and_gives_one_item()
    {
        var s = Select(E(ProbeRole.Foreground, GameP, Game), new SampleEntry(ProbeRole.Focus, EntryOutcome.Ended));
        Assert.Equal([AppCategory.Games], Cats(s));
        Assert.True(s.EndedItem);
        Assert.False(s.Blind);
    }

    [Fact]
    public void Ended_cursor_entry_gives_an_item_only_with_noActivate()
    {
        var entries = new[] { E(ProbeRole.Foreground, StudyP, Study), new SampleEntry(ProbeRole.Cursor, EntryOutcome.Ended) };
        Assert.False(Select(new SampleReading(entries)).EndedItem);
        Assert.True(Select(new SampleReading(entries, CursorNoActivate: true)).EndedItem);
    }

    [Fact]
    public void Read_fg_with_an_Unreadable_child_counts_the_fg_and_is_blind()
    {
        var s = Select(E(ProbeRole.Foreground, StudyP, Study), new SampleEntry(ProbeRole.Child, EntryOutcome.Unreadable));
        Assert.Equal([AppCategory.Study], Cats(s));
        Assert.True(s.Blind);
    }

    [Fact]
    public void Unreadable_cursor_entry_without_noActivate_is_blind()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, StudyP, Study), new SampleEntry(ProbeRole.Cursor, EntryOutcome.Unreadable)]));
        Assert.True(s.Blind);
    }

    [Fact]
    public void Blind_sample_of_the_probe_is_blind_and_counts_nothing()
    {
        var s = Select(new SampleReading([], ProbeBlind: true));
        Assert.True(s.Blind);
        Assert.Empty(s.Counted);
        Assert.Null(s.NextLastForeground);
    }

    [Fact]
    public void Ghost_window_with_a_Read_hung_game_counts_in_Games()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)], Hung: E(ProbeRole.Foreground, GameP, Game)));
        Assert.Equal([AppCategory.Games], Cats(s));
        Assert.Equal(new ForegroundProcess(GameP, Game), s.NextLastForeground);
        Assert.Contains(Game, s.ExesToRecord);
    }

    [Fact]
    public void Ghost_window_without_hung_keeps_the_game_while_it_runs()
    {
        var last = new ForegroundProcess(GameP, Game);
        var running = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)]), Context(last: last, running: p => p == GameP));
        Assert.Equal([AppCategory.Games], Cats(running));
        Assert.Equal(last, running.NextLastForeground);

        var ended = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)]), Context(last: last, running: _ => false));
        Assert.Empty(ended.Counted);
        Assert.False(ended.Blind);
        Assert.False(ended.EndedItem);
    }

    [Fact]
    public void Ghost_window_with_an_Ended_hung_entry_is_an_item_without_the_fallback()
    {
        var last = new ForegroundProcess(GameP, Game);
        var s = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)], Hung: new SampleEntry(ProbeRole.Foreground, EntryOutcome.Ended)),
            Context(last: last));
        Assert.Empty(s.Counted);
        Assert.True(s.EndedItem);
        Assert.False(s.Blind);
    }

    [Fact]
    public void Ghost_window_with_an_Unreadable_hung_entry_is_blind()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)], Hung: new SampleEntry(ProbeRole.Foreground, EntryOutcome.Unreadable)),
            Context(last: new ForegroundProcess(GameP, Game)));
        Assert.Empty(s.Counted);
        Assert.True(s.Blind);
    }

    [Fact]
    public void Hung_entry_with_a_fg_that_is_not_dwm_is_ignored()
    {
        // A game that registers the class Ghost after a Study program: the game counts, not Study.
        var s = Select(new SampleReading([E(ProbeRole.Foreground, GameP, Game)], Hung: E(ProbeRole.Foreground, StudyP, Study)),
            Context(last: new ForegroundProcess(StudyP, Study)));
        Assert.Equal([AppCategory.Games], Cats(s));
        Assert.DoesNotContain(Study, s.ExesToRecord);
    }

    [Fact]
    public void Dwm_from_another_folder_is_not_a_ghost()
    {
        var fake = @"C:\Users\Kid\dwm.exe";
        var s = Select(new SampleReading([E(ProbeRole.Foreground, OtherP, fake)], Hung: E(ProbeRole.Foreground, StudyP, Study)));
        Assert.Equal([AppCategory.Unknown], Cats(s));
    }

    [Fact]
    public void Ghost_of_a_suspended_game_counts_nothing()
    {
        var s = Select(new SampleReading([E(ProbeRole.Foreground, DwmP, Dwm)], Hung: E(ProbeRole.Foreground, GameP, Game)),
            Context(suspended: p => p == GameP));
        Assert.Empty(s.Counted);
    }
}
