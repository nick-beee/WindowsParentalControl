using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;
using Xunit;

namespace ParentalControl.Domain.Tests.Sessions;

// Task 4.8: the Agent Window Budget in the probe counting (category-limits, "Agent Window Budget" and "The Agent Window
// Budget exempts only the Session Agent"). The host does the same per sample: AgentWindowBudget.Sample, then
// ProbeCategoryTime.PerCategory and NeverSuspendedAllowance.Sample with the exempt part.
public class ProbeCategoryTimeTests
{
    private static readonly CountedProcess Agent = new(new ProcessIdentity(100, DateTime.UnixEpoch), @"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe",
        AppCategory.Unknown, ProbeRole.Foreground, IsAgent: true);
    private static readonly CountedProcess UnknownGame = new(new ProcessIdentity(200, DateTime.UnixEpoch), @"C:\Users\Kid\Downloads\game.exe",
        AppCategory.Unknown, ProbeRole.Child, IsAgent: false);
    private static readonly CountedProcess StudyApp = new(new ProcessIdentity(300, DateTime.UnixEpoch), @"C:\Study\app.exe",
        AppCategory.Study, ProbeRole.Child, IsAgent: false);

    private static SampleSelection Selection(params CountedProcess[] counted) =>
        new(counted, counted.Select(c => c.Category).Distinct().ToList(), counted.Select(c => c.ExePath).ToList(), false, false, [], null);

    private sealed record Run(TimeSpan Unknown, TimeSpan Study, bool Locked);

    // Samples every 5 s for the given time; Unknown is exhausted from the start. The agent is never suspended.
    private static Run Simulate(TimeSpan duration, SampleSelection selection, AgentWindowBudget? budget = null)
    {
        budget ??= new AgentWindowBudget();
        var allowance = new NeverSuspendedAllowance(_ => false);
        var unknown = TimeSpan.Zero;
        var study = TimeSpan.Zero;
        var locked = false;
        for (var t = 5; t <= duration.TotalSeconds; t += 5)
        {
            var now = TimeSpan.FromSeconds(t);
            var agentCounted = selection.Counted.Any(c => c.IsAgent);
            var exempt = TimeSpan.FromSeconds(budget.Sample(now, TimeSpan.FromSeconds(5), 5, agentCounted, unlocked: true));
            var time = ProbeCategoryTime.PerCategory(selection, TimeSpan.FromSeconds(5), exempt);
            unknown += time.GetValueOrDefault(AppCategory.Unknown);
            study += time.GetValueOrDefault(AppCategory.Study);
            if (allowance.Sample(now, true, selection, TimeSpan.FromSeconds(5), c => c == AppCategory.Unknown, exempt).Count > 0)
                locked = true;
        }
        return new Run(unknown, study, locked);
    }

    [Fact]
    public void Request_while_Unknown_is_exhausted_adds_nothing_and_does_not_lock()
    {
        var run = Simulate(TimeSpan.FromMinutes(3), Selection(Agent));
        Assert.Equal(TimeSpan.Zero, run.Unknown);
        Assert.False(run.Locked);
    }

    [Fact]
    public void Budget_used_up_the_last_2_of_7_minutes_count_and_lock_after_60_s()
    {
        var run = Simulate(TimeSpan.FromMinutes(7), Selection(Agent));
        Assert.Equal(TimeSpan.FromMinutes(2), run.Unknown);
        Assert.True(run.Locked);

        // The lock comes within 2 minutes after the budget is used up (5 min + 65 s here).
        Assert.False(Simulate(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(60), Selection(Agent)).Locked);
        Assert.True(Simulate(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(65), Selection(Agent)).Locked);
    }

    [Fact]
    public void Game_moved_into_the_agent_window_counts_the_whole_interval_in_Unknown()
    {
        var run = Simulate(TimeSpan.FromMinutes(3), Selection(Agent, UnknownGame));
        Assert.Equal(TimeSpan.FromMinutes(3), run.Unknown);
    }

    [Fact]
    public void Another_category_of_the_same_sample_still_gets_the_interval()
    {
        var run = Simulate(TimeSpan.FromMinutes(3), Selection(Agent, StudyApp));
        Assert.Equal(TimeSpan.Zero, run.Unknown);
        Assert.Equal(TimeSpan.FromMinutes(3), run.Study);
    }

    [Fact]
    public void A_sample_without_the_agent_takes_nothing_from_the_budget()
    {
        var budget = new AgentWindowBudget();
        Simulate(TimeSpan.FromMinutes(10), Selection(UnknownGame), budget);
        Assert.Equal(AgentWindowBudget.MaxExemptSeconds, budget.RemainingSeconds);
    }
}
