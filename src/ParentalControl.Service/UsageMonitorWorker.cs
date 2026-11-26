using System.Collections.Concurrent;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;

namespace ParentalControl.Service;

public sealed class UsageMonitorWorker : BackgroundService
{
    private readonly SessionTracker _sessionTracker;
    private readonly CategoryEnforcer _categoryEnforcer;
    private readonly BreakEnforcer _breakEnforcer;
    private readonly Serilog.ILogger _logger;
    private readonly ServiceStartup _startup;
    private readonly IHostApplicationLifetime _lifetime;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private DateOnly _lastCleanupDate = DateOnly.MinValue;
    private readonly ConcurrentDictionary<int, HashSet<int>> _sentAlerts = new();
    private long _lastMonotonicTick = Environment.TickCount64;
    private DateTime _lastWallTime = DateTime.UtcNow;
    public UsageMonitorWorker(SessionTracker sessionTracker, CategoryEnforcer categoryEnforcer, BreakEnforcer breakEnforcer, Serilog.ILogger logger,
        ServiceStartup startup, IHostApplicationLifetime lifetime)
    {
        _startup = startup;
        _lifetime = lifetime;
        _sessionTracker = sessionTracker;
        _categoryEnforcer = categoryEnforcer;
        _breakEnforcer = breakEnforcer;
        _logger = logger;
    }

    /// <summary>On a service stop, resume every process that the Category enforcer suspended.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _categoryEnforcer.ResumeAll();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while resuming suspended processes on stop");
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // No limit work before the data folder, the database and the log are safe (require-admin-sign-in, CB1).
            await _startup.WaitReadyAsync(stoppingToken);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // S3: the host ignores a failed worker, but without this worker no limit is enforced. The Service Control
            // Manager starts the service again.
            _logger.Fatal(ex, "The usage monitor worker failed. The service restarts.");
            DataFolderStartup.WriteEvent(System.Diagnostics.EventLogEntryType.Error, 1014,
                $"The usage monitor of Parental Control failed ({ex.GetType().Name}: {ex.Message}). The service restarts.");
            DataFolderLockdown.RestartService(_lifetime);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Usage monitor worker started");

