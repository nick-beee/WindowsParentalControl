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

        // Runs samples for `seconds` and gives the sum of the exempt seconds.
        public int Run(int seconds, bool agent, bool unlocked = true, int interval = Interval)
        {
            var exempt = 0;
            for (var t = 0; t < seconds; t += interval)
            {
                Now += TimeSpan.FromSeconds(interval);
                exempt += Budget.Sample(Now, interval, agent, unlocked);
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
        Assert.Equal(5, probe.Budget.Sample(probe.Now, 10, agentCounted: true, unlocked: true));
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
        // A sleep of 3 hours: the monotonic clock jumps, but the next sample moves the window by one interval only.
        probe.Now += TimeSpan.FromHours(3);
        Assert.Equal(0, probe.Budget.Sample(probe.Now, Interval, agentCounted: true, unlocked: true));
        Assert.Equal(300 + Interval, probe.Budget.UnlockedSeconds);
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
        Assert.Equal(10, budget.Sample(now, 10, agentCounted: true, unlocked: true));
        Assert.Equal(0, budget.Sample(now, 10, agentCounted: true, unlocked: true));
        Assert.Equal(10, budget.UnlockedSeconds);
    }
}
