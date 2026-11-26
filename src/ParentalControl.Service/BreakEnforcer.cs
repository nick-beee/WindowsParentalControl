using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Breaks;
using ParentalControl.Domain.Messages;

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

    // --- Tick (called by UsageMonitorWorker) ---

    /// <summary>
    /// The break check of a tick, for each Restricted User. It runs after the checks of the Allowance Time and the
    /// Schedule Window (design D8): a session that they locked in this tick is locked now, so no Break starts for it.
    /// It also ends a Break at its end time for a user whose session is locked or signed out.
    /// </summary>
    public void Tick(SessionTracker tracker)
    {
        var nowLocal = DateTime.Now;
        foreach (var user in UserRepository.GetAll().Where(u => u.IsRestricted))
        {
            try
            {
                TickUser(tracker, user, nowLocal);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during the break check of {Username}", user.Username);
            }
        }
    }

    private void TickUser(SessionTracker tracker, User user, DateTime nowLocal)
    {
        var unlockedSessions = tracker.ActiveSessions.Values
            .Where(s => s.UserSid == user.Sid && !s.IsLocked)
            .Select(s => s.SessionId)
            .ToList();
        var (minutesUntilOtherLock, scheduleEndUtc) = OtherLockTimes(user.Id, nowLocal);
        var tick = new BreakTick(
            SessionUnlocked: unlockedSessions.Count > 0,
            OtherLockInThisTick: false,
            MinutesUntilOtherLock: minutesUntilOtherLock,
            ScheduleEndUtc: scheduleEndUtc);

        var result = Apply(user.Id, (rule, state, now) => BreakPlanner.Tick(rule, state, now, tick));
        Run(user, result);

        foreach (var action in result.Actions)
        {
            switch (action)
            {
                case SendBreakWarning warning:
                    SendWarning(user, unlockedSessions, warning.MinutesLeft);
                    break;
                case StartBreak start:
                    StartBreakInSessions(tracker, user, unlockedSessions, start);
                    break;
            }
        }
    }

    /// <summary>
    /// The minutes until the Allowance Time or the Schedule Window ends, and today's Schedule Window end in UTC.
    /// Both are null without a schedule. The same rule as in <see cref="UsageMonitorWorker"/>.
    /// </summary>
    private static (int? MinutesUntilOtherLock, DateTime? ScheduleEndUtc) OtherLockTimes(int userId, DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        var limit = ScheduleRepository.GetEffectiveLimit(userId, nowLocal.DayOfWeek, today);
        if (limit is null) return (null, null);

        var usage = UsageRepository.GetUsage(userId, today);
        var remainingDaily = Math.Max(0, limit.DailyMinutes + (usage?.BonusMinutes ?? 0) - (usage?.MinutesUsed ?? 0));

        var now = TimeOnly.FromDateTime(nowLocal);
        if (now >= limit.ScheduleEnd) return (0, null);
        var remainingSchedule = (int)(limit.ScheduleEnd.ToTimeSpan() - now.ToTimeSpan()).TotalMinutes;
        var scheduleEndUtc = today.ToDateTime(limit.ScheduleEnd, DateTimeKind.Local).ToUniversalTime();
        return (Math.Min(remainingDaily, remainingSchedule), scheduleEndUtc);
    }

    private void SendWarning(User user, IReadOnlyList<int> sessions, int minutesLeft)
    {
        var title = SessionTexts.For(SettingsRepository.GetLanguagePreset())["break_title"];
        var message = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBreakWarn, BreakTexts.WarnValues(minutesLeft));
        foreach (var sessionId in sessions)
        {
            NotificationManager.SendMessage(sessionId, title, message, isWarning: true, timeoutSeconds: 30);
            _logger.Information("Dispatched the {Minutes}m break warning to {Username} on session {SessionId}",
                minutesLeft, user.Username, sessionId);
        }
    }

    /// <summary>
    /// Starts a Break: the message with the end time, then the Enforced Workstation Lock. The programs stay open.
    /// The state with the Break is already stored, so an unlock during the message is locked again.
    /// </summary>
    private void StartBreakInSessions(SessionTracker tracker, User user, IReadOnlyList<int> sessions, StartBreak start)
    {
        var endText = BreakTexts.EndText(start.EndsUtc, TimeZoneInfo.Local);
        var minutes = BreakPlanner.MinutesLeft(start.StartUtc, start.EndsUtc);
        EventRepository.LogEvent(user.Sid, EventType.BREAK_STARTED, $"Until {endText} ({minutes} min)");
        _logger.Information("Break of {Username} starts: {Minutes} min until {End}", user.Username, minutes, endText);

        var title = SessionTexts.For(SettingsRepository.GetLanguagePreset())["break_title"];
        var message = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBreakStart,
            BreakTexts.StartValues(start.StartUtc, start.EndsUtc, TimeZoneInfo.Local));
        foreach (var sessionId in sessions)
        {
            NotificationManager.SendMessage(sessionId, title, message, isWarning: true, timeoutSeconds: 5, wait: true);
            SessionManager.LockSession(sessionId);
            tracker.OnSessionLock(sessionId, $"Break until {endText} - workstation locked");
        }
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
