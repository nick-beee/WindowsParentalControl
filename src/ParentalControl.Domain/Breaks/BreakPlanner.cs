namespace ParentalControl.Domain.Breaks;

/// <summary>The reason why a Break ends. <see cref="BreakPlanner.ReasonText"/> gives the text of the event <c>BREAK_ENDED</c>.</summary>
public enum BreakEndReason
{
    Elapsed,
    ScheduleEnd,
    Parent,
    RuleOff,
}

/// <summary>A thing that the service must do after a planner step.</summary>
public abstract record BreakAction;

/// <summary>Send the break warning with the minutes of use that remain before the Break.</summary>
public sealed record SendBreakWarning(int MinutesLeft) : BreakAction;

/// <summary>Send the break start message, then lock the session with the Enforced Workstation Lock.</summary>
public sealed record StartBreak(DateTime StartUtc, DateTime EndsUtc) : BreakAction;

/// <summary>A Break ended. Write the event. The service does not unlock the session.</summary>
public sealed record EndBreak(BreakEndReason Reason, DateTime StartUtc) : BreakAction;

/// <summary>The session shows as unlocked during a Break: send the running message and lock the session again.</summary>
public sealed record LockAgain(DateTime EndsUtc) : BreakAction;

/// <summary>The result of a planner step: the new state (to store) and the actions (to run after the store).</summary>
public sealed record BreakResult(BreakState State, IReadOnlyList<BreakAction> Actions);

/// <summary>The input of a tick for one Restricted User.</summary>
/// <param name="SessionUnlocked">A session of the user shows as unlocked after the checks of the Allowance Time and the Schedule Window.</param>
/// <param name="OtherLockInThisTick">The Allowance Time or the Schedule Window locked a session of the user in this tick.</param>
/// <param name="MinutesUntilOtherLock">The minutes until the Allowance Time or the Schedule Window ends, or null without a limit.</param>
/// <param name="ScheduleEndUtc">The end of today's Schedule Window, or null without a schedule.</param>
public readonly record struct BreakTick(
    bool SessionUnlocked,
    bool OtherLockInThisTick,
    int? MinutesUntilOtherLock,
    DateTime? ScheduleEndUtc);

/// <summary>The result of an end of a Break by the parent.</summary>
public enum ParentEndOutcome
{
    /// <summary>The Break ended.</summary>
    Ended,

    /// <summary>No Break runs.</summary>
    NoBreak,

    /// <summary>A Break runs, but it is not the Break of the button.</summary>
    OtherBreak,
}

/// <summary>
/// The pure logic of the Breaks: the count of Continuous Use, the Rest, the warnings, the start and the end of a Break,
/// the end by the parent and the change of the Break Rule. All times are UTC. The planner does no I/O.
/// Each step first ends a stored Break whose end time is past, so that no step works on an old Break.
/// </summary>
public static class BreakPlanner
{
    /// <summary>Adds counted minutes of unlocked use. A gap of M minutes or more since the last use is a Rest.</summary>
    public static BreakResult CountMinutes(BreakRule rule, BreakState state, DateTime nowUtc, int minutes)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        if (!rule.Enabled || minutes <= 0 || settled.IsBreakRunning(nowUtc))
            return new BreakResult(settled, actions);

        if (settled.LastUseUtc is { } last && (nowUtc - last).TotalMinutes - minutes >= rule.BreakMinutes)
            settled = settled.ResetCycle();

