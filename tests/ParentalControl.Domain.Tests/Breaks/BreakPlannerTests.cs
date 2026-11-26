using ParentalControl.Domain.Breaks;

namespace ParentalControl.Domain.Tests.Breaks;

public class BreakPlannerTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
    private static readonly BreakRule Rule = new(true, 45, 10);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static BreakTick Unlocked(int? untilOtherLock = null, DateTime? scheduleEnd = null) =>
        new(SessionUnlocked: true, OtherLockInThisTick: false, MinutesUntilOtherLock: untilOtherLock, ScheduleEndUtc: scheduleEnd);

    /// <summary>Counts one minute per tick from minute <paramref name="from"/>, as the service does.</summary>
    private static BreakState Use(BreakState state, int from, int minutes, BreakRule? rule = null)
    {
        for (var i = 1; i <= minutes; i++)
            state = BreakPlanner.CountMinutes(rule ?? Rule, state, At(from + i), 1).State;
        return state;
    }

    private static BreakState WithUse(int minutes, double lastUseAt = 0) =>
        BreakState.Empty with { ContinuousUseMinutes = minutes, LastUseUtc = At(lastUseAt) };

    // --- 1.3 Count and Rest ---

    [Fact]
    public void ThirtyCountedMinutesGiveThirty() =>
        Assert.Equal(30, Use(BreakState.Empty, 0, 30).ContinuousUseMinutes);

    [Fact]
    public void RestOneMinuteTooShortKeepsTheValue()
    {
        var state = BreakPlanner.RestStart(Rule, WithUse(30), At(0)).State;
        state = BreakPlanner.RestEnd(Rule, state, At(9)).State;
        Assert.Equal(30, state.ContinuousUseMinutes);
    }

    [Fact]
    public void RestOfMMinutesGivesZero()
    {
        var state = BreakPlanner.RestStart(Rule, WithUse(30), At(0)).State;
        state = BreakPlanner.RestEnd(Rule, state, At(10)).State;
        Assert.Equal(0, state.ContinuousUseMinutes);
        Assert.Equal(At(10), state.LastUseUtc);
    }

    [Fact]
    public void LockFor12MinutesGivesZero()
    {
        var state = BreakPlanner.RestStart(Rule, WithUse(30), At(0)).State;
        Assert.Equal(0, BreakPlanner.RestEnd(Rule, state, At(12)).State.ContinuousUseMinutes);
    }

    [Fact]
    public void FirstMinuteAfterAShortRestDoesNotSeeTheRestAgain()
    {
        // Lock 9.5 minutes, then the first tick 1.5 minutes after the unlock counts 1 minute.
        var state = BreakPlanner.RestStart(Rule, WithUse(30), At(0)).State;
        state = BreakPlanner.RestEnd(Rule, state, At(9.5)).State;
        state = BreakPlanner.CountMinutes(Rule, state, At(11), 1).State;
        Assert.Equal(31, state.ContinuousUseMinutes);
    }

    [Fact]
    public void TickGapOfMMinutesGivesZero()
    {
        // The last minute counted at 0. The next tick at 11 counts 1 minute (a gap counts as 1 minute): 10 minutes without use.
        var state = BreakPlanner.CountMinutes(Rule, WithUse(30), At(11), 1).State;
        Assert.Equal(1, state.ContinuousUseMinutes);
    }

    [Fact]
    public void TickGapShorterThanMKeepsTheValue()
    {
        var state = BreakPlanner.CountMinutes(Rule, WithUse(30), At(10), 1).State;
        Assert.Equal(31, state.ContinuousUseMinutes);
    }

    [Fact]
    public void SignOutAndSignInAfter15MinutesGivesZero()
    {
        var state = BreakPlanner.RestStart(Rule, WithUse(40), At(0)).State;
        Assert.Equal(0, BreakPlanner.RestEnd(Rule, state, At(15)).State.ContinuousUseMinutes);
    }

    [Fact]
    public void ServiceStartWithAnUnlockedSessionKeepsTheValue()
    {
        // The service was down for 15 minutes while the session stayed unlocked. The PC did not restart.
        var state = BreakPlanner.ServiceStart(Rule, WithUse(30), At(15), hasUnlockedSession: true, bootUtc: At(-600)).State;
        Assert.Equal(30, state.ContinuousUseMinutes);
        Assert.Equal(At(15), state.LastUseUtc);

        // The down time is not a Rest: the next minute counts on.
        state = BreakPlanner.CountMinutes(Rule, state, At(16), 1).State;
        Assert.Equal(31, state.ContinuousUseMinutes);
    }

    [Fact]
    public void ServiceStartAfterARebootWithAnUnlockedSessionIsARest()
    {
        // The PC was off from minute 0 to 25 and the child signed in before the service started.
        var state = BreakPlanner.ServiceStart(Rule, WithUse(40), At(30), hasUnlockedSession: true, bootUtc: At(25)).State;
        Assert.Equal(0, state.ContinuousUseMinutes);
    }

    [Fact]
    public void ServiceStartAfterAShortRebootKeepsTheValue()
    {
        var state = BreakPlanner.ServiceStart(Rule, WithUse(40), At(6), hasUnlockedSession: true, bootUtc: At(3)).State;
        Assert.Equal(40, state.ContinuousUseMinutes);
    }

    [Fact]
    public void ServiceStartWithoutASessionLetsTheRestRunFromTheLastUse()
    {
        // PC off overnight: the service starts before the sign-in, the sign-in ends the Rest.
        var state = BreakPlanner.ServiceStart(Rule, WithUse(40), At(600), hasUnlockedSession: false, bootUtc: At(599)).State;
        Assert.Equal(At(0), state.LastUseUtc);
        Assert.Equal(0, BreakPlanner.RestEnd(Rule, state, At(602)).State.ContinuousUseMinutes);
    }

    [Fact]
    public void LockedSessionAddsNoUse()
    {
        // Locked: the service counts no minute and reports no second Rest start.
        var state = BreakPlanner.RestStart(Rule, WithUse(30), At(0)).State;
        state = BreakPlanner.RestEnd(Rule, state, At(5)).State;
        Assert.Equal(30, state.ContinuousUseMinutes);
    }

    [Fact]
    public void RuleOffCountsNothing()
    {
        var off = Rule with { Enabled = false };
        Assert.Equal(0, Use(BreakState.Empty, 0, 30, off).ContinuousUseMinutes);
    }

    // --- 1.4 Warnings and start ---

    private static (BreakState State, List<BreakAction> Actions) RunTicks(BreakState state, int fromMinute, int toMinute, Func<int, BreakTick>? tick = null)
    {
        var actions = new List<BreakAction>();
        for (var m = fromMinute + 1; m <= toMinute; m++)
        {
            state = BreakPlanner.CountMinutes(Rule, state, At(m), 1).State;
            var result = BreakPlanner.Tick(Rule, state, At(m), tick?.Invoke(m) ?? Unlocked());
            state = result.State;
            actions.AddRange(result.Actions);
        }
        return (state, actions);
    }

    [Fact]
    public void WarningsAt5And1MinuteComeOneTimeEach()
    {
        var (_, actions) = RunTicks(BreakState.Empty, 0, 44);
        var warnings = actions.OfType<SendBreakWarning>().ToList();
        Assert.Equal(new[] { 5, 1 }, warnings.Select(w => w.MinutesLeft));
    }

    [Fact]
    public void WarningsDoNotRepeatInTheSameCycle()
    {
        var (state, _) = RunTicks(BreakState.Empty, 0, 44);
        var again = BreakPlanner.Tick(Rule, state, At(44.5), Unlocked());
        Assert.Empty(again.Actions);
    }

    [Fact]
    public void BreakStartsAtN()
    {
        var (state, actions) = RunTicks(BreakState.Empty, 0, 45);
        var start = Assert.Single(actions.OfType<StartBreak>());
        Assert.Equal(At(45), start.StartUtc);
        Assert.Equal(At(55), start.EndsUtc);
        Assert.True(state.IsBreakRunning(At(50)));
    }

    [Fact]
    public void NoWarningWhenTheAllowanceTimeEndsFirst()
    {
        // Continuous Use 40 of 45, the Allowance Time ends in 3 minutes.
        var result = BreakPlanner.Tick(Rule, WithUse(40), At(0), Unlocked(untilOtherLock: 3));
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void NoWarningWhenTheScheduleWindowEndsAtTheBreakStart()
    {
        var result = BreakPlanner.Tick(Rule, WithUse(40), At(0), Unlocked(untilOtherLock: 5));
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void WarningWhenTheAllowanceTimeEndsAfterTheBreak()
    {
        var result = BreakPlanner.Tick(Rule, WithUse(40), At(0), Unlocked(untilOtherLock: 6));
        Assert.Equal(5, Assert.IsType<SendBreakWarning>(Assert.Single(result.Actions)).MinutesLeft);
    }

    [Fact]
    public void NoBreakWhenTheAllowanceTimeLocksInTheSameTick()
    {
        var tick = new BreakTick(SessionUnlocked: true, OtherLockInThisTick: true, MinutesUntilOtherLock: 0, ScheduleEndUtc: null);
        var result = BreakPlanner.Tick(Rule, WithUse(45), At(0), tick);
        Assert.Empty(result.Actions);
        Assert.False(result.State.HasBreak);
    }

    [Fact]
    public void NoBreakForALockedSession()
    {
        var tick = new BreakTick(SessionUnlocked: false, OtherLockInThisTick: false, MinutesUntilOtherLock: null, ScheduleEndUtc: null);
        Assert.Empty(BreakPlanner.Tick(Rule, WithUse(45), At(0), tick).Actions);
    }

    [Fact]
    public void JumpPastTheFirstWarningSendsOnlyTheLastWarning()
    {
        var result = BreakPlanner.Tick(Rule, WithUse(44), At(0), Unlocked());
        Assert.Equal(1, Assert.IsType<SendBreakWarning>(Assert.Single(result.Actions)).MinutesLeft);
        Assert.Empty(BreakPlanner.Tick(Rule, result.State, At(0.5), Unlocked()).Actions);
    }

    [Fact]
    public void BonusMinutesAreNotAnInput()
    {
        // Continuous Use 44 of 45: the Break starts at 45, whatever the Allowance Time is.
        var state = BreakPlanner.CountMinutes(Rule, WithUse(44), At(1), 1).State;
        var result = BreakPlanner.Tick(Rule, state, At(1), Unlocked(untilOtherLock: 90));
        Assert.Single(result.Actions.OfType<StartBreak>());
    }

    // --- 1.5 End and changes ---

    private static BreakState Running(double start = 0, double minutes = 10) =>
        BreakState.Empty with { ContinuousUseMinutes = 45, LastUseUtc = At(start), BreakStartedUtc = At(start), BreakEndsUtc = At(start + minutes), Warned5 = true, Warned1 = true };

    [Fact]
    public void EndIsClampedToTheScheduleWindowEnd()
    {
        var result = BreakPlanner.Tick(Rule, WithUse(45), At(0), Unlocked(scheduleEnd: At(5)));
        Assert.Equal(At(5), Assert.Single(result.Actions.OfType<StartBreak>()).EndsUtc);

        var ended = BreakPlanner.Tick(Rule, result.State, At(5), Unlocked());
        Assert.Equal(BreakEndReason.ScheduleEnd, Assert.Single(ended.Actions.OfType<EndBreak>()).Reason);
    }

    [Fact]
    public void ScheduleEndAfterStartPlusMDoesNotChangeTheEnd()
    {
        var result = BreakPlanner.Tick(Rule, WithUse(45), At(0), Unlocked(scheduleEnd: At(60)));
        Assert.Equal(At(10), Assert.Single(result.Actions.OfType<StartBreak>()).EndsUtc);
    }

    [Fact]
    public void UnlockedSessionDuringABreakIsLockedAgain()
    {
        var result = BreakPlanner.Tick(Rule, Running(), At(4), Unlocked());
        Assert.Equal(At(10), Assert.IsType<LockAgain>(Assert.Single(result.Actions)).EndsUtc);
    }

    [Fact]
    public void LockedSessionDuringABreakNeedsNoAction()
    {
        var tick = new BreakTick(false, false, null, null);
        Assert.Empty(BreakPlanner.Tick(Rule, Running(), At(4), tick).Actions);
    }

    [Fact]
    public void ContinuousUseIsZeroAfterTheEnd()
    {
        var result = BreakPlanner.Tick(Rule, Running(), At(10), new BreakTick(false, false, null, null));
        Assert.Equal(BreakEndReason.Elapsed, Assert.Single(result.Actions.OfType<EndBreak>()).Reason);
        Assert.Equal(0, result.State.ContinuousUseMinutes);
        Assert.False(result.State.HasBreak);
        Assert.False(result.State.Warned5);
    }

    [Fact]
    public void UnlockAfterTheEndWorks()
    {
        var result = BreakPlanner.RestEnd(Rule, Running(), At(12));
        Assert.Single(result.Actions.OfType<EndBreak>());
        Assert.False(result.State.IsBreakRunning(At(12)));
        Assert.Empty(BreakPlanner.Tick(Rule, result.State, At(12.5), Unlocked()).Actions);
    }

    [Fact]
    public void RestEndDuringABreakChangesNothing()
    {
        var result = BreakPlanner.RestEnd(Rule, Running(), At(4));
        Assert.Equal(Running(), result.State);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void MinutesDuringABreakDoNotCount()
    {
        var result = BreakPlanner.CountMinutes(Rule, Running(), At(4), 1);
        Assert.Equal(45, result.State.ContinuousUseMinutes);
    }

    [Fact]
    public void ParentEndsTheRunningBreak()
    {
        var (result, outcome) = BreakPlanner.EndByParent(Rule, Running(), At(4));
        Assert.Equal(ParentEndOutcome.Ended, outcome);
        Assert.Equal(BreakEndReason.Parent, Assert.Single(result.Actions.OfType<EndBreak>()).Reason);
        Assert.Equal(0, result.State.ContinuousUseMinutes);
        Assert.False(result.State.HasBreak);
    }

    [Fact]
    public void ParentEndsOnlyTheBreakWithTheSameStart()
    {
        var state = Running(start: 60);
        var oldButton = BreakPlanner.ToUnixSeconds(At(0));
        var (result, outcome) = BreakPlanner.EndByParent(Rule, state, At(64), oldButton);
        Assert.Equal(ParentEndOutcome.OtherBreak, outcome);
        Assert.Equal(state, result.State);

        var (ended, ok) = BreakPlanner.EndByParent(Rule, state, At(64), BreakPlanner.ToUnixSeconds(At(60)));
        Assert.Equal(ParentEndOutcome.Ended, ok);
        Assert.False(ended.State.HasBreak);
    }

    [Fact]
    public void ParentEndWithoutABreakChangesNothing()
    {
        var (result, outcome) = BreakPlanner.EndByParent(Rule, WithUse(20), At(4));
        Assert.Equal(ParentEndOutcome.NoBreak, outcome);
        Assert.Equal(20, result.State.ContinuousUseMinutes);
    }

    [Fact]
    public void RuleOffEndsTheBreak()
    {
        var result = BreakPlanner.ChangeRule(Rule, Rule with { Enabled = false }, Running(), At(4));
        Assert.Equal(BreakEndReason.RuleOff, Assert.Single(result.Actions.OfType<EndBreak>()).Reason);
        Assert.False(result.State.HasBreak);
        Assert.Empty(BreakPlanner.Tick(Rule with { Enabled = false }, result.State, At(5), Unlocked()).Actions);
    }

    [Fact]
    public void RuleOnStartsAtZero()
    {
        var off = Rule with { Enabled = false };
        var result = BreakPlanner.ChangeRule(off, Rule, WithUse(40), At(0));
        Assert.Equal(0, result.State.ContinuousUseMinutes);
    }

    [Fact]
    public void ShorterNGivesNMinus5AndTheWarningsAgain()
    {
        var oldRule = Rule with { UseMinutes = 60 };
        var newRule = Rule with { UseMinutes = 30 };
        var state = WithUse(40) with { Warned5 = true };
        var changed = BreakPlanner.ChangeRule(oldRule, newRule, state, At(0)).State;
        Assert.Equal(25, changed.ContinuousUseMinutes);
        Assert.False(changed.Warned5);

        var tick = BreakPlanner.Tick(newRule, changed, At(0), Unlocked());
        Assert.Equal(5, Assert.IsType<SendBreakWarning>(Assert.Single(tick.Actions)).MinutesLeft);

        // The Break starts 5 minutes later.
        var s = tick.State;
        StartBreak? start = null;
        for (var m = 1; m <= 5; m++)
        {
            s = BreakPlanner.CountMinutes(newRule, s, At(m), 1).State;
            var r = BreakPlanner.Tick(newRule, s, At(m), Unlocked());
            s = r.State;
            start ??= r.Actions.OfType<StartBreak>().SingleOrDefault();
        }
        Assert.Equal(At(5), start?.StartUtc);
    }

    [Fact]
    public void NewMKeepsTheEndOfARunningBreak()
    {
        var result = BreakPlanner.ChangeRule(Rule, Rule with { BreakMinutes = 30 }, Running(), At(4));
        Assert.Equal(At(10), result.State.BreakEndsUtc);
        Assert.Empty(result.Actions);
    }

    [Theory]
    [InlineData(6, 4)]
    [InlineData(5.1, 5)]
    [InlineData(0, 10)]
    [InlineData(9.9, 1)]
    [InlineData(10, 0)]
    public void MinutesLeftIsRoundedUp(double now, int expected) =>
        Assert.Equal(expected, BreakPlanner.MinutesLeft(At(now), At(10)));
}
