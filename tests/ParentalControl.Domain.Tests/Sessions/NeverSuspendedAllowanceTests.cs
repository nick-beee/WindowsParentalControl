using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class NeverSuspendedAllowanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);
    private const string FakeWebView2 = @"C:\Users\Kid\Downloads\msedgewebview2.exe";
    private const string Notepad = @"C:\Windows\notepad.exe";
    private const string Agent = @"C:\Program Files\ParentalControl\ParentalControl.Agent.exe";
    private const string Game = @"C:\Games\game.exe";

    private static readonly ProtectedPrograms Protected = new(@"C:\Windows", [@"C:\Program Files\ParentalControl"]);

    private static NeverSuspendedAllowance New() => new(Protected.IsProtected);

    private static SampleSelection Sel(params CountedProcess[] counted) =>
        new(counted, counted.Select(c => c.Category).Distinct().ToList(), counted.Select(c => c.ExePath).ToList(), false, false, [], null);

    private static CountedProcess P(string exe, AppCategory category, bool agent = false) =>
        new(new ProcessIdentity(exe.Length, T0), exe, category, ProbeRole.Foreground, agent);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // Feeds samples of `step` seconds from `start` for `total` seconds; returns the result of the last sample.
    private static IReadOnlyList<string> Run(NeverSuspendedAllowance allowance, SampleSelection selection, double start, double total,
        double step = 5, bool unlocked = true, Func<AppCategory, bool>? exhausted = null, double agentExempt = 0)
    {
        IReadOnlyList<string> result = [];
        for (var t = step; t <= total; t += step)
            result = allowance.Sample(S(start + t), unlocked, selection, S(step), exhausted ?? (_ => true), S(agentExempt));
        return result;
    }

    [Fact]
    public void Thirty_seconds_give_no_fallback()
    {
        Assert.Empty(Run(New(), Sel(P(FakeWebView2, AppCategory.Unknown)), 0, 30));
    }

    [Fact]
    public void Sixty_one_seconds_give_the_fallback()
    {
        var allowance = New();
        Assert.Empty(Run(allowance, Sel(P(FakeWebView2, AppCategory.Unknown)), 0, 60));
        Assert.Equal([FakeWebView2], allowance.Sample(S(61), true, Sel(P(FakeWebView2, AppCategory.Unknown)), S(1), _ => true));
    }

    [Fact]
    public void WebView2_name_in_Downloads_in_an_exhausted_Unknown_gives_the_fallback()
    {
        Assert.Equal([FakeWebView2], Run(New(), Sel(P(FakeWebView2, AppCategory.Unknown)), 0, 65));
    }

    [Fact]
    public void Exe_below_the_Windows_folder_without_a_System_rule_gives_the_fallback()
    {
        Assert.Equal([Notepad], Run(New(), Sel(P(Notepad, AppCategory.Unknown)), 0, 65));
    }

    [Fact]
    public void Time_before_the_exhaustion_does_not_count()
    {
        var allowance = New();
        var exhausted = false;
        Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 0, 300, exhausted: _ => exhausted);
        exhausted = true;
        Assert.Empty(Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 300, 60, exhausted: _ => exhausted));
        Assert.Equal([Notepad], Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 360, 5, exhausted: _ => exhausted));
    }

    [Fact]
    public void Locked_time_does_not_count()
    {
        var allowance = New();
        Assert.Empty(Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 0, 600, unlocked: false));
        Assert.Empty(Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 0, 60));
    }

    [Fact]
    public void Time_older_than_ten_minutes_of_unlocked_time_leaves_the_window()
    {
        var allowance = New();
        Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 0, 50);
        Assert.Empty(Run(allowance, Sel(P(Notepad, AppCategory.Unknown)), 650, 50));
    }

    [Fact]
    public void Agent_during_an_active_prompt_adds_nothing()
    {
        // The selection has no agent entry during a prompt (CountedProcesses, S10).
        Assert.Empty(Run(New(), Sel(), 0, 300));
    }

    [Fact]
    public void Agent_counts_without_the_exempt_time()
    {
        var agent = Sel(P(Agent, AppCategory.Unknown, agent: true));
        Assert.Empty(Run(New(), agent, 0, 300, agentExempt: 5));
        Assert.Equal([Agent], Run(New(), agent, 0, 65));
    }

    [Fact]
    public void Trusted_WebView2_whose_creator_can_be_suspended_adds_nothing()
    {
        // The selection counts a trusted WebView2 as its creator, here a game that can be suspended.
        Assert.Empty(Run(New(), Sel(P(Game, AppCategory.Games)), 0, 300));
    }

    [Fact]
    public void System_and_a_category_with_time_left_add_nothing()
    {
        Assert.Empty(Run(New(), Sel(P(Notepad, AppCategory.System)), 0, 300));
        Assert.Empty(Run(New(), Sel(P(Notepad, AppCategory.Study)), 0, 300, exhausted: c => c != AppCategory.Study));
    }
}
