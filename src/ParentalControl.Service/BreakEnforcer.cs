using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Domain.Breaks;

namespace ParentalControl.Service;

/// <summary>
/// Runs the Breaks of the Restricted Users (design D13). It stores the Break Rule and the break state through
/// <see cref="BreakRepository"/>, calls the pure <see cref="BreakPlanner"/> and runs its actions.
/// It does not depend on <see cref="SessionTracker"/>: <see cref="SessionTracker"/> calls it, and the tick gets
/// the tracker as an argument. A Break never closes a program: it locks the session.
/// </summary>
public sealed class BreakEnforcer
{
    private readonly object _gate = new();
    private readonly Serilog.ILogger _logger;

    public BreakEnforcer(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    // --- Count and Rest (called by SessionTracker) ---

    /// <summary>Adds the elapsed minutes that the service also added to the Allowance Time.</summary>
    public void OnMinutesCounted(User user, int minutes)
    {
        if (!user.IsRestricted || minutes <= 0) return;
        Run(user, Apply(user.Id, (rule, state, now) => BreakPlanner.CountMinutes(rule, state, now, minutes)));
    }

    /// <summary>A Rest starts: a session of the user that the service tracked as unlocked locks, signs out or sleeps.</summary>
    public void OnRestStart(User user)
    {
        if (!user.IsRestricted) return;
        Run(user, Apply(user.Id, BreakPlanner.RestStart));
    }

    /// <summary>A Rest ends: an accepted unlock, a sign-in or a wake.</summary>
    public void OnRestEnd(User user)
    {
        if (!user.IsRestricted) return;
        Run(user, Apply(user.Id, BreakPlanner.RestEnd));
    }

    /// <summary>The service starts. <paramref name="hasUnlockedSession"/>: a session of the user shows as unlocked.</summary>
    public void OnServiceStart(User user, bool hasUnlockedSession)
    {
        if (!user.IsRestricted) return;
        var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        Run(user, Apply(user.Id, (rule, state, now) => BreakPlanner.ServiceStart(rule, state, now, hasUnlockedSession, bootUtc)));
        _logger.Information("Break state of {Username} at the service start: {State} (unlocked session: {Unlocked}, boot {Boot:o})",
            user.Username, BreakRepository.GetState(user.Id), hasUnlockedSession, bootUtc);
    }

    /// <summary>The end of the running Break of the user, or null if no Break runs.</summary>
    public DateTime? GetRunningBreakEnd(int userId)
    {
        var state = BreakRepository.GetState(userId);
        return state.IsBreakRunning(DateTime.UtcNow) ? state.BreakEndsUtc : null;
    }

    // --- Core ---

    private BreakResult Apply(int userId, Func<BreakRule, BreakState, DateTime, BreakResult> step) =>
        Apply(userId, (rule, state, now) => (step(rule, state, now), true)).Result;

    /// <summary>Reads the rule and the state, runs one planner step and stores a changed state, in one lock.</summary>
    private (BreakResult Result, T Extra) Apply<T>(int userId, Func<BreakRule, BreakState, DateTime, (BreakResult, T)> step)
    {
        lock (_gate)
        {
            var rule = BreakRepository.GetRule(userId);
            var state = BreakRepository.GetState(userId);
            var (result, extra) = step(rule, state, DateTime.UtcNow);
            if (result.State != state) BreakRepository.SaveState(userId, result.State);
            return (result, extra);
        }
    }

    /// <summary>Runs the actions that need no session: the events of an ended Break.</summary>
    private void Run(User user, BreakResult result)
    {
        foreach (var action in result.Actions)
        {
            if (action is EndBreak end)
            {
                var reason = BreakPlanner.ReasonText(end.Reason);
                EventRepository.LogEvent(user.Sid, EventType.BREAK_ENDED, reason);
                _logger.Information("Break of {Username} ended ({Reason})", user.Username, reason);
            }
        }
    }
}
