using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

/// <summary>The Agent Window Budget (request-window, design D8). Samples every 10 seconds unless a test says otherwise.</summary>
public class AgentWindowBudgetTests
{
    private const int Interval = 10;

    private sealed class Probe
    {
        public readonly AgentWindowBudget Budget = new();
        public TimeSpan Now = TimeSpan.FromHours(1);

        // Runs samples for `seconds` and gives the sum of the exempt seconds. Each sample has the gap `interval` and
        // counts `counted` seconds (default: the gap). A sample with only System programs counts 0 seconds.
        public int Run(int seconds, bool agent, bool unlocked = true, int interval = Interval, int? counted = null)
        {
            var exempt = 0;
            for (var t = 0; t < seconds; t += interval)
            {
                Now += TimeSpan.FromSeconds(interval);
                exempt += Budget.Sample(Now, TimeSpan.FromSeconds(interval), counted ?? interval, agent, unlocked);
            }
            return exempt;
        }
    }

    [Fact]
    public void ThreeMinutesAreExempt()
    {
        var probe = new Probe();
        Assert.Equal(180, probe.Run(180, agent: true));
    }

    [Fact]
    public void SevenMinutesGiveFiveExemptAndTwoCounted()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(420, agent: true));
        Assert.Equal(0, probe.Budget.RemainingSeconds);
    }

    [Fact]
    public void ASampleThatCrossesTheLimitIsExemptInPart()
    {
        var probe = new Probe();
        Assert.Equal(295, probe.Run(295, agent: true, interval: 5));
        probe.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(5, probe.Budget.Sample(probe.Now, TimeSpan.FromSeconds(10), 10, agentCounted: true, unlocked: true));
    }

    [Fact]
    public void SlidingWindow()
    {
        var probe = new Probe();
        // 10:00 to 10:05: the agent is counted.
        Assert.Equal(300, probe.Run(300, agent: true));
        // 10:05 to 11:00: the agent is not counted.
        Assert.Equal(0, probe.Run(3300, agent: false));
        // 11:00 to 11:05: each second is exempt again, one for each second that leaves the window.
        Assert.Equal(300, probe.Run(300, agent: true));
    }

    [Fact]
    public void NoExemptSecondBefore1100()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(300, agent: true));
        // 10:05 to 10:59:50 with the agent counted: nothing is exempt.
        Assert.Equal(0, probe.Run(3290, agent: true));
        // The sample 10:59:50 to 11:00:00 is still in the window of 10:00:00 to 10:00:10.
        Assert.Equal(0, probe.Run(10, agent: true));
        // From 11:00 the seconds of 10:00 leave the window.
        Assert.Equal(10, probe.Run(10, agent: true));
    }

    [Fact]
    public void FullBudgetAgainAt1105()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(300, agent: true));
        Assert.Equal(0, probe.Run(3600, agent: false));
        Assert.Equal(300, probe.Budget.RemainingSeconds);
    }

    [Fact]
    public void LockedTimeDoesNotMoveTheWindow()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(300, agent: true));
        // Two hours locked, also with the agent counted.
        Assert.Equal(0, probe.Run(7200, agent: true, unlocked: false));
        Assert.Equal(0, probe.Budget.RemainingSeconds);
        Assert.Equal(0, probe.Run(60, agent: true));
    }

    [Fact]
    public void SleepDoesNotMoveTheWindow()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(300, agent: true));
        // A sleep of 3 hours: the monotonic clock jumps, but the counter gives the first sample after the resume the
        // gap zero, so the window does not move. The next sample moves it by its gap only.
        probe.Now += TimeSpan.FromHours(3);
        Assert.Equal(0, probe.Budget.Sample(probe.Now, TimeSpan.Zero, 0, agentCounted: true, unlocked: true));
        Assert.Equal(300, probe.Budget.UnlockedSeconds);
        probe.Now += TimeSpan.FromSeconds(Interval);
        Assert.Equal(0, probe.Budget.Sample(probe.Now, TimeSpan.FromSeconds(Interval), Interval, agentCounted: true, unlocked: true));
        Assert.Equal(300 + Interval, probe.Budget.UnlockedSeconds);
    }

    [Fact]
    public void SystemProgramsOnlyStillMoveTheWindow()
    {
        // Final review, item 3: the counted interval is zero while only System programs are in front, but the window
        // moves with the unlocked time.
        var probe = new Probe();
        // 10:00 to 10:05: the agent is counted.
        Assert.Equal(300, probe.Run(300, agent: true));
        // 10:05 to 11:10: only System programs, each sample counts 0 seconds.
        Assert.Equal(0, probe.Run(3900, agent: false, counted: 0));
        Assert.Equal(300, probe.Budget.RemainingSeconds);
        // 11:10: the budget is full again.
        Assert.Equal(300, probe.Run(300, agent: true));
    }

    [Fact]
    public void LockedTimeStillDoesNotMoveTheWindowAfterSystemOnlySamples()
    {
        var probe = new Probe();
        Assert.Equal(300, probe.Run(300, agent: true));
        Assert.Equal(0, probe.Run(600, agent: false, counted: 0));
        // Two hours locked: no move.
        Assert.Equal(0, probe.Run(7200, agent: false, unlocked: false, counted: 0));
        Assert.Equal(0, probe.Budget.RemainingSeconds);
        // The first sample after the unlock has the gap zero: no move either.
        probe.Now += TimeSpan.FromSeconds(Interval);
        Assert.Equal(0, probe.Budget.Sample(probe.Now, TimeSpan.Zero, 0, agentCounted: true, unlocked: true));
        Assert.Equal(900, probe.Budget.UnlockedSeconds);
    }

    [Fact]
    public void ExemptSecondsAreCappedByTheCountedInterval()
    {
        // A stall: the gap is 34 s, but the gap rule counts 12 s. The window moves 34 s, and at most 12 s are exempt.
        var budget = new AgentWindowBudget();
        Assert.Equal(12, budget.Sample(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(34), 12, agentCounted: true, unlocked: true));
        Assert.Equal(34, budget.UnlockedSeconds);
        Assert.Equal(288, budget.RemainingSeconds);
    }

    [Fact]
    public void TwoSessionsOfOneUserDoNotMoveTheWindowTwice()
    {
        var budget = new AgentWindowBudget();
        budget.Sample(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(5), 5, agentCounted: false, unlocked: true);
        // A sample of a second session 1 s later with its own gap of 5 s moves the window by 1 s only.
        budget.Sample(TimeSpan.FromSeconds(101), TimeSpan.FromSeconds(5), 5, agentCounted: false, unlocked: true);
        Assert.Equal(6, budget.UnlockedSeconds);
    }

    [Fact]
    public void ASampleWithoutTheAgentUsesNothing()
    {
        var probe = new Probe();
        Assert.Equal(0, probe.Run(600, agent: false));
        Assert.Equal(300, probe.Budget.RemainingSeconds);
    }

    [Fact]
    public void ARepeatedSampleAtTheSameTimeMovesNothing()
    {
        var budget = new AgentWindowBudget();
        var now = TimeSpan.FromMinutes(5);
        Assert.Equal(10, budget.Sample(now, TimeSpan.FromSeconds(10), 10, agentCounted: true, unlocked: true));
        Assert.Equal(0, budget.Sample(now, TimeSpan.FromSeconds(10), 10, agentCounted: true, unlocked: true));
        Assert.Equal(10, budget.UnlockedSeconds);
    }
}
