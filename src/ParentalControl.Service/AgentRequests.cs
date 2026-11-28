using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Services;
using ParentalControl.Domain.Captures;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.Sessions;
using ParentalControl.Domain.Time;

namespace ParentalControl.Service;

/// <summary>
/// The handlers of the Agent Channel messages (session-probe-and-agent-channel, design D8). The user and the session of
/// each message come from the launch record of its channel, never from the message. Agent data only shows texts,
/// answers prompts, stores captures and asks for a Session End Hold: no limit reads it (design D10).
/// <para>
/// It also holds the capture commands of the parent, which the web admin and the Telegram bot set and the status
/// answer reads.
/// </para>
/// </summary>
public sealed class AgentRequests
{
    /// <summary>The longest time between the creation of a prompt and its answer (<see cref="SessionPromptRepository"/>).</summary>
    private static readonly TimeSpan PromptLifetime = TimeSpan.FromMinutes(5);

    private readonly SessionTracker _sessionTracker;
    private readonly CategoryEnforcer _categoryEnforcer;
    private readonly SessionLocker _sessionLocker;
    private readonly TelegramBotService? _telegramBotService;
    private readonly Serilog.ILogger _logger;
    private readonly Dictionary<int, (int Files, long Bytes)> _captureReservations = new();
    private readonly CaptureRefusalLog _captureRefusals = new();

    public AgentRequests(SessionTracker sessionTracker, CategoryEnforcer categoryEnforcer, SessionLocker sessionLocker,
        TelegramBotService? telegramBotService, Serilog.ILogger logger)
    {
        _sessionTracker = sessionTracker;
        _categoryEnforcer = categoryEnforcer;
        _sessionLocker = sessionLocker;
        _telegramBotService = telegramBotService;
        _logger = logger;
    }

    /// <summary>The capture commands of the parent per user name.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, CaptureCommandState> CaptureStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A capture of an agent is stored.</summary>
    public static event Action<ScreenCaptureRecord>? CaptureReceived;

    internal static void RaiseCaptureReceived(ScreenCaptureRecord record) => CaptureReceived?.Invoke(record);