        try
        {
            ProcessTick();
            RunCleanupIfNeeded();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during initial usage monitor tick");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TickInterval, stoppingToken);

            if (!_sessionTracker.IsAwake)
                continue;

            try
            {
                ProcessTick();
                RunCleanupIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during usage monitor tick");
            }
        }
    }

    private void ProcessTick()
    {
        // 1. Synchronize active sessions from Windows Terminal Services
        _sessionTracker.SynchronizeSessions();

        // 2. Monotonic Clock Watchdog
        var currentMonotonic = Environment.TickCount64;
        var currentWall = DateTime.UtcNow;
        var monotonicDeltaSec = (currentMonotonic - _lastMonotonicTick) / 1000.0;
        var wallDeltaSec = (currentWall - _lastWallTime).TotalSeconds;

        _lastMonotonicTick = currentMonotonic;
        _lastWallTime = currentWall;

        if (monotonicDeltaSec > 0 && Math.Abs(wallDeltaSec - monotonicDeltaSec) > 60)
        {
            _logger.Warning("Clock tampering detected: wall clock drifted {Drift:F1}s vs monotonic uptime",
                wallDeltaSec - monotonicDeltaSec);
            EventRepository.LogEvent("SYSTEM", EventType.CLOCK_TAMPER,
                $"Wall clock drifted {wallDeltaSec - monotonicDeltaSec:F0}s (wall: {wallDeltaSec:F0}s, mono: {monotonicDeltaSec:F0}s)");

            _ = Task.Run(async () =>
            {
                try
                {
                    var botToken = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
                    var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
                    if (!string.IsNullOrWhiteSpace(botToken) && !string.IsNullOrWhiteSpace(chatId))
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        var payload = new
                        {
                            chat_id = chatId,
                            text = $"⚠️ *PERINGATAN KEAMANAN: Jam Sistem Dimanipulasi*\n"
                                 + $"Komputer: `{Environment.MachineName}`\n"
                                 + $"Pergeseran Waktu: {wallDeltaSec - monotonicDeltaSec:F0} detik.\n"
                                 + $"Pengawasan kuota dan jam malam tetap aktif.",
                            parse_mode = "Markdown"
                        };
                        var json = System.Text.Json.JsonSerializer.Serialize(payload);
                        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        await http.PostAsync($"https://api.telegram.org/bot{botToken}/sendMessage", content);
                    }
                }
                catch { }
            });
        }

        _sessionTracker.TickAllSessions();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var dayOfWeek = DateTime.Now.DayOfWeek;
        var alertSetting = SettingsRepository.Get(SettingsRepository.KeyAlertIntervals, "15,5,1");
        var thresholds = alertSetting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .OrderByDescending(v => v)
            .ToList();

        // Clean stale sessions from _sentAlerts
        var activeIds = _sessionTracker.ActiveSessions.Keys.ToHashSet();
        foreach (var key in _sentAlerts.Keys)
        {
            if (!activeIds.Contains(key))
            {
                _sentAlerts.TryRemove(key, out _);
            }
        }

        foreach (var (sessionId, session) in _sessionTracker.ActiveSessions)
        {
            if (session.IsLocked) continue;

            var user = UserRepository.GetBySid(session.UserSid);
            if (user is null || !user.IsRestricted) continue;
            _sessionTracker.EnsureAgentRunning(sessionId);

            var limit = ScheduleRepository.GetEffectiveLimit(user.Id, dayOfWeek, today);
            if (limit is null) continue;

            var usage = UsageRepository.GetUsage(user.Id, today);
            var totalAllowed = limit.DailyMinutes + (usage?.BonusMinutes ?? 0);
            var used = usage?.MinutesUsed ?? 0;
            var remainingDaily = Math.Max(0, totalAllowed - used);

            var remainingSchedule = int.MaxValue;
            if (now < limit.ScheduleEnd)
            {
                remainingSchedule = (int)(limit.ScheduleEnd.ToTimeSpan() - now.ToTimeSpan()).TotalMinutes;
            }
            else
            {
                remainingSchedule = 0;
            }

            // Check hard expirations first
            if (used >= totalAllowed)
            {
                _logger.Information("Daily limit reached for {Username} ({MinutesUsed}/{DailyMinutes} min, bonus: {Bonus} min)",
                    session.Username, used, limit.DailyMinutes, usage?.BonusMinutes ?? 0);
                EventRepository.LogEvent(session.UserSid, EventType.LIMIT_REACHED,
                    $"Used {used} of {totalAllowed} minutes (base: {limit.DailyMinutes}, bonus: {usage?.BonusMinutes ?? 0})");
                NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgLimitReached), isWarning: true, timeoutSeconds: 5, wait: true);
                SessionManager.LockSession(sessionId);
                _sessionTracker.OnSessionLock(sessionId, "Daily limit reached - workstation locked");
                _sentAlerts.TryRemove(sessionId, out _);
                continue;
            }

            if (now < limit.ScheduleStart || now >= limit.ScheduleEnd)
            {
                _logger.Information("Outside allowed schedule for {Username} (allowed {Start}-{End})",
                    session.Username, limit.ScheduleStart, limit.ScheduleEnd);
                NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgCurfewReached), isWarning: true, timeoutSeconds: 5, wait: true);
                SessionManager.LockSession(sessionId);
                _sessionTracker.OnSessionLock(sessionId, "Outside allowed schedule - workstation locked");
                _sentAlerts.TryRemove(sessionId, out _);
                continue;
            }

            // Check thresholds for notifications
            var sessionSent = _sentAlerts.GetOrAdd(sessionId, _ => new HashSet<int>());
            var effectiveRemaining = Math.Min(remainingDaily, remainingSchedule);

            foreach (var threshold in thresholds)
            {
                if (effectiveRemaining <= threshold && !sessionSent.Contains(threshold))
                {
                    if (remainingDaily <= remainingSchedule)
                    {
                        NotificationManager.SendWarning(sessionId, remainingDaily);
                    }
                    else
                    {
                        NotificationManager.SendCurfewWarning(sessionId, remainingSchedule, limit.ScheduleEnd);
                    }

                    sessionSent.Add(threshold);
                    _logger.Information("Dispatched {Threshold}m warning to {Username} on session {SessionId}",
                        threshold, session.Username, sessionId);
                    break; // alert once per tick
                }
            }
        }

        // Breaks: after the checks of the Allowance Time and the Schedule Window, so that they come first.
        _breakEnforcer.Tick(_sessionTracker);

        // 3. App Categories: warnings, suspension and resume. An error here must not stop the session checks.
        try
        {
            _categoryEnforcer.Tick();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during the category check");
        }
    }

    private void RunCleanupIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today <= _lastCleanupDate) return;

        _lastCleanupDate = today;

        var eventsCutoff = DateTime.Now.AddDays(-DatabaseManager.RetentionDays);
        var usageCutoff = today.AddDays(-DatabaseManager.RetentionDays);

        var eventsDeleted = EventRepository.DeleteOlderThan(eventsCutoff);
        var usageDeleted = UsageRepository.DeleteOlderThan(usageCutoff);
        var appUsageDeleted = AppUsageRepository.DeleteOlderThan(usageCutoff);
        var categoryUsageDeleted = CategoryUsageRepository.DeleteOlderThan(usageCutoff);
        BlockedStartRepository.DeleteOlderThan(usageCutoff);
        var capturesDeleted = ScreenCaptureRepository.PruneOldCaptures(DatabaseManager.CapturesRetentionDays, DatabaseManager.MaxCapturesStorageBytes);
        var exceptionsDeleted = ScheduleExceptionRepository.DeleteOlderThan(today);
        if (eventsDeleted > 0 || usageDeleted > 0 || appUsageDeleted > 0 || categoryUsageDeleted > 0)
        {
            _logger.Information("Database cleanup: deleted {EventsDeleted} events, {UsageDeleted} usage records, {AppUsageDeleted} app records, {CategoryUsageDeleted} category usage records, {CapturesDeleted} captures",
                eventsDeleted, usageDeleted, appUsageDeleted, categoryUsageDeleted, capturesDeleted);
        }
    }
}