        settled = settled with
        {
            ContinuousUseMinutes = settled.ContinuousUseMinutes + minutes,
            LastUseUtc = nowUtc,
        };
        return new BreakResult(settled, actions);
    }

    /// <summary>A Rest starts: a session that was unlocked locks, signs out or sleeps.</summary>
    public static BreakResult RestStart(BreakRule rule, BreakState state, DateTime nowUtc)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        return new BreakResult(settled with { LastUseUtc = nowUtc }, actions);
    }

    /// <summary>
    /// A Rest ends: an accepted unlock, a sign-in or a wake. A Rest of M minutes or more sets Continuous Use to 0.
    /// The time of the last use becomes the end of the Rest.
    /// </summary>
    public static BreakResult RestEnd(BreakRule rule, BreakState state, DateTime nowUtc)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        if (settled.IsBreakRunning(nowUtc))
            return new BreakResult(settled, actions);

        if (settled.LastUseUtc is { } last && nowUtc - last >= TimeSpan.FromMinutes(rule.BreakMinutes))
            settled = settled.ResetCycle();

        return new BreakResult(settled with { LastUseUtc = nowUtc }, actions);
    }

    /// <summary>
    /// The service starts. If the PC started after the last use, the PC was off, and the time since the last use is a Rest.
    /// Else, with an unlocked session, the down time counts neither as use nor as a Rest.
    /// Else, the Rest started at the last use.
    /// </summary>
    public static BreakResult ServiceStart(BreakRule rule, BreakState state, DateTime nowUtc, bool hasUnlockedSession, DateTime bootUtc)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        if (settled.IsBreakRunning(nowUtc))
            return new BreakResult(settled, actions);

        var pcWasOff = settled.LastUseUtc is { } last && bootUtc > last;
        if (pcWasOff)
        {
            if (!hasUnlockedSession) return new BreakResult(settled, actions);
            var ended = RestEnd(rule, settled, nowUtc);
            return new BreakResult(ended.State, actions.Concat(ended.Actions).ToList());
        }

        return hasUnlockedSession
            ? new BreakResult(settled with { LastUseUtc = nowUtc }, actions)
            : new BreakResult(settled, actions);
    }

    /// <summary>
    /// The tick of one Restricted User: ends a Break at its end time, locks an unlocked session during a Break again,
    /// sends the warnings and starts a Break when Continuous Use reaches N.
    /// </summary>
    public static BreakResult Tick(BreakRule rule, BreakState state, DateTime nowUtc, BreakTick tick)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        var list = actions.ToList();

        if (settled.IsBreakRunning(nowUtc))
        {
            if (tick.SessionUnlocked) list.Add(new LockAgain(settled.BreakEndsUtc!.Value));
            return new BreakResult(settled, list);
        }

        if (!rule.Enabled || !tick.SessionUnlocked || tick.OtherLockInThisTick)
            return new BreakResult(settled, list);

        var minutesLeft = rule.UseMinutes - settled.ContinuousUseMinutes;
        if (minutesLeft <= 0)
        {
            var end = nowUtc.AddMinutes(rule.BreakMinutes);
            if (tick.ScheduleEndUtc is { } scheduleEnd && scheduleEnd > nowUtc && scheduleEnd < end)
                end = scheduleEnd;

            settled = settled with { BreakStartedUtc = nowUtc, BreakEndsUtc = end, Warned5 = true, Warned1 = true };
            list.Add(new StartBreak(nowUtc, end));
            return new BreakResult(settled, list);
        }

        // The Allowance Time or the Schedule Window ends at or before the start of the Break: its own warnings come instead.
        var otherLockFirst = tick.MinutesUntilOtherLock is { } other && other <= minutesLeft;
        if (!otherLockFirst)
        {
            if (minutesLeft <= BreakRule.LastWarningMinutes && !settled.Warned1)
            {
                settled = settled with { Warned5 = true, Warned1 = true };
                list.Add(new SendBreakWarning(minutesLeft));
            }
            else if (minutesLeft <= BreakRule.FirstWarningMinutes && !settled.Warned5)
            {
                settled = settled with { Warned5 = true };
                list.Add(new SendBreakWarning(minutesLeft));
            }
        }

        return new BreakResult(settled, list);
    }

    /// <summary>
    /// The parent ends the running Break. With <paramref name="expectedStartUnixSeconds"/> (a Telegram button),
    /// only the Break with that start ends. The session stays locked.
    /// </summary>
    public static (BreakResult Result, ParentEndOutcome Outcome) EndByParent(
        BreakRule rule, BreakState state, DateTime nowUtc, long? expectedStartUnixSeconds = null)
    {
        var (settled, actions) = Settle(rule, state, nowUtc);
        if (!settled.IsBreakRunning(nowUtc))
            return (new BreakResult(settled, actions), ParentEndOutcome.NoBreak);

        var start = settled.BreakStartedUtc!.Value;
        if (expectedStartUnixSeconds is { } expected && expected != ToUnixSeconds(start))
            return (new BreakResult(settled, actions), ParentEndOutcome.OtherBreak);

        var list = actions.ToList();
        list.Add(new EndBreak(BreakEndReason.Parent, start));
        return (new BreakResult(settled.ResetCycle(), list), ParentEndOutcome.Ended);
    }

    /// <summary>
    /// The parent changes the Break Rule. Off ends a running Break and resets the state. On after off starts at 0.
    /// A new N gives Continuous Use = min(value, N − 5) and resets the warnings. A new M does not move a running Break.
    /// </summary>
    public static BreakResult ChangeRule(BreakRule oldRule, BreakRule newRule, BreakState state, DateTime nowUtc)
    {
        var (settled, actions) = Settle(oldRule, state, nowUtc);
        var list = actions.ToList();

        if (!newRule.Enabled)
        {
            if (settled.IsBreakRunning(nowUtc))
                list.Add(new EndBreak(BreakEndReason.RuleOff, settled.BreakStartedUtc!.Value));
            return new BreakResult(settled.ResetCycle(), list);
        }

        if (!oldRule.Enabled)
            return new BreakResult(settled.ResetCycle(), list);

        if (newRule.UseMinutes != oldRule.UseMinutes)
        {
            var cap = Math.Max(0, newRule.UseMinutes - BreakRule.FirstWarningMinutes);
            settled = settled with
            {
                ContinuousUseMinutes = Math.Min(settled.ContinuousUseMinutes, cap),
                Warned5 = false,
                Warned1 = false,
            };
        }

        return new BreakResult(settled, list);
    }

    /// <summary>The whole minutes from now to the end, rounded up. A past end gives 0.</summary>
    public static int MinutesLeft(DateTime nowUtc, DateTime endUtc)
    {
        var minutes = (endUtc - nowUtc).TotalMinutes;
        return minutes <= 0 ? 0 : (int)Math.Ceiling(minutes - 1e-9);
    }

    public static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>The detail text of the event <c>BREAK_ENDED</c>.</summary>
    public static string ReasonText(BreakEndReason reason) => reason switch
    {
        BreakEndReason.Elapsed => "elapsed",
        BreakEndReason.ScheduleEnd => "schedule end",
        BreakEndReason.Parent => "parent",
        BreakEndReason.RuleOff => "rule off",
        _ => reason.ToString(),
    };

    /// <summary>Ends a stored Break whose end time is past. Continuous Use becomes 0.</summary>
    private static (BreakState State, IReadOnlyList<BreakAction> Actions) Settle(BreakRule rule, BreakState state, DateTime nowUtc)
    {
        if (!state.HasBreak || state.IsBreakRunning(nowUtc))
            return (state, Array.Empty<BreakAction>());

        var start = state.BreakStartedUtc ?? state.BreakEndsUtc!.Value;
        var end = state.BreakEndsUtc!.Value;
        var reason = end < start.AddMinutes(rule.BreakMinutes) ? BreakEndReason.ScheduleEnd : BreakEndReason.Elapsed;
        return (state.ResetCycle(), new BreakAction[] { new EndBreak(reason, start) });
    }
}