    /// <summary>
    /// The status answer for the user of the launch record (spec "The status shows only the data of the agent user").
    /// It clears a capture request only of that user.
    /// </summary>
    /// <param name="captureTaken">True if this status took the capture request of the parent.</param>
    public AgentStatus Status(AgentLaunchRecord record, out bool captureTaken)
    {
        captureTaken = false;
        var user = UserRepository.GetBySid(record.UserSid);
        // The Session Agent shows its texts in this Language Preset. The preset comes only from app_settings.
        var language = SettingsRepository.GetLanguagePreset();
        var activePromptObj = user is null ? null : ActivePrompt(user);

        if (user is null || !user.IsRestricted)
        {
            return new AgentStatus(
                username: AgentStatusLimits.Text(user?.Username, AgentStatusLimits.MaxShortChars),
                isRestricted: false,
                remainingSeconds: 0,
                curfew: AgentStatusLimits.Text(AgentTexts.For(language)["status_unrestricted"], AgentStatusLimits.MaxShortChars),
                isLocked: false,
                isCurfewClamped: false,
                lastDeclinedId: null,
                lastDeclinedReason: null,
                lastDeclinedTime: null,
                captureRequested: false,
                watchIntervalSeconds: 0,
                language: AgentStatusLimits.Text(language, AgentStatusLimits.MaxShortChars),
                activePrompt: activePromptObj,
                suspendedApps: null,
                suspendedAppsTotal: 0,
                pendingApps: null,
                pendingAppsTotal: 0);
        }

        var moment = ControlTime.Current.Read(); // control-clock: the day of the Control Time Zone
        var today = moment.Today;
        var limit = ScheduleRepository.GetEffectiveLimit(user.Id, moment.DayOfWeek, today);
        var usage = UsageRepository.GetUsage(user.Id, today);
        var totalAllowed = (limit?.DailyMinutes ?? 120) + (usage?.BonusMinutes ?? 0);
        var used = UsageLedger.Shared.AllowanceUsed(user.Id, today, usage);
        var remMinutes = Math.Max(0, totalAllowed - used);

        // The session of the launch record, not the first session of the user.
        _sessionTracker.ActiveSessions.TryGetValue(record.SessionId, out var session);
        var isLocked = session?.IsLocked ?? false;
        var elapsedSec = session != null && !isLocked ? ElapsedTime.Seconds(session.LastTickMono, Environment.TickCount64) : 0;
        var dailySec = Math.Max(0, (remMinutes * 60) - Math.Min(59, Math.Max(0, elapsedSec)));

        var nowTime = moment.TimeOfDay.ToTimeSpan();
        var curfewSec = int.MaxValue;
        if (limit != null)
        {
            var startSpan = limit.ScheduleStart.ToTimeSpan();
            var endSpan = limit.ScheduleEnd.ToTimeSpan();
            curfewSec = nowTime < startSpan || nowTime >= endSpan ? 0 : Math.Max(0, (int)(endSpan - nowTime).TotalSeconds);
        }

        var remainingSeconds = Math.Min(dailySec, curfewSec);
        var isCurfewClamped = curfewSec < dailySec;

        var latestReq = GraceRequestRepository.GetLatestRequest(user.Id);
        int? lastDeclinedId = null;
        string? lastDeclinedReason = null;
        DateTime? lastDeclinedTime = null;
        if (latestReq != null && string.Equals(latestReq.Status, "DECLINED", StringComparison.OrdinalIgnoreCase))
        {
            lastDeclinedId = latestReq.Id;
            lastDeclinedReason = latestReq.DeclineReason;
            lastDeclinedTime = latestReq.ResolvedAt;
        }

        var curfew = limit != null ? $"{limit.ScheduleStart:HH:mm} – {limit.ScheduleEnd:HH:mm}" : "08:00 – 22:00";

        var captureRequested = false;
        var watchInterval = 0;
        if (CaptureStates.TryGetValue(user.Username, out var capState))
        {
            if (capState.WatchMode && DateTime.Now < capState.WatchExpiresAt)
                watchInterval = 10;
            if (capState.CaptureRequested)
            {
                captureRequested = true;
                captureTaken = true;
                capState.CaptureRequested = false;
            }
        }

        // The Suspended Apps (the mute and the resume at the end of the session) and the Pending Suspensions (the agent
        // minimizes their windows). Both name only processes of the child. Each list has a cap, so that the answer
        // always fits in one frame (AgentStatusLimits).
        var suspended = AgentStatusLimits.Apps(SuspensionRepository.GetForUser(user.Id).Select(r => (r.Pid, r.StartTimeUtc)));
        var pending = AgentStatusLimits.Apps(_categoryEnforcer.GetPendingApps(user.Id).Select(p => (p.Pid, p.StartTimeUtc)));

        return new AgentStatus(
            username: AgentStatusLimits.Text(user.Username, AgentStatusLimits.MaxShortChars),
            isRestricted: true,
            remainingSeconds: remainingSeconds,
            curfew: AgentStatusLimits.Text(curfew, AgentStatusLimits.MaxShortChars),
            isLocked: isLocked,
            isCurfewClamped: isCurfewClamped,
            lastDeclinedId: lastDeclinedId,
            lastDeclinedReason: AgentStatusLimits.Text(lastDeclinedReason, AgentStatusLimits.MaxReasonChars),
            lastDeclinedTime: lastDeclinedTime,
            captureRequested: captureRequested,
            watchIntervalSeconds: watchInterval,
            language: AgentStatusLimits.Text(language, AgentStatusLimits.MaxShortChars),
            activePrompt: activePromptObj,
            suspendedApps: suspended.Apps,
            suspendedAppsTotal: suspended.Total,
            pendingApps: pending.Apps,
            pendingAppsTotal: pending.Total);
    }

    /// <summary>Gives a capture request back after its status answer did not reach the agent.</summary>
    public void RestoreCaptureRequest(AgentLaunchRecord record)
    {
        var user = UserRepository.GetBySid(record.UserSid);
        if (user is not null && CaptureStates.TryGetValue(user.Username, out var state))
            state.CaptureRequested = true;
    }

    private static AgentStatusPrompt? ActivePrompt(User user)
    {
        var activePrompt = SessionPromptRepository.GetActivePrompt(user.Username);
        if (activePrompt is null || activePrompt.UserSid != user.Sid) return null;
        var options = !string.IsNullOrWhiteSpace(activePrompt.CustomOptions)
            ? activePrompt.CustomOptions.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList()
            : SettingsRepository.GetPromptResponsePresets();
        return AgentStatusLimits.Prompt(activePrompt.Id, activePrompt.Message, activePrompt.Urgency, activePrompt.TargetDisplay,
            timeoutSeconds: 120, options);
    }

