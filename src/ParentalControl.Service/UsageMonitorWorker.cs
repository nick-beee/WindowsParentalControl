using System.Collections.Concurrent;
using System.Diagnostics;
using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Service;

public sealed class UsageMonitorWorker : BackgroundService
{
    /// <summary>The setting that the probe write of each tick stores (storage-failure, design D8).</summary>
    public const string KeyStorageProbe = "storage_probe";

    /// <summary>Windows event IDs of the Storage Lockdown: entry and exit.</summary>
    public const int StorageLockdownEntryEventId = 1015;
    public const int StorageLockdownExitEventId = 1016;

    private static readonly TimeSpan LockLoopInterval = TimeSpan.FromSeconds(2);

    private readonly SessionTracker _sessionTracker;
    private readonly CategoryEnforcer _categoryEnforcer;
    private readonly BreakEnforcer _breakEnforcer;
    private readonly ControlTimeService _controlTime;
    private readonly ParentAlertOutbox _alerts;
    private readonly Serilog.ILogger _logger;
    private readonly ServiceStartup _startup;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly StorageHealth _health = StorageHealth.Shared;
    private readonly UsageLedger _ledger = UsageLedger.Shared;
    private readonly StorageLockdownState _storageLockdown = new();
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private DateOnly _lastCleanupDate = DateOnly.MinValue;
    private readonly ConcurrentDictionary<int, HashSet<int>> _sentAlerts = new();
    private long _lastMonotonicTick = Environment.TickCount64;
    private DateTime _lastWallTime = DateTime.UtcNow;
    private CancellationToken _stopping;
    private CancellationTokenSource? _lockLoop;

    public UsageMonitorWorker(SessionTracker sessionTracker, CategoryEnforcer categoryEnforcer, BreakEnforcer breakEnforcer,
        ControlTimeService controlTime, ParentAlertOutbox alerts, Serilog.ILogger logger,
        ServiceStartup startup, IHostApplicationLifetime lifetime)
    {
        _startup = startup;
        _lifetime = lifetime;
        _sessionTracker = sessionTracker;
        _categoryEnforcer = categoryEnforcer;
        _breakEnforcer = breakEnforcer;
        _controlTime = controlTime;
        _alerts = alerts;
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
            // Manager starts the service again. A write failure never comes here: it gives the Storage Lockdown.
            _logger.Fatal(ex, "The usage monitor worker failed. The service restarts.");
            DataFolderStartup.WriteEvent(EventLogEntryType.Error, 1014,
                $"The usage monitor of Parental Control failed ({ex.GetType().Name}: {ex.Message}). The service restarts.");
            DataFolderLockdown.RestartService(_lifetime);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _logger.Information("Usage monitor worker started (Control Time Zone {Zone})", ControlTime.Zone.Id);

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

    /// <summary>
    /// One tick in the order of design D7: the clock, the sessions, the count of time into the ledger, the checks and
    /// locks, the Breaks, the categories, and last the report to the Storage Lockdown. A failed write never stops a
    /// later step: each write runs through <see cref="StorageHealth.TryWrite"/> or the ledger.
    /// </summary>
    private void ProcessTick()
    {
        var readFailed = false;
        try
        {
            // 0. The Control Time Zone and the system time zone (also in the Storage Lockdown). The retry of the parent alerts.
            _controlTime.Refresh();
            _controlTime.CheckSystemZone();
            _ = _alerts.FlushAsync();

            // 1. Synchronize active sessions from Windows Terminal Services, and the clock watchdog.
            _sessionTracker.SynchronizeSessions();
            RunClockWatchdog();

            // 2. Count the elapsed time of each session. A failed write keeps the minutes in the Pending Usage.
            try
            {
                _sessionTracker.TickAllSessions();
            }
            catch (Exception ex)
            {
                readFailed = true;
                _logger.Error(ex, "Error while counting the session time");
            }

            // 3. The checks of the Allowance Time and the Schedule Window, with the stored time plus the Pending Usage.
            if (!CheckLimits())
                readFailed = true;

            // Breaks: after the checks of the Allowance Time and the Schedule Window, so that they come first.
            try
            {
                _breakEnforcer.Tick(_sessionTracker);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during the break check");
            }

            // 4. App Categories: warnings, suspension and resume. An error here must not stop the session checks.
            try
            {
                _categoryEnforcer.Tick();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during the category check");
            }
        }
        finally
        {
            // 5. The result of the tick for the Storage Lockdown.
            ReportStorage(readFailed);
        }
    }

    /// <summary>The monotonic clock watchdog: the UTC wall clock against <c>Environment.TickCount64</c>.</summary>
    private void RunClockWatchdog()
    {
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
            _health.TryWrite(() => EventRepository.LogEvent("SYSTEM", EventType.CLOCK_TAMPER,
                $"Wall clock drifted {wallDeltaSec - monotonicDeltaSec:F0}s (wall: {wallDeltaSec:F0}s, mono: {monotonicDeltaSec:F0}s)"),
                "clock tamper event", _logger);

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
    }

    /// <summary>
    /// The checks of the Allowance Time and the Schedule Window of each unlocked session of a Restricted User. In each
    /// lock path, the lock comes before the writes. Returns false if a read for a check failed.
    /// </summary>
    private bool CheckLimits()
    {
        var allRead = true;
        var moment = ControlTime.Current.Read();
        var today = moment.Today;
        var now = moment.TimeOfDay;

        List<int> thresholds;
        try
        {
            var alertSetting = SettingsRepository.Get(SettingsRepository.KeyAlertIntervals, "15,5,1");
            thresholds = alertSetting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .Where(v => v > 0)
                .OrderByDescending(v => v)
                .ToList();
        }
        catch (Exception ex)
        {
            allRead = false;
            thresholds = [15, 5, 1];
            _logger.Error(ex, "Could not read the alert intervals; using 15, 5, 1");
        }

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
            try
            {
                CheckSession(sessionId, session, today, now, moment.DayOfWeek, thresholds);
            }
            catch (Exception ex)
            {
                allRead = false;
                _logger.Error(ex, "Error during the limit check of {Username} (session {SessionId})", session.Username, sessionId);
            }
        }
        return allRead;
    }