    /// <summary>
    /// Records the answer to an Interactive Session Prompt only for an active prompt of the user of the launch record
    /// (spec "Prompt answers only for the prompts of the agent user"). Else it changes nothing and returns false.
    /// </summary>
    public bool AnswerPrompt(AgentLaunchRecord record, AgentPromptAnswer answer)
    {
        var prompt = SessionPromptRepository.GetById(answer.PromptId);
        if (prompt is null || prompt.UserSid != record.UserSid || prompt.Status != "PENDING"
            || DateTime.Now - prompt.CreatedAt > PromptLifetime)
        {
            _logger.Warning("Prompt answer for prompt {PromptId} refused: not an active prompt of the agent user {Sid} (session {SessionId})",
                answer.PromptId, record.UserSid, record.SessionId);
            return false;
        }

        var response = answer.Response.ToUpperInvariant() switch
        {
            "YES" => "YES",
            "TIMEOUT" => "TIMEOUT",
            _ => "NO",
        };
        var turnaroundSeconds = Math.Clamp(answer.TurnaroundSeconds, 0, (int)PromptLifetime.TotalSeconds);
        if (!SessionPromptRepository.ResolvePrompt(prompt.Id, response, answer.Reason, turnaroundSeconds))
            return false;

        if (response == "YES")
            EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_ANSWERED, $"Prompt #{prompt.Id} dijawab (YA) dalam {turnaroundSeconds}s");
        else if (response == "TIMEOUT")
            EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_TIMEOUT, $"Prompt #{prompt.Id} tidak dijawab (Timeout {turnaroundSeconds}s)");
        else
        {
            var reasonDesc = !string.IsNullOrWhiteSpace(answer.Reason) ? $": \"{answer.Reason}\"" : "";
            EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_ANSWERED, $"Prompt #{prompt.Id} dijawab (TIDAK){reasonDesc} dalam {turnaroundSeconds}s");
        }

        prompt.Response = response;
        prompt.ResponseReason = answer.Reason;
        prompt.TurnaroundSeconds = turnaroundSeconds;
        prompt.Status = response == "TIMEOUT" ? "TIMEOUT" : "ANSWERED";

        if (_telegramBotService is { } telegram)
        {
            var sessionId = record.SessionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await telegram.SendPromptResponseAlertAsync(prompt, sessionId);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to send prompt response alert to Telegram");
                }
            });
        }
        return true;
    }

    /// <summary>
    /// The check of a <c>capture</c> JSON frame before the codec allocates the buffer of its JPEG frame: the user of the
    /// launch record and the daily quota. <see cref="StoreCapture"/> checks the quota again with a reservation.
    /// </summary>
    public CaptureAdmission AdmitCapture(AgentLaunchRecord record)
    {
        var user = UserRepository.GetBySid(record.UserSid);
        if (user is null) return CaptureAdmission.Refuse("unknownUser");
        CaptureQuotaDecision quota;
        int filesToday;
        long bytesToday;
        lock (_captureReservations)
        {
            (filesToday, bytesToday) = ScreenCaptureRepository.GetDayTotals(user.Id, ControlTime.Current.StartOfToday);
            _captureReservations.TryGetValue(user.Id, out var reserved);
            quota = CaptureQuota.Check(filesToday + reserved.Files, bytesToday + reserved.Bytes);
        }
        if (quota.Allowed) return CaptureAdmission.Allow(quota.MaxBytes);
        if (_captureRefusals.Count(user.Id, ControlTime.Current.Today))
            _logger.Warning("Capture refused for {Username} before its JPEG: the daily quota is used ({Outcome}: {Files} files, {Bytes} bytes). " +
                            "Later refusals today are not logged.", user.Username, quota.Outcome, filesToday, bytesToday);
        return CaptureAdmission.Refuse("quota");
    }

    /// <summary>Logs a capture that the codec skipped because its JPEG frame was larger than the admission allowed.</summary>
    public void CaptureSkipped(AgentLaunchRecord record, AgentCapture capture)
    {
        if (capture.Refused != "tooLarge") return;
        var user = UserRepository.GetBySid(record.UserSid);
        if (user is not null && _captureRefusals.Count(user.Id, ControlTime.Current.Today))
            _logger.Warning("Capture refused for {Username}: the JPEG frame is larger than the quota allows. Later refusals today are not logged.",
                user.Username);
    }

    /// <summary>
    /// Stores a screen capture of the agent for the user of the launch record, under the daily quota (design D8).
    /// <c>admin_ip</c> is NULL. Returns the outcome for the answer.
    /// </summary>
    public string StoreCapture(AgentLaunchRecord record, AgentCapture capture)
    {
        var user = UserRepository.GetBySid(record.UserSid);
        if (user is null) return "unknownUser";
        if (capture.Jpeg.Length == 0) return "empty";

        CaptureQuotaDecision quota;
        int filesToday;
        long bytesToday;
        lock (_captureReservations)
        {
            (filesToday, bytesToday) = ScreenCaptureRepository.GetDayTotals(user.Id, ControlTime.Current.StartOfToday);
            _captureReservations.TryGetValue(user.Id, out var reserved);
            quota = CaptureQuota.Check(filesToday + reserved.Files, bytesToday + reserved.Bytes);
            if (quota.Allowed)
                _captureReservations[user.Id] = (reserved.Files + 1, reserved.Bytes + quota.MaxBytes);
        }
        try
        {
            if (!quota.Allowed)
            {
                if (_captureRefusals.Count(user.Id, ControlTime.Current.Today))
                    _logger.Warning("Capture refused for {Username}: the daily quota is used ({Outcome}: {Files} files, {Bytes} bytes). " +
                                    "Later refusals today are not logged.", user.Username, quota.Outcome, filesToday, bytesToday);
                return "quota";
            }
            if (capture.Jpeg.Length > quota.MaxBytes)
            {
                if (_captureRefusals.Count(user.Id, ControlTime.Current.Today))
                    _logger.Warning("Capture refused for {Username}: the capture is larger than {Max} bytes. Later refusals today are not logged.",
                        user.Username, quota.MaxBytes);
                return "tooLarge";
            }

            var userCapturesDir = Path.Combine(DatabaseManager.CapturesDirectory, user.Id.ToString());
            Directory.CreateDirectory(userCapturesDir);
            var filePath = Path.Combine(userCapturesDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");
            using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(capture.Jpeg);

            var stored = ScreenCaptureRepository.Add(new ScreenCaptureRecord
            {
                UserId = user.Id,
                Timestamp = ControlTime.Current.Now, // the capture quota counts from midnight of the Control Time Zone
                FilePath = filePath,
                Width = capture.Width > 0 ? capture.Width : 1920,
                Height = capture.Height > 0 ? capture.Height : 1080,
                FileSizeBytes = capture.Jpeg.Length,
                TriggerType = capture.Trigger,
                AdminIp = null,
            });
            _logger.Information("Screen capture recorded for {Username} ({Width}x{Height}, {Bytes} bytes, Trigger: {Trigger})",
                user.Username, stored.Width, stored.Height, stored.FileSizeBytes, stored.TriggerType);
            RaiseCaptureReceived(stored);
            return "stored";
        }
        finally
        {
            if (quota.Allowed)
            {
                lock (_captureReservations)
                {
                    var reserved = _captureReservations[user.Id];
                    reserved = (reserved.Files - 1, reserved.Bytes - quota.MaxBytes);
                    if (reserved.Files <= 0)
                        _captureReservations.Remove(user.Id);
                    else
                        _captureReservations[user.Id] = reserved;
                }
            }
        }
    }

    /// <summary>
    /// A session-end request of the agent (design D8, D14): first the Lock Deferral, then the hold or the Session End
    /// Guard, both untrusted and only for the session of the launch record of a Restricted User. The caller must not
    /// tell the agent whether the deferral was granted.
    /// </summary>
    public void SessionEnding(AgentLaunchRecord record, AgentSessionEnding message)
    {
        var user = UserRepository.GetBySid(record.UserSid);
        if (user is null || !user.IsRestricted)
        {
            _logger.Information("Session end request of the agent in session {SessionId} ignored: no Restricted User", record.SessionId);
            return;
        }
        // The agent resumed the Suspended Apps before it sent this request. Unwatch them before the deferral and the
        // gate of the enforcer, so that a tick in between does not count a Child Resume (lock-after-repeated-resume D12).
        _categoryEnforcer.UnwatchSessionForEnd(record.SessionId);
        _sessionLocker.OnSessionEnding(record.SessionId, user.Id, user.Username, trusted: false, $"agent {message.Reason}");
        var result = _categoryEnforcer.ResumeSessionForEnd(record.SessionId, user, trusted: false, $"agent {message.Reason}");
        _logger.Information("Session end request of the agent in session {SessionId} ({Username}): {Result}",
            record.SessionId, user.Username, result);
    }
}

/// <summary>A capture command of the parent for one user: one capture, or the watch mode.</summary>
public sealed class CaptureCommandState
{
    public bool CaptureRequested { get; set; }
    public bool WatchMode { get; set; }
    public DateTime WatchExpiresAt { get; set; }
    public string Trigger { get; set; } = "MANUAL";
}