    private void CheckSession(int sessionId, ActiveSession session, DateOnly today, TimeOnly now, DayOfWeek dayOfWeek, List<int> thresholds)
    {
        var user = UserRepository.GetBySid(session.UserSid);
        if (user is null || !user.IsRestricted) return;
        _sessionTracker.EnsureAgentRunning(sessionId);

        var limit = ScheduleRepository.GetEffectiveLimit(user.Id, dayOfWeek, today);
        if (limit is null) return;

        var usage = UsageRepository.GetUsage(user.Id, today);
        var totalAllowed = limit.DailyMinutes + (usage?.BonusMinutes ?? 0);
        var used = _ledger.AllowanceUsed(user.Id, today, usage);
        var remainingDaily = Math.Max(0, totalAllowed - used);

        var remainingSchedule = now < limit.ScheduleEnd
            ? (int)(limit.ScheduleEnd.ToTimeSpan() - now.ToTimeSpan()).TotalMinutes
            : 0;

        // Check hard expirations first
        if (used >= totalAllowed)
        {
            _logger.Information("Daily limit reached for {Username} ({MinutesUsed}/{DailyMinutes} min, bonus: {Bonus} min)",
                session.Username, used, limit.DailyMinutes, usage?.BonusMinutes ?? 0);
            NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgLimitReached), isWarning: true, timeoutSeconds: 5, wait: true);
            SessionManager.LockSession(sessionId);
            _sessionTracker.OnSessionLock(sessionId, "Daily limit reached - workstation locked");
            _sentAlerts.TryRemove(sessionId, out _);
            _health.TryWrite(() => EventRepository.LogEvent(session.UserSid, EventType.LIMIT_REACHED,
                $"Used {used} of {totalAllowed} minutes (base: {limit.DailyMinutes}, bonus: {usage?.BonusMinutes ?? 0})"),
                "limit reached event", _logger);
            return;
        }

        if (now < limit.ScheduleStart || now >= limit.ScheduleEnd)
        {
            _logger.Information("Outside allowed schedule for {Username} (allowed {Start}-{End})",
                session.Username, limit.ScheduleStart, limit.ScheduleEnd);
            NotificationManager.SendMessage(sessionId, "Parental Control", SettingsRepository.GetMessage(SettingsRepository.KeyMsgCurfewReached), isWarning: true, timeoutSeconds: 5, wait: true);
            SessionManager.LockSession(sessionId);
            _sessionTracker.OnSessionLock(sessionId, "Outside allowed schedule - workstation locked");
            _sentAlerts.TryRemove(sessionId, out _);
            return;
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

    /// <summary>
    /// The probe write, the store of the Pending Usage and the failures of the tick go to the Storage Lockdown state
    /// (design D8). It never restarts the service for a write failure.
    /// </summary>
    private void ReportStorage(bool readFailed)
    {
        try
        {
            var probeOk = _health.TryWrite(() => SettingsRepository.Set(KeyStorageProbe, DateTime.UtcNow.ToString("O")), "storage probe", _logger);
            var storeOk = _ledger.Store();
            var failures = _health.TakeFailures();
            // The retry of this tick decides the exit; the failures since the last tick count only for the entry.
            var retrySucceeded = !readFailed && probeOk && storeOk;
            var tickFailed = !retrySucceeded || failures > 0;

            long? free;
            try
            {
                free = DiskSpace.FreeBytes(DatabaseManager.DataDirectory);
            }
            catch
            {
                free = null;
            }

            var today = ControlTime.Current.Today;
            var outcome = _storageLockdown.ReportTick(tickFailed, retrySucceeded, _ledger.Pending.IsEmpty, free, today);

            if (tickFailed && !outcome.Entered && !outcome.Left)
                _logger.Warning("Database write failed in this tick ({Failures} failures, {InRow} failed ticks in a row, Storage Lockdown: {InLockdown}, free: {Free} bytes)",
                    failures, _storageLockdown.FailedTicksInRow, _storageLockdown.InLockdown, free);

            if (outcome.Entered)
                EnterStorageLockdown(free, outcome.Alert);
            else if (outcome.Left)
                LeaveStorageLockdown(free);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during the storage check");
        }
    }

    private void EnterStorageLockdown(long? free, bool alert)
    {
        var text = $"Parental Control cannot write its database ({FreeText(free)} free on the volume of the data folder). " +
                   "It locks each session of a user who is not an administrator until an administrator frees space.";
        _logger.Error("Storage Lockdown entered: {Text}", text);
        DataFolderStartup.WriteEvent(EventLogEntryType.Error, StorageLockdownEntryEventId, text);
        _health.TryWrite(() => EventRepository.LogEvent("SYSTEM", EventType.STORAGE_LOCKDOWN, "Entered: " + text), "storage lockdown event", _logger);
        StartLockLoop();
        if (alert)
            _alerts.Post($"Parental Control on {Environment.MachineName}: the disk is full or the database cannot be written ({FreeText(free)} free). " +
                         "Each session of a child is locked, and the programs stay open. Sign in as an administrator and free space on the disk.");
    }

    private void LeaveStorageLockdown(long? free)
    {
        StopLockLoop();
        var text = $"Parental Control can write its database again ({FreeText(free)} free). The Storage Lockdown ended.";
        _logger.Information(text);
        DataFolderStartup.WriteEvent(EventLogEntryType.Information, StorageLockdownExitEventId, text);
        _health.TryWrite(() => EventRepository.LogEvent("SYSTEM", EventType.STORAGE_LOCKDOWN, "Ended: " + text), "storage lockdown event", _logger);
        // The sessions that the lockdown disconnected are locked; the unlock check of SessionTracker applies to them.
    }

    private static string FreeText(long? free) => free is { } b ? $"{b / (1024 * 1024)} MB" : "unknown space";

    /// <summary>
    /// The lock loop of the Storage Lockdown: the Enforced Workstation Lock on each active session of a user who is not
    /// an administrator, every 2 seconds. It never closes or kills a program.
    /// </summary>
    private void StartLockLoop()
    {
        if (_lockLoop is not null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        _lockLoop = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var locked = DataFolderLockdown.LockSessions();
                    if (locked.Count > 0)
                        _logger.Information("Storage Lockdown: locked session(s) {Sessions}", string.Join(", ", locked));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in the lock loop of the Storage Lockdown");
                }

                try
                {
                    await Task.Delay(LockLoopInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private void StopLockLoop()
    {
        var cts = Interlocked.Exchange(ref _lockLoop, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    private void RunCleanupIfNeeded()
    {
        var today = ControlTime.Current.Today;
        if (today <= _lastCleanupDate) return;

        _lastCleanupDate = today;

        try
        {
            // Event rows keep the system time (display only). The day keys use the Control Time Zone.
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
        catch (Exception ex)
        {
            _health.ReportFailure();
            _logger.Error(ex, "Database cleanup failed");
        }
    }
}
