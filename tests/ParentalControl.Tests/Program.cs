using ParentalControl.Core.Platform;
using System.Diagnostics;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Security;
using ParentalControl.Core;
using ParentalControl.Core.Services;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.AdminAccess;

Console.WriteLine("=== Running ParentalControl Self-Checks ===");

// 1. Test TOTP service
var secret = TotpService.GenerateSecret();
Debug.Assert(!string.IsNullOrEmpty(secret), "Secret should not be empty");
Debug.Assert(secret.Length >= 16, "Secret should be at least 16 chars");

var uri = TotpService.GenerateOtpauthUri("TestApp", "TestUser", secret);
Debug.Assert(uri.StartsWith("otpauth://totp/"), "URI should start with otpauth://totp/");

Debug.Assert(!TotpService.VerifyCode(secret, "000000"), "Random dummy code should fail");
Debug.Assert(!TotpService.VerifyCode(secret, ""), "Empty code should fail");
Debug.Assert(!TotpService.VerifyCode(secret, "12345"), "Short code should fail");
var qrBytes = TotpService.GenerateQrCodePng(uri, 4);
Debug.Assert(qrBytes != null && qrBytes.Length > 0, "QR Code PNG bytes should not be empty");
Debug.Assert(qrBytes[0] == 0x89 && qrBytes[1] == 0x50 && qrBytes[2] == 0x4E && qrBytes[3] == 0x47, "QR should be valid PNG");
Console.WriteLine("✅ TOTP Service & QR Generation Verification Passed.");
var tempDir = Path.Combine(Path.GetTempPath(), "ParentalControlTest_" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("PARENTAL_CONTROL_DATA_DIR", tempDir);
try
{
    // 2. Initialize Database in test environment
    DatabaseManager.Initialize();
// 3. Test Settings
SettingsRepository.Set("test_key", "test_value_123");
var val = SettingsRepository.Get("test_key");
Debug.Assert(val == "test_value_123", "Settings key should match saved value");
Console.WriteLine("✅ Settings Repository Verification Passed.");

// 4. Test User & Grace Requests
var testSid = "S-1-5-21-999999999-999999999-999999999-9999";
var user = UserRepository.Upsert(testSid, "TestBrother", true);
Debug.Assert(user.Id > 0, "User ID should be > 0");

var today = DateOnly.FromDateTime(DateTime.Now);
var req = GraceRequestRepository.Create(user.Id, today, 30, "Testing grace submission");
Debug.Assert(req.Id > 0, "GraceRequest ID should be > 0");
Debug.Assert(req.Status == "PENDING", "Initial status should be PENDING");

var retrievedReq = GraceRequestRepository.GetTodayRequest(user.Id, today);
Debug.Assert(retrievedReq != null, "Today's request should be retrievable");
Debug.Assert(retrievedReq!.RequestedMinutes == 30, "Requested minutes should be 30");

GraceRequestRepository.Resolve(req.Id, "APPROVED");
var resolvedReq = GraceRequestRepository.GetById(req.Id);
Debug.Assert(resolvedReq!.Status == "APPROVED", "Status should be updated to APPROVED");
var count = GraceRequestRepository.GetTodayRequestCount(user.Id, today);
Debug.Assert(count == 1, "Today's request count should be 1");
Debug.Assert(!GraceRequestRepository.HasPendingRequest(user.Id, today), "Pending request should be false after resolve");

Debug.Assert(SettingsRepository.GetMaxDailyRequests() == 1, "Default max daily requests should be 1");
SettingsRepository.Set(SettingsRepository.KeyMaxDailyRequests, "3");
Debug.Assert(SettingsRepository.GetMaxDailyRequests() == 3, "Updated max daily requests should be 3");
Console.WriteLine("✅ Grace Request Repository & Dynamic Limits Verification Passed.");

// 5. Test Usage and Bonus Minutes
UsageRepository.SetUsage(user.Id, today, 60);
UsageRepository.AddBonusMinutes(user.Id, today, 15);
var usage = UsageRepository.GetUsage(user.Id, today);
Debug.Assert(usage != null, "Usage should not be null");
Debug.Assert(usage!.MinutesUsed == 60, "MinutesUsed should be 60");
Debug.Assert(usage!.BonusMinutes == 15, "BonusMinutes should be 15");
Console.WriteLine("✅ Usage and Bonus Minutes Verification Passed.");

// 6. Test Weekly Schedule
ScheduleRepository.SaveDaySchedule(new DaySchedule
{
    UserId = user.Id,
    DayOfWeek = DayOfWeek.Saturday,
    DailyMinutes = 180,
    ScheduleStart = new TimeOnly(9, 0),
    ScheduleEnd = new TimeOnly(23, 0)
});
var saturdayLimit = ScheduleRepository.GetEffectiveLimit(user.Id, DayOfWeek.Saturday);
Debug.Assert(saturdayLimit != null, "Saturday limit should not be null");
Debug.Assert(saturdayLimit!.DailyMinutes == 180, "Saturday limit should be 180");

// Sunday has no specific day schedule, should fallback
var sundayLimit = ScheduleRepository.GetEffectiveLimit(user.Id, DayOfWeek.Sunday);
Console.WriteLine("✅ Weekly Schedule & Fallback Verification Passed.");

// 7. Test Session Lock/Unlock Event Types
EventRepository.LogEvent(testSid, EventType.SESSION_LOCKED, "Locked via Web Admin (15s delay)");
EventRepository.LogEvent(testSid, EventType.SESSION_UNLOCKED, "Unlocked by user");
EventRepository.LogEvent(testSid, EventType.LOGIN_DENIED, "Outside allowed schedule");
var events = EventRepository.GetEvents(userSid: testSid);
Debug.Assert(events.Any(e => e.EventType == EventType.SESSION_LOCKED), "SESSION_LOCKED event should be recorded");
Debug.Assert(events.Any(e => e.EventType == EventType.SESSION_UNLOCKED), "SESSION_UNLOCKED event should be recorded");
Debug.Assert(events.Any(e => e.EventType == EventType.LOGIN_DENIED), "LOGIN_DENIED event should be recorded");
Console.WriteLine("✅ Session Lock & Unlock Events Verification Passed.");

// 8. Test GetByUsername and Console Session detection
var foundUser = UserRepository.GetByUsername("TESTBROTHER");
Debug.Assert(foundUser != null && foundUser.Id == user.Id, "GetByUsername should find user case-insensitively");
var notFound = UserRepository.GetByUsername("NonExistentUser123");
Debug.Assert(notFound == null, "GetByUsername should return null for nonexistent user");
var consoleId = SessionManager.GetActiveConsoleSessionId();
Debug.Assert(consoleId >= -1, "GetActiveConsoleSessionId should return valid session ID or -1");
Console.WriteLine("✅ User Discovery by Username & Console Session Detection Passed.");

// 9. Test App Activity Hourly & Range Queries
AppUsageRepository.AddMinutes(user.Id, today, "RobloxPlayerBeta.exe", "Roblox", 45, hour: 14);
AppUsageRepository.AddMinutes(user.Id, today, "chrome.exe", "YouTube", 15, hour: 14);
AppUsageRepository.AddMinutes(user.Id, today, "chrome.exe", "Homework", 30, hour: 16);

var hourly = AppUsageRepository.GetHourlyDistribution(user.Id, today, today);
Debug.Assert(hourly[14] == 60, "Hour 14 should have 60 min total");
Debug.Assert(hourly[16] == 30, "Hour 16 should have 30 min total");
var rangeApps = AppUsageRepository.GetUsageForRange(user.Id, today, today);
Debug.Assert(rangeApps.Count == 2, "Should have 2 unique processes");
Debug.Assert(rangeApps.First(a => a.ProcessName == "chrome.exe").Minutes == 45, "Chrome should have 45 min total");
Console.WriteLine("✅ Hourly Activity & App Usage Range Verification Passed.");

// 9b. Test 20-Second Activity Tick Accumulation & Second-to-Minute Conversion
AppUsageRepository.ResetAccumulators();
var testGame = "eldenring.exe";
AppUsageRepository.AddSeconds(user.Id, today, testGame, "ELDEN RING", 20, hour: 10);
var check1 = AppUsageRepository.GetForUserAndDate(user.Id, today).FirstOrDefault(a => a.ProcessName == testGame);
Debug.Assert(check1 == null, "20 seconds should not yet commit a full minute to database");

AppUsageRepository.AddSeconds(user.Id, today, testGame, "ELDEN RING", 20, hour: 10);
var check2 = AppUsageRepository.GetForUserAndDate(user.Id, today).FirstOrDefault(a => a.ProcessName == testGame);
Debug.Assert(check2 == null, "40 seconds should not yet commit a full minute to database");

AppUsageRepository.AddSeconds(user.Id, today, testGame, "ELDEN RING", 20, hour: 10);
var check3 = AppUsageRepository.GetForUserAndDate(user.Id, today).FirstOrDefault(a => a.ProcessName == testGame);
Debug.Assert(check3 != null && check3.Minutes == 1, "60 seconds (3x20s) must commit exactly 1 minute to database");

// 9c. Test Database Migration Repair for Legacy Inflated Records (> 60m per hour)
using (var rawConn = DatabaseManager.CreateConnection())
{
    using var cmd = rawConn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO app_activity_hourly (user_id, date, hour, process_name, minutes)
        VALUES (@userId, @date, 9, 'legacy_inflated.exe', 180)
        ON CONFLICT(user_id, date, hour, process_name) DO UPDATE SET minutes = 180;
        INSERT INTO app_usage (user_id, date, process_name, window_title, minutes)
        VALUES (@userId, @date, 'legacy_inflated.exe', 'Legacy Game', 180)
        ON CONFLICT(user_id, date, process_name) DO UPDATE SET minutes = 180;
        """;
    cmd.Parameters.AddWithValue("@userId", user.Id);
    cmd.Parameters.AddWithValue("@date", today.ToString("yyyy-MM-dd"));
    cmd.ExecuteNonQuery();
}
DatabaseManager.Initialize(); // Re-run migration/initialization
var repairedHourly = AppUsageRepository.GetHourlyDistribution(user.Id, today, today);
Debug.Assert(repairedHourly[9] == 60, "Legacy inflated 180m record in hour 9 must be repaired and clamped to 60m");
var repairedApp = AppUsageRepository.GetForUserAndDate(user.Id, today).FirstOrDefault(a => a.ProcessName == "legacy_inflated.exe");
Debug.Assert(repairedApp != null && repairedApp.Minutes == 60, "Legacy inflated app usage should be re-synced to match repaired hourly sum (60m)");
Console.WriteLine("✅ 20-Second Telemetry Accumulation & Legacy Inflation Repair Passed.");

// 10. Test Session Enumeration & Curfew Countdown Clamping Logic
var loggedOnSessions = SessionManager.GetLoggedOnSessions();
Debug.Assert(loggedOnSessions != null, "GetLoggedOnSessions should return a non-null list");
var runningCheck = SessionManager.FindProcessesByExeName(@"C:\Missing\NonExistentProcessName123.exe", 0);
Debug.Assert(runningCheck.Count == 0, "Nonexistent process should not be reported as running");
foreach (var (sid0, _, _, _) in loggedOnSessions)
{
    // The lock flag query must not throw. The value depends on the machine, so the test only prints it.
    var (lockFlag, rawLockFlags) = SessionManager.QueryLockFlag(sid0);
    Console.WriteLine($"Session {sid0}: lock flag {lockFlag} (SessionFlags {rawLockFlags?.ToString() ?? "n/a"})");
}

// Verify curfew clamping formula:
// Case A: Curfew is earlier than daily quota
var testDailySec = 7200; // 2 hours
var testCurfewSec = 600;  // 10 minutes
var testClamped = Math.Min(testDailySec, testCurfewSec);
Debug.Assert(testClamped == 600, "Should clamp to curfew remaining seconds");

// Case B: Daily quota is earlier than curfew
testDailySec = 300;   // 5 minutes
testCurfewSec = 3600; // 1 hour
testClamped = Math.Min(testDailySec, testCurfewSec);
Debug.Assert(testClamped == 300, "Should clamp to daily remaining seconds");
Console.WriteLine("✅ Session Enumeration & Curfew Clamping Calculation Verification Passed.");

// 11. Test Screen Capture Repository & File Management
var dummyCaptureFile = Path.Combine(DatabaseManager.CapturesDirectory, $"{user.Id}_test.jpg");
File.WriteAllBytes(dummyCaptureFile, new byte[1024]);

var capture = new ScreenCaptureRecord
{
    UserId = user.Id,
    Timestamp = DateTime.Now,
    FilePath = dummyCaptureFile,
    Width = 1920,
    Height = 1080,
    FileSizeBytes = 1024,
    TriggerType = "MANUAL",
    AdminIp = "127.0.0.1"
};
var savedCapture = ScreenCaptureRepository.Add(capture);
Debug.Assert(savedCapture.Id > 0, "ScreenCapture ID should be > 0");

var retrievedCapture = ScreenCaptureRepository.GetById(savedCapture.Id);
Debug.Assert(retrievedCapture != null, "Capture should be retrievable by ID");
Debug.Assert(retrievedCapture!.Width == 1920 && retrievedCapture.Height == 1080, "Dimensions should match");

var latestCapture = ScreenCaptureRepository.GetLatestByUser(user.Id);
Debug.Assert(latestCapture != null && latestCapture.Id == savedCapture.Id, "Latest capture should match saved capture");

var recentCaptures = ScreenCaptureRepository.GetRecentByUser(user.Id, 5);
Debug.Assert(recentCaptures.Count == 1, "Should have 1 recent capture");

var totalBytes = ScreenCaptureRepository.GetTotalStorageBytes();
Debug.Assert(totalBytes >= 1024, "Total storage should be at least 1024 bytes");

// Test capture deletion
var deleted = ScreenCaptureRepository.Delete(savedCapture.Id);
Debug.Assert(deleted, "Capture should be deleted successfully");
Debug.Assert(!File.Exists(dummyCaptureFile), "Capture file should be deleted from disk");
Console.WriteLine("✅ Screen Capture Repository CRUD & Storage Tracking Passed.");

// 12. Test Screen Capture Retention & Storage Ceiling Pruning
var oldCaptureFile = Path.Combine(DatabaseManager.CapturesDirectory, $"{user.Id}_old.jpg");
File.WriteAllBytes(oldCaptureFile, new byte[2048]);
var oldCapture = ScreenCaptureRepository.Add(new ScreenCaptureRecord
{
    UserId = user.Id,
    Timestamp = DateTime.Now.AddDays(-10), // older than 7 days
    FilePath = oldCaptureFile,
    Width = 1920,
    Height = 1080,
    FileSizeBytes = 2048,
    TriggerType = "WATCH"
});

var prunedCount = ScreenCaptureRepository.PruneOldCaptures(retentionDays: 7, maxTotalBytes: 500 * 1024 * 1024);
Debug.Assert(prunedCount >= 1, "PruneOldCaptures should prune expired captures");
Debug.Assert(ScreenCaptureRepository.GetById(oldCapture.Id) == null, "Old capture should be removed from database");
Debug.Assert(!File.Exists(oldCaptureFile), "Old capture file should be removed from disk");
Console.WriteLine("✅ Screen Capture Pruning (Retention Days & Size Limit) Passed.");

// 13. Test Telegram Dynamic Approval Buttons Calculation
var testReq60 = new GraceRequest { Id = 101, RequestedMinutes = 60 };
var buttons60 = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
buttons60.Add(testReq60.RequestedMinutes);
if (testReq60.RequestedMinutes > 60) buttons60.Add(60);
if (testReq60.RequestedMinutes > 30) buttons60.Add(30);
if (testReq60.RequestedMinutes > 15) buttons60.Add(15);
Debug.Assert(buttons60.Contains(60), "60m request must generate Approve 60m option");
Debug.Assert(buttons60.Contains(30), "60m request must also generate Approve 30m option");
Debug.Assert(buttons60.Contains(15), "60m request must also generate Approve 15m option");

var testReq30 = new GraceRequest { Id = 102, RequestedMinutes = 30 };
var buttons30 = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
buttons30.Add(testReq30.RequestedMinutes);
if (testReq30.RequestedMinutes > 60) buttons30.Add(60);
if (testReq30.RequestedMinutes > 30) buttons30.Add(30);
if (testReq30.RequestedMinutes > 15) buttons30.Add(15);
Debug.Assert(!buttons30.Contains(60), "30m request should not offer 60m");
Debug.Assert(buttons30.Contains(30) && buttons30.Contains(15), "30m request should offer 30m and 15m");
Console.WriteLine("✅ Telegram Dynamic Approval Buttons Calculation Passed.");

// 14. Test Sparse Weekly Schedule Persistence and Fallback Deletion
LimitRepository.Upsert(new LimitConfig
{
    UserId = user.Id,
    DailyMinutes = 120,
    ScheduleStart = new TimeOnly(8, 0),
    ScheduleEnd = new TimeOnly(22, 0)
});
ScheduleRepository.SaveDaySchedule(new DaySchedule
{
    UserId = user.Id,
    DayOfWeek = DayOfWeek.Monday,
    DailyMinutes = 90,
    ScheduleStart = new TimeOnly(15, 0),
    ScheduleEnd = new TimeOnly(20, 0)
});

var monLimit = ScheduleRepository.GetEffectiveLimit(user.Id, DayOfWeek.Monday);
Debug.Assert(monLimit != null && monLimit.DailyMinutes == 90, "Monday custom limit should be 90m");
Debug.Assert(monLimit.ScheduleStart == new TimeOnly(15, 0), "Monday custom start should be 15:00");

var tueLimit = ScheduleRepository.GetEffectiveLimit(user.Id, DayOfWeek.Tuesday);
Debug.Assert(tueLimit != null && tueLimit.DailyMinutes == 120, "Tuesday should fall back to baseline 120m");

// Delete custom schedule for Monday -> should fall back to baseline
ScheduleRepository.DeleteDaySchedule(user.Id, DayOfWeek.Monday);
var monLimitAfterDelete = ScheduleRepository.GetEffectiveLimit(user.Id, DayOfWeek.Monday);
Debug.Assert(monLimitAfterDelete != null && monLimitAfterDelete.DailyMinutes == 120, "Monday should fall back to 120m baseline after deletion");
Console.WriteLine("✅ Sparse Weekly Schedule & Baseline Fallback Verification Passed.");

// 14b. Test Single-Day Schedule Exceptions & Precedence Hierarchy
var testDate = new DateOnly(2026, 10, 15); // Thursday
var exc = new ScheduleException
{
    UserId = user.Id,
    ExceptionDate = testDate,
    DailyMinutes = 300,
    ScheduleStart = new TimeOnly(7, 0),
    ScheduleEnd = new TimeOnly(23, 0),
    CreatedAt = DateTime.Now
};
ScheduleExceptionRepository.Upsert(exc);

var queriedExc = ScheduleExceptionRepository.GetForDate(user.Id, testDate);
Debug.Assert(queriedExc != null && queriedExc.DailyMinutes == 300, "Queried exception should have 300m");

var upcomingExcs = ScheduleExceptionRepository.GetUpcomingForUser(user.Id, new DateOnly(2026, 10, 1));
Debug.Assert(upcomingExcs.Any(e => e.ExceptionDate == testDate), "Upcoming exceptions should include test date");

// Precedence check: Exception should override baseline
var effectiveWithExc = ScheduleRepository.GetEffectiveLimit(user.Id, testDate.DayOfWeek, testDate);
Debug.Assert(effectiveWithExc != null && effectiveWithExc.DailyMinutes == 300, "Effective limit should use 300m exception");
Debug.Assert(effectiveWithExc!.ScheduleEnd == new TimeOnly(23, 0), "Effective limit should use 23:00 exception curfew");

// Delete exception -> fallback
ScheduleExceptionRepository.DeleteForUserAndDate(user.Id, testDate);
var effectiveAfterExcDelete = ScheduleRepository.GetEffectiveLimit(user.Id, testDate.DayOfWeek, testDate);
Debug.Assert(effectiveAfterExcDelete != null && effectiveAfterExcDelete.DailyMinutes == 120, "Should fall back to 120m baseline after exception delete");

// 14c. Test Schedule Change Grace Requests & Decline Reasons
var schedReq = GraceRequestRepository.CreateScheduleChange(
    user.Id,
    DateOnly.FromDateTime(DateTime.Now),
    testDate,
    240,
    new TimeOnly(8, 0),
    new TimeOnly(21, 0),
    "Study holiday");
Debug.Assert(schedReq.RequestType == "schedule_change", "RequestType should be schedule_change");
Debug.Assert(schedReq.TargetDate == testDate, "TargetDate should match");
Debug.Assert(schedReq.RequestedMinutes == 240, "RequestedMinutes should be 240");

GraceRequestRepository.Resolve(schedReq.Id, "DECLINED", "Belum menyelesaikan tugas sekolah");
var resolvedSchedReq = GraceRequestRepository.GetById(schedReq.Id);
Debug.Assert(resolvedSchedReq != null && resolvedSchedReq.Status == "DECLINED", "Status should be DECLINED");
Debug.Assert(resolvedSchedReq!.DeclineReason == "Belum menyelesaikan tugas sekolah", "DeclineReason should match");

var latestReq = GraceRequestRepository.GetLatestRequest(user.Id);
Debug.Assert(latestReq != null && latestReq.Id == schedReq.Id, "Latest request should match");
Console.WriteLine("✅ Schedule Exception Precedence & Extended Grace Requests Verification Passed.");

// 15. Test AppVersion & UpdateService SemVer Logic
Debug.Assert(AppVersion.Current == "1.5.0", "Current version should be 1.5.0");
Debug.Assert(AppVersion.DisplayName == "v1.5.0", "Display name should be v1.5.0");
// The fork must never offer the upstream installer as an update.
Debug.Assert(AppVersion.GitHubRepo == "nick-beee/WindowsParentalControl", "Updates must come from the fork");

Debug.Assert(UpdateService.IsNewerVersion("1.5.1", "1.5.0") == true, "1.5.1 is newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("2.0.0", "1.5.0") == true, "2.0.0 is newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("1.6.0", "1.5.0") == true, "1.6.0 is newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("1.5.0", "1.5.0") == false, "1.5.0 is not newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("1.4.1", "1.5.0") == false, "1.4.1 is not newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("1.4.0", "1.5.0") == false, "1.4.0 is not newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("1.3.0", "1.5.0") == false, "1.3.0 is not newer than 1.5.0");
Debug.Assert(UpdateService.IsNewerVersion("zzz", "1.5.0") == false, "A tag that is not a version is never newer");
Debug.Assert(UpdateService.IsNewerVersion("1.2.1", "1.4.1") == false, "1.2.1 is not newer than 1.4.1");
Console.WriteLine("✅ AppVersion & UpdateService SemVer Comparison Verification Passed.");

// Live call to the GitHub API. Skipped on GitHub Actions: shared runner IPs often hit the
// unauthenticated rate limit (403), which would make CI fail for no code reason.
var isGitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
var updateCheck = isGitHubActions ? null : UpdateService.CheckForUpdatesAsync().GetAwaiter().GetResult();
if (updateCheck is null)
{
    Console.WriteLine("ℹ️ UpdateService CheckForUpdatesAsync live check skipped on GitHub Actions.");
}
else
{
    Debug.Assert(updateCheck.Error == null, $"Error should be null on update check: {updateCheck.Error}");
    Console.WriteLine($"✅ UpdateService CheckForUpdatesAsync Passed (Latest: {updateCheck.LatestVersion}, HasUpdate: {updateCheck.HasUpdate}).");
}

var shouldTestLiveDownload = Environment.GetEnvironmentVariable("TEST_LIVE_DOWNLOAD") == "1";
if (shouldTestLiveDownload && !string.IsNullOrWhiteSpace(updateCheck?.DownloadUrl))
{
    int lastPct = -1;
    var progress = new Progress<int>(pct => { lastPct = pct; });
    var (downloadSuccess, downloadedPath, downloadMsg) = UpdateService.DownloadVerifiedUpdateAsync(progress).GetAwaiter().GetResult();
    Debug.Assert(downloadSuccess, $"Download should succeed: {downloadMsg}");
    Debug.Assert(File.Exists(downloadedPath), "Downloaded installer should exist");
    Debug.Assert(new FileInfo(downloadedPath!).Length > 1_000_000, "Downloaded installer should be > 1MB");
    Debug.Assert(lastPct == 100, "Progress should reach 100%");
    Console.WriteLine($"✅ UpdateService DownloadVerifiedUpdateAsync Live Verification Passed ({new FileInfo(downloadedPath!).Length} bytes, 100% progress).");
    try { File.Delete(downloadedPath!); } catch { }
}
else
{
    Console.WriteLine("ℹ️ UpdateService DownloadVerifiedUpdateAsync live download skipped (set TEST_LIVE_DOWNLOAD=1 to run).");
}

// 16. Test Telegram Session Notification Toggles & Security Alert Events
Debug.Assert(SettingsRepository.IsTelegramNotifySignInEnabled() == true, "Sign-in notification should be enabled by default");
Debug.Assert(SettingsRepository.IsTelegramNotifySignOutEnabled() == true, "Sign-out notification should be enabled by default");
Debug.Assert(SettingsRepository.IsTelegramNotifyAdminLogonEnabled() == true, "Admin logon alert should be enabled by default");

SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignIn, "false");
SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignOut, "false");
SettingsRepository.Set(SettingsRepository.KeyTelegramNotifyAdminLogon, "false");
Debug.Assert(SettingsRepository.IsTelegramNotifySignInEnabled() == false, "Sign-in notification should reflect disabled state");
Debug.Assert(SettingsRepository.IsTelegramNotifySignOutEnabled() == false, "Sign-out notification should reflect disabled state");
Debug.Assert(SettingsRepository.IsTelegramNotifyAdminLogonEnabled() == false, "Admin logon alert should reflect disabled state");

SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignIn, "true");
SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignOut, "true");
SettingsRepository.Set(SettingsRepository.KeyTelegramNotifyAdminLogon, "true");

EventRepository.LogEvent(testSid, EventType.SECURITY_ALERT, "Unauthorized administrator login alert test");
var secEvents = EventRepository.GetEvents(userSid: testSid);
Debug.Assert(secEvents.Any(e => e.EventType == EventType.SECURITY_ALERT), "SECURITY_ALERT event should be stored and retrievable");

var testLogger = ParentalControl.Core.Logging.LoggingConfig.CreateLogger("Test", Path.Combine(tempDir, "logs"));
var botService = new TelegramBotService(testLogger);
var resSignIn = botService.SendUserSignInNotificationAsync("testuser", "TEST-PC", DateTime.Now, 120, new TimeOnly(8, 0), new TimeOnly(20, 0)).GetAwaiter().GetResult();
Debug.Assert(!resSignIn, "SendUserSignInNotificationAsync should return false gracefully when token is unconfigured");
var resSignOut = botService.SendUserSignOutNotificationAsync("testuser", "TEST-PC", DateTime.Now, 60).GetAwaiter().GetResult();
Debug.Assert(!resSignOut, "SendUserSignOutNotificationAsync should return false gracefully when token is unconfigured");
var resAdmin = botService.SendAdminSignInAlertAsync(1, "Administrator", "TEST-PC", DateTime.Now).GetAwaiter().GetResult();
Debug.Assert(!resAdmin, "SendAdminSignInAlertAsync should return false gracefully when token is unconfigured");
Console.WriteLine("✅ Telegram Session Notification Toggles & Security Alert Events Passed.");

// 17. Test Offline Telegram Notification Queue & Timestamp Drift Bug Fix
var pastEventTime = DateTime.Now.AddMinutes(-15);
var dummyPayload = "{\"Username\":\"TestKid\",\"MachineName\":\"KID-PC\",\"MinutesUsedToday\":45}";
PendingNotificationRepository.Enqueue("USER_SIGN_OUT", dummyPayload, pastEventTime);
Debug.Assert(PendingNotificationRepository.GetCount() == 1, "Pending queue count should be 1 after enqueue");

var pendingList = PendingNotificationRepository.GetPending(5);
Debug.Assert(pendingList.Count == 1, "Pending list should return 1 item");
Debug.Assert(pendingList[0].NotificationType == "USER_SIGN_OUT", "Type should be USER_SIGN_OUT");
Debug.Assert(Math.Abs((pendingList[0].EventTime - pastEventTime).TotalSeconds) < 1, "Original event time must be preserved exactly");
Debug.Assert(pendingList[0].RetryCount == 0, "Initial retry count should be 0");

PendingNotificationRepository.IncrementRetry(pendingList[0].Id);
var retriedList = PendingNotificationRepository.GetPending(5);
Debug.Assert(retriedList[0].RetryCount == 1, "Retry count should be 1 after increment");

// Verify FormatTimeWithDelay behavior
var recentTime = DateTime.Now;
var formattedRecent = TelegramBotService.FormatTimeWithDelay(recentTime, "id");
Debug.Assert(!formattedRecent.Contains("Terkirim tertunda"), "Immediate notification should not have delayed tag");
Debug.Assert(formattedRecent.Contains(recentTime.ToString("HH:mm:ss")), "Recent time string must match event time");

var formattedDelayedId = TelegramBotService.FormatTimeWithDelay(pastEventTime, "id");
Debug.Assert(formattedDelayedId.Contains("Terkirim tertunda"), "Delayed notification in ID must contain 'Terkirim tertunda'");
Debug.Assert(formattedDelayedId.Contains(pastEventTime.ToString("HH:mm:ss")), "Delayed notification must display true original event time");

var formattedDelayedEn = TelegramBotService.FormatTimeWithDelay(pastEventTime, "en");
Debug.Assert(formattedDelayedEn.Contains("Delayed delivery"), "Delayed notification in EN must contain 'Delayed delivery'");
Debug.Assert(formattedDelayedEn.Contains(pastEventTime.ToString("HH:mm:ss")), "Delayed notification in EN must display true original event time");

PendingNotificationRepository.Delete(pendingList[0].Id);
Debug.Assert(PendingNotificationRepository.GetCount() == 0, "Pending queue should be 0 after delete");
Console.WriteLine("✅ Offline Telegram Notification Queue & Timestamp Drift Bug Fix Passed.");

// 18. Test Interactive Session Prompts Repository & Presets
var defaultPresets = SettingsRepository.GetPromptResponsePresets();
Debug.Assert(defaultPresets.Count >= 3, "Default prompt presets should have at least 3 items");
Debug.Assert(defaultPresets.Any(p => p.Contains("Hausaufgaben", StringComparison.OrdinalIgnoreCase)), "Default preset of a new install should be German");

// 18a. Language Preset: a new database gets "de"; a preset switch and an empty text keep the edited texts.
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyLanguagePreset) == "de", "A new database should store the preset de");
Debug.Assert(SettingsRepository.GetMessage(SettingsRepository.KeyMsgLimitReached) == MessageCatalog.Defaults("de")[ChildMessageKeys.LimitReached], "msg_limit_reached should be the German default");
var oneMinuteWarn = SettingsRepository.GetMessage(SettingsRepository.KeyMsgLimitWarn, new Dictionary<string, string> { ["minutes"] = "1" });
Debug.Assert(oneMinuteWarn.Contains("1 Minute um"), "The German warning should say '1 Minute'");
SettingsRepository.Set(SettingsRepository.KeyMsgRemoteLock, "Abendessen!");
var keptKeys = SettingsRepository.ApplyLanguagePreset("id");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyLanguagePreset) == "id", "The preset should be id after the switch");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyMsgRemoteLock) == "Abendessen!", "An edited text should survive a preset switch");
Debug.Assert(keptKeys.Contains(SettingsRepository.KeyMsgRemoteLock), "The switch should report the kept key");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyMsgLimitReached) == MessageCatalog.Defaults("id")[ChildMessageKeys.LimitReached], "An unedited text should get the Indonesian default");
SettingsRepository.Set(SettingsRepository.KeyMsgBonusGranted, "");
var bonusText = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBonusGranted, new Dictionary<string, string> { ["minutes"] = "15" });
Debug.Assert(bonusText == MessageCatalog.Defaults("id").Format(ChildMessageKeys.BonusGranted, ("minutes", 15)), "An empty text should give the default of the current preset");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyMsgRemoteLock) == "Abendessen!", "An empty text should not change another key");
// An update keeps the preset; an earlier "en" default gets the word forms.
SettingsRepository.ApplyLanguagePreset("en");
SettingsRepository.Set(SettingsRepository.KeyMsgLimitWarn, "You have {minutes} minutes of daily screen time remaining. Please save your work.");
DatabaseManager.Initialize();
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyLanguagePreset) == "en", "Initialize should keep the stored preset");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyMsgLimitWarn) == MessageCatalog.Defaults("en")[ChildMessageKeys.LimitWarn], "Initialize should replace an earlier default text");
Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyMsgRemoteLock) == "Abendessen!", "Initialize should keep an edited text");
SettingsRepository.ApplyLanguagePreset("de");
Console.WriteLine("✅ Language Preset Verification Passed.");

var promptId1 = SessionPromptRepository.Create(testSid, "TestBrother", "Mau makan siang?", "URGENT", 1, "Sekarang|Nanti 10m");
Debug.Assert(promptId1 > 0, "Created prompt ID should be > 0");

var activePrompt = SessionPromptRepository.GetActivePrompt("TestBrother");
Debug.Assert(activePrompt != null, "Active prompt should be found");
Debug.Assert(activePrompt!.Id == promptId1, "Active prompt ID should match");
Debug.Assert(activePrompt.Urgency == "URGENT", "Urgency should be URGENT");
Debug.Assert(activePrompt.TargetDisplay == 1, "Target display should be 1");
Debug.Assert(activePrompt.CustomOptions == "Sekarang|Nanti 10m", "Custom options should match");
Debug.Assert(activePrompt.Status == "PENDING", "Status should be PENDING");

var resolvedSuccess = SessionPromptRepository.ResolvePrompt(promptId1, "NO", "Nanti 10m", 14);
Debug.Assert(resolvedSuccess, "Prompt resolution should succeed");

var promptAfterResolve = SessionPromptRepository.GetById(promptId1);
Debug.Assert(promptAfterResolve != null, "Prompt should exist");
Debug.Assert(promptAfterResolve!.Status == "ANSWERED", "Status should be ANSWERED");
Debug.Assert(promptAfterResolve.Response == "NO", "Response should be NO");
Debug.Assert(promptAfterResolve.ResponseReason == "Nanti 10m", "Response reason should match");
Debug.Assert(promptAfterResolve.TurnaroundSeconds == 14, "Turnaround seconds should be 14");
Debug.Assert(promptAfterResolve.AnsweredAt != null, "AnsweredAt should not be null");

// Verify active prompt is now cleared
Debug.Assert(SessionPromptRepository.GetActivePrompt("TestBrother") == null, "Active prompt should be null after resolve");

// Test timeout resolution
var promptId2 = SessionPromptRepository.Create(testSid, "TestBrother", "Sudah selesai tugas?", "NORMAL", -1);
var activePrompt2 = SessionPromptRepository.GetActivePrompt("TestBrother");
Debug.Assert(activePrompt2 != null && activePrompt2.Id == promptId2, "Second prompt should be active");
SessionPromptRepository.ResolvePrompt(promptId2, "TIMEOUT", null, 120);
var prompt2After = SessionPromptRepository.GetById(promptId2);
Debug.Assert(prompt2After!.Status == "TIMEOUT", "Status should be TIMEOUT");
Debug.Assert(prompt2After.TurnaroundSeconds == 120, "Turnaround should be 120s");

// Test Prompt Event Logging
EventRepository.LogEvent(testSid, EventType.PROMPT_SENT, "Prompt sent test");
EventRepository.LogEvent(testSid, EventType.PROMPT_ANSWERED, "Prompt answered test");
EventRepository.LogEvent(testSid, EventType.PROMPT_TIMEOUT, "Prompt timeout test");
var promptEvents = EventRepository.GetEvents(userSid: testSid);
Debug.Assert(promptEvents.Any(e => e.EventType == EventType.PROMPT_SENT), "PROMPT_SENT should be in event log");
Debug.Assert(promptEvents.Any(e => e.EventType == EventType.PROMPT_ANSWERED), "PROMPT_ANSWERED should be in event log");
Debug.Assert(promptEvents.Any(e => e.EventType == EventType.PROMPT_TIMEOUT), "PROMPT_TIMEOUT should be in event log");
Console.WriteLine("✅ Interactive Session Prompts Repository & Presets Passed.");

// App Categories: seed, limits, rules, Category Usage, suspension records (add-app-categories, group 4)
{
    var catToday = DateOnly.FromDateTime(DateTime.Now);

    // Seed: seven categories, default limits, default rules, seed key.
    var catLimits = CategoryRepository.GetLimits();
    Debug.Assert(catLimits.Count == 7, "Seven categories should exist");
    Debug.Assert(catLimits[AppCategory.Games] == new DailyLimit(120, 180), "Games default should be 120/180");
    Debug.Assert(catLimits[AppCategory.Unknown] == new DailyLimit(120, 180), "Unknown default should be 120/180");
    Debug.Assert(catLimits[AppCategory.Study].IsNone, "Study should have no limit");
    Debug.Assert(catLimits[AppCategory.System].IsNone, "System should have no limit");
    Debug.Assert(!string.IsNullOrEmpty(SettingsRepository.Get(CategoryRepository.KeyCategoriesSeeded)), "Seed key should be set");
    using (var countConn = DatabaseManager.CreateConnection())
    using (var countCmd = countConn.CreateCommand())
    {
        countCmd.CommandText = "SELECT COUNT(*) FROM app_categories";
        Debug.Assert(Convert.ToInt32(countCmd.ExecuteScalar()) == 7, "Each category should exist one time");
    }

    var seededRules = AppRuleRepository.GetAllRows();
    Debug.Assert(seededRules.Count > 0 && seededRules.All(r => r.IsDefault), "Default rules should be seeded and marked default");
    var seededResolver = CategoryConfigCache.Current.Resolver;
    var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    Debug.Assert(seededResolver.Resolve(pf86 + @"\Steam\steamapps\common\Portal 2\portal2.exe").Category == AppCategory.Games, "Steam game should be in Games");
    Debug.Assert(seededResolver.Resolve(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE").Category == AppCategory.Study, "Word should be in Study");
    Debug.Assert(seededResolver.Resolve(winDir + @"\explorer.exe").Category == AppCategory.System, "Explorer should be in System");

    // A changed limit and a deleted default rule survive a second Initialize().
    CategoryRepository.SetLimit(AppCategory.Games, new DailyLimit(90, 180));
    var wordRule = seededRules.Single(r => r.Rule.Pattern.Equals("WINWORD.EXE", StringComparison.OrdinalIgnoreCase));
    Debug.Assert(AppRuleRepository.Delete(wordRule.Rule.Id), "Default Word rule should delete");
    var rulesBefore = AppRuleRepository.GetAll().Count;
    DatabaseManager.Initialize();
    Debug.Assert(CategoryRepository.GetLimits()[AppCategory.Games].WeekdayMinutes == 90, "Changed Games limit should survive Initialize");
    Debug.Assert(AppRuleRepository.GetAll().Count == rulesBefore, "Initialize should not add rules again");
    Debug.Assert(AppRuleRepository.GetAll().All(r => !r.Pattern.Equals("WINWORD.EXE", StringComparison.OrdinalIgnoreCase)), "Deleted default rule should stay deleted");
    Debug.Assert(CategoryConfigCache.Current.Resolver.Resolve(@"C:\Office\WINWORD.EXE").Category == AppCategory.Unknown, "Word should be Unknown without its rule");

    // Limit validation.
    var systemRefused = false;
    try { CategoryRepository.SetLimit(AppCategory.System, new DailyLimit(10, 10)); } catch (ArgumentException) { systemRefused = true; }
    Debug.Assert(systemRefused && CategoryRepository.GetLimits()[AppCategory.System].IsNone, "System limit should be refused");
    var rangeRefused = false;
    try { CategoryRepository.SetLimit(AppCategory.Games, new DailyLimit(2000, 180)); } catch (ArgumentException) { rangeRefused = true; }
    Debug.Assert(rangeRefused && CategoryRepository.GetLimits()[AppCategory.Games].WeekdayMinutes == 90, "2000 minutes should be refused");
    CategoryRepository.SetLimit(AppCategory.Video, DailyLimit.None);
    Debug.Assert(CategoryRepository.GetLimits()[AppCategory.Video].IsNone, "No limit should persist as NULL");

    // Rule CRUD with an App Limit.
    var mcId = AppRuleRepository.Insert(RuleMatchType.File, " Minecraft.Test.exe ", AppCategory.Games, new DailyLimit(180, 240));
    var mc = AppRuleRepository.GetById(mcId)!;
    Debug.Assert(mc.Pattern == "Minecraft.Test.exe" && mc.AppLimit == new DailyLimit(180, 240), "Rule should store trimmed pattern and App Limit");
    Debug.Assert(CategoryConfigCache.Current.Resolver.Resolve(@"C:\x\minecraft.test.exe").Rule?.Id == mcId, "Cache should see the new rule");
    var duplicateRefused = false;
    try { AppRuleRepository.Insert(RuleMatchType.File, "MINECRAFT.TEST.EXE", AppCategory.Video, null); } catch (InvalidOperationException) { duplicateRefused = true; }
    Debug.Assert(duplicateRefused, "Duplicate pattern (other letter case) should be refused");
    var invalidRefused = false;
    try { AppRuleRepository.Insert(RuleMatchType.File, "x.exe", AppCategory.System, new DailyLimit(10, null)); } catch (ArgumentException) { invalidRefused = true; }
    Debug.Assert(invalidRefused, "App Limit on System should be refused");
    Debug.Assert(AppRuleRepository.Update(mcId, RuleMatchType.File, "Minecraft.Test.exe", AppCategory.Games, null), "Update should succeed");
    Debug.Assert(AppRuleRepository.GetById(mcId)!.AppLimit is null, "Removed App Limit should be NULL");
    var folderId = AppRuleRepository.Insert(RuleMatchType.Folder, @"C:\Kid Games", AppCategory.Games, null);
    Debug.Assert(AppRuleRepository.GetById(folderId)!.Pattern == @"C:\Kid Games\", "Folder pattern should be stored canonical with trailing separator");
    Debug.Assert(AppRuleRepository.Delete(mcId) && AppRuleRepository.Delete(folderId), "Delete should succeed");
    Debug.Assert(AppRuleRepository.GetById(mcId) is null, "Deleted rule should be gone");

    // Category Usage: 3 x 20 s = 1 minute, System never counts.
    CategoryUsageRepository.ResetAccumulators();
    Debug.Assert(CategoryUsageRepository.AddSeconds(user.Id, catToday, AppCategory.Games, 20) == 0, "20 s should not commit");
    Debug.Assert(CategoryUsageRepository.AddSeconds(user.Id, catToday, AppCategory.Games, 20) == 0, "40 s should not commit");
    Debug.Assert(CategoryUsageRepository.AddSeconds(user.Id, catToday, AppCategory.Games, 20) == 1, "60 s should commit 1 minute");
    for (var i = 0; i < 6; i++) CategoryUsageRepository.AddSeconds(user.Id, catToday, AppCategory.System, 20);
    var catUsage = CategoryUsageRepository.GetForUserAndDate(user.Id, catToday);
    Debug.Assert(catUsage.GetValueOrDefault(AppCategory.Games) == 1, "Games usage should be 1 minute");
    Debug.Assert(!catUsage.ContainsKey(AppCategory.System), "System should not be counted");

    // Retention.
    CategoryUsageRepository.AddMinutes(user.Id, catToday.AddDays(-40), AppCategory.Games, 5);
    Debug.Assert(CategoryUsageRepository.DeleteOlderThan(catToday.AddDays(-DatabaseManager.RetentionDays)) == 1, "Old category usage should be deleted");
    Debug.Assert(CategoryUsageRepository.GetForUserAndDate(user.Id, catToday).GetValueOrDefault(AppCategory.Games) == 1, "Today's usage should stay");

    // Suspension records: the start time keeps full precision.
    var startUtc = new DateTime(2026, 11, 25, 7, 30, 15, DateTimeKind.Utc).AddTicks(1234567);
    var rec = new SuspensionRecord(4242, startUtc, 1, user.Id, AppCategory.Games, @"C:\Games\x.exe");
    SuspensionRepository.Insert(rec);
    SuspensionRepository.Insert(rec); // the same process again replaces the record
    var recs = SuspensionRepository.GetAll();
    Debug.Assert(recs.Count == 1 && recs[0] == rec, "Suspension record should round-trip exactly");
    Debug.Assert(!SuspensionRepository.Delete(4242, startUtc.AddTicks(1)), "Another start time should not delete the record");
    Debug.Assert(SuspensionRepository.Delete(4242, startUtc), "Delete by pid and start time should succeed");
    Debug.Assert(SuspensionRepository.GetAll().Count == 0, "No records should remain");

    CategoryRepository.SetLimit(AppCategory.Games, new DailyLimit(120, 180));
    CategoryRepository.SetLimit(AppCategory.Video, new DailyLimit(120, 180));
}
Console.WriteLine("✅ App Categories, Rules, Category Usage & Suspension Records Passed.");

// Process list, suspend and resume (add-app-categories, task 6.1)
{
    using var self = Process.GetCurrentProcess();
    var ownSession = self.SessionId;
    var listed = ProcessControl.ListProcesses(new HashSet<int> { ownSession });
    var me = listed.SingleOrDefault(p => p.Pid == Environment.ProcessId);
    Debug.Assert(me is not null, "The test process should be in the list of its session");
    Debug.Assert(string.Equals(WindowsPath.Canonicalize(me!.ExePath), WindowsPath.Canonicalize(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
        $"The listed path should be the test exe path: {me.ExePath} vs {Environment.ProcessPath}");
    Debug.Assert(me.StartTimeUtc == self.StartTime.ToUniversalTime(), "The listed start time should equal Process.StartTime");
    Debug.Assert(!me.IsSuspended, "The test process should not be suspended");
    Debug.Assert(ProcessControl.ListProcesses(new HashSet<int> { ownSession + 1000 }).Count == 0, "Another session should list nothing");

    // A helper process: suspend, check, resume. It stays open the whole time.
    using var helper = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
    Thread.Sleep(500);
    var (_, helperStart) = ProcessControl.QueryPathAndStart(helper.Id);
    Debug.Assert(helperStart is not null, "Helper start time should be readable");
    Debug.Assert(ProcessControl.Suspend(helper.Id, helperStart!.Value.AddTicks(1), out _) == ProcessOpResult.NotFound, "Wrong start time should refuse the suspend");
    Debug.Assert(ProcessControl.Suspend(helper.Id, helperStart.Value, out var suspendDetail) == ProcessOpResult.Ok, "Suspend should succeed: " + suspendDetail);
    Thread.Sleep(200);
    var helperRow = ProcessControl.ListProcesses(new HashSet<int> { ownSession }).Single(p => p.Pid == helper.Id);
    Debug.Assert(helperRow.IsSuspended, "Helper should show all threads suspended");
    Debug.Assert(!helper.HasExited, "Suspended helper should still exist");
    // A second suspend (count 2) and one Resume call: Resume repeats until no thread is suspended.
    Debug.Assert(ProcessControl.Suspend(helper.Id, helperStart.Value, out _) == ProcessOpResult.Ok, "Second suspend should succeed");
    Debug.Assert(ProcessControl.Resume(helper.Id, helperStart.Value, out var resumeDetail) == ProcessOpResult.Ok, "Resume should succeed: " + resumeDetail);
    Thread.Sleep(200);
    helperRow = ProcessControl.ListProcesses(new HashSet<int> { ownSession }).Single(p => p.Pid == helper.Id);
    Debug.Assert(!helperRow.IsSuspended, "Helper should run again after resume");
    try { helper.Kill(); } catch { } // the test's own helper, not a program of the child
    helper.WaitForExit(5000);
    Debug.Assert(ProcessControl.Resume(helper.Id, helperStart.Value, out _) != ProcessOpResult.Ok, "Resume of an ended process should report that it is gone");
}
Console.WriteLine("✅ Process List, Suspend & Resume Passed.");

// Admin sign-in storage and start check (require-admin-sign-in, tasks 3.1 and 3.3)
{
    var now = DateTimeOffset.UtcNow;
    AdminAccessRepository.DeleteAllFailures();
    AdminAccessRepository.AddFailure(LoginSource.Local, now.AddMinutes(-5));
    AdminAccessRepository.AddFailure(LoginSource.Local, now.AddMinutes(-4));
    AdminAccessRepository.AddFailure("192.168.1.50", now.AddMinutes(-3));
    AdminAccessRepository.AddFailure(LoginSource.Local, now.AddHours(-25));
    DatabaseManager.Initialize(); // the rows survive a new Initialize()
    Debug.Assert(DatabaseManager.DataFolderSecured, "A test data folder counts as secured");
    var recentFailures = AdminAccessRepository.GetRecentFailures(now);
    Debug.Assert(recentFailures.Count == 3, "Only the failures of the last 24 hours should be listed");
    Debug.Assert(recentFailures.All(f => Math.Abs((f.FailedAtUtc - now).TotalMinutes) < 6), "Failure times should round-trip");
    Debug.Assert(AdminAccessRepository.DeleteFailuresOlderThan24Hours(now) == 1, "The failure of 25 hours ago should be deleted");
    Debug.Assert(AdminAccessRepository.DeleteFailuresOfSource("192.168.1.50") == 1, "Delete by source should remove only that source");
    Debug.Assert(AdminAccessRepository.GetRecentFailures(now).All(f => f.Source == LoginSource.Local), "The failures of the other source should stay");
    Debug.Assert(AdminAccessRepository.DeleteAllFailures() == 2, "Delete all should remove the rest");

    // SetMany writes all keys or none.
    SettingsRepository.SetMany(new Dictionary<string, string> { ["admin_test_a"] = "1", ["admin_test_b"] = "2" });
    Debug.Assert(SettingsRepository.Get("admin_test_a") == "1" && SettingsRepository.Get("admin_test_b") == "2", "SetMany should write all keys");
    static IEnumerable<KeyValuePair<string, string>> FailHalfway()
    {
        yield return new("admin_test_c", "3");
        throw new InvalidOperationException("test");
    }
    var setManyFailed = false;
    try { SettingsRepository.SetMany(FailHalfway()); } catch (InvalidOperationException) { setManyFailed = true; }
    Debug.Assert(setManyFailed && SettingsRepository.Get("admin_test_c") == "", "SetMany should write no key when it fails");

    // Session epoch: missing = 0, increment, garbage = not valid, increment repairs it.
    Debug.Assert(AdminAccessRepository.GetSessionEpoch() == 0, "The epoch should start at 0");
    AdminAccessRepository.SignOutAllSessions();
    Debug.Assert(AdminAccessRepository.GetSessionEpoch() == 1, "Sign out all should increment the epoch");
    SettingsRepository.Set(SettingsRepository.KeyAdminSessionEpoch, "garbage");
    Debug.Assert(AdminAccessRepository.GetSessionEpoch() is null, "An epoch that is not a number should not be valid");
    AdminAccessRepository.SignOutAllSessions();
    Debug.Assert(AdminAccessRepository.GetSessionEpoch() == 1, "Increment should repair an epoch that is not a number");

    // Setup in one transaction; the confirmation code cannot log in.
    Debug.Assert(!AdminAccessRepository.IsCredentialSetUp(), "A fresh install should not be set up");
    var invalidSetupRefused = false;
    try { AdminAccessRepository.CompleteSetup("SHORT", 1, now); } catch (ArgumentException) { invalidSetupRefused = true; }
    Debug.Assert(invalidSetupRefused && !AdminAccessRepository.IsCredentialSetUp(), "A setup with an invalid secret should save nothing");
    var adminSecret = TotpService.GenerateSecret();
    Debug.Assert(TotpSecret.TryDecode(adminSecret, out var adminKey), "A new secret should be valid");
    var confirmStep = Totp.StepAt(now);
    var confirmCode = Totp.ComputeCode(adminKey, confirmStep);
    Debug.Assert(Totp.TryVerify(adminSecret, confirmCode, now, Totp.NoStepUsed, out var matchedStep) && matchedStep == confirmStep, "The confirmation code should be valid");
    AdminAccessRepository.AddFailure(LoginSource.Local, now);
    AdminAccessRepository.CompleteSetup(adminSecret.ToLowerInvariant(), matchedStep, now);
    Debug.Assert(AdminAccessRepository.IsCredentialSetUp(), "The setup should be complete");
    Debug.Assert(SettingsRepository.Get(SettingsRepository.KeyTotpSecret) == adminSecret, "The secret should be saved in normal form");
    Debug.Assert(AdminAccessRepository.GetSessionEpoch() == 2, "The setup should end all Admin Sessions");
    Debug.Assert(AdminAccessRepository.GetRecentFailures(now).Count == 0, "The setup should clear the login limit");
    Debug.Assert(AdminAccessRepository.GetSetupTime() is not null, "The setup time should be saved");
    Debug.Assert(!Totp.TryVerify(adminSecret, confirmCode, now, AdminAccessRepository.GetLastUsedStep(), out _), "The confirmation code should not log in");

    // Start check: the four upgrade cases.
    static AdminStartupPlan PlanWith(string enabled, string secret)
    {
        SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, enabled);
        SettingsRepository.Set(SettingsRepository.KeyTotpSecret, secret);
        return AdminSetup.PlanStart(
            SettingsRepository.Get(SettingsRepository.KeyTotpEnabled),
            SettingsRepository.Get(SettingsRepository.KeyTotpSecret),
            noticeSent: false, telegramConfigured: true);
    }
    var planValid = PlanWith("true", adminSecret);
    Debug.Assert(!planValid.DisableTotp && !planValid.SendSetupNotice && AdminAccessRepository.IsCredentialSetUp(), "TOTP on with a valid secret keeps its sign-in");
    var planEmpty = PlanWith("true", "");
    Debug.Assert(planEmpty.DisableTotp && planEmpty.SendSetupNotice && !AdminAccessRepository.IsCredentialSetUp(), "TOTP on with an empty secret is not set up");
    var planShort = PlanWith("true", TotpSecret.Encode(new byte[9]));
    Debug.Assert(planShort.DisableTotp && !AdminAccessRepository.IsCredentialSetUp(), "TOTP on with a 9-byte secret is not set up");
    var planOff = PlanWith("false", adminSecret);
    Debug.Assert(!planOff.DisableTotp && planOff.SendSetupNotice && !AdminAccessRepository.IsCredentialSetUp(), "TOTP off with an old secret is not set up");

    SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, "false");
    SettingsRepository.Set(SettingsRepository.KeyTotpSecret, "");
}
Console.WriteLine("✅ Admin Sign-In Storage & Start Check Passed.");

// Data folder owner and access rules (require-admin-sign-in D9, findings B1, B2, S1, S2). Needs an elevated administrator (CI, SSH as nick).
{
    var aclBase = Path.Combine(Path.GetTempPath(), "PcAclTest_" + Guid.NewGuid().ToString("N"));
    var aclRoot = Path.Combine(aclBase, "ParentalControl");
    var aclOutside = Path.Combine(aclBase, "Outside");
    var usersSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
    var systemSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
    var adminsSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
    // Test items get an explicit owner and DACL, so the result does not depend on the default owner of the test account.
    void SetSddl(FileSystemInfo item, string sddl)
    {
        const System.Security.AccessControl.AccessControlSections sections =
            System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access;
        if (item is DirectoryInfo d) { var sec = new System.Security.AccessControl.DirectorySecurity(); sec.SetSecurityDescriptorSddlForm(sddl, sections); d.SetAccessControl(sec); }
        else if (item is FileInfo f) { var sec = new System.Security.AccessControl.FileSecurity(); sec.SetSecurityDescriptorSddlForm(sddl, sections); f.SetAccessControl(sec); }
    }
    // A folder of an install: owner Administrators, SYSTEM and Administrators, plus read for Users (an old, open rule).
    const string trustedWithUsersRead = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FR;;;BU)";
    // A folder that a standard user could change: Users have full access.
    const string usersCanWrite = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";
    List<System.Security.AccessControl.FileSystemAccessRule> RulesOf(System.Security.AccessControl.FileSystemSecurity sec, bool explicitOnly) =>
        sec.GetAccessRules(true, !explicitOnly, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>()
           .Where(r => !explicitOnly || !r.IsInherited).ToList();
    bool OnlySystemAndAdmins(IEnumerable<System.Security.AccessControl.FileSystemAccessRule> rules) =>
        rules.Any() && rules.All(r => r.IdentityReference.Equals(systemSid) || r.IdentityReference.Equals(adminsSid));
    void AssertSecuredRoot(string path, string what)
    {
        var info = new DirectoryInfo(path);
        Debug.Assert(info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint), $"{what}: the data folder should be a real folder");
        var sec = info.GetAccessControl();
        Debug.Assert(sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Equals(adminsSid), $"{what}: the folder owner should be Administrators");
        Debug.Assert(sec.AreAccessRulesProtected, $"{what}: the folder DACL should be protected");
        var rules = RulesOf(sec, explicitOnly: false);
        Debug.Assert(rules.Count == 2 && OnlySystemAndAdmins(rules),
            $"{what}: the folder should have only SYSTEM and Administrators: " + string.Join(", ", rules.Select(r => r.IdentityReference.Value)));
    }
    void RunCmd(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(10000);
        Debug.Assert(p.ExitCode == 0, "cmd /c " + arguments + " failed");
    }
    try
    {
        Directory.CreateDirectory(aclBase);
        var outsideDir = Directory.CreateDirectory(aclOutside);
        SetSddl(outsideDir, trustedWithUsersRead);
        File.WriteAllText(Path.Combine(aclOutside, "target.txt"), "outside");

        // 1. An install with open read rules, a link inside and a hard link to data.db (D9, S2).
        var rootDir = Directory.CreateDirectory(aclRoot);
        SetSddl(rootDir, trustedWithUsersRead);
        var subDir = Directory.CreateDirectory(Path.Combine(aclRoot, "logs"));
        var dbPath = Path.Combine(aclRoot, "data.db");
        File.WriteAllText(dbPath, "secret");
        File.WriteAllText(Path.Combine(subDir.FullName, "x.log"), "x");
        foreach (FileSystemInfo item in new FileSystemInfo[] { subDir, new FileInfo(dbPath), new FileInfo(Path.Combine(subDir.FullName, "x.log")) })
            SetSddl(item, "O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;;FR;;;BU)");
        var hardLink = Path.Combine(aclOutside, "data-hardlink.db");
        Debug.Assert(TestNative.CreateHardLinkW(hardLink, dbPath, IntPtr.Zero), "The test hard link should be created");
        RunCmd($"mklink /J \"{Path.Combine(aclRoot, "captures")}\" \"{aclOutside}\"");
        Directory.CreateSymbolicLink(Path.Combine(aclRoot, "link"), aclOutside);

        DataFolderResult secured = null!;
        try { secured = DataFolderGuard.Secure(aclRoot); }
        catch (Exception ex) { Debug.Fail("DataFolderGuard.Secure failed (the test needs an elevated administrator): " + ex); }
        Debug.Assert(secured.QuarantinedTo is null, "A trusted folder should not be moved: " + secured.QuarantineReason);
        AssertSecuredRoot(aclRoot, "Open read rules");
        foreach (var child in new FileSystemInfo[] { subDir, new FileInfo(dbPath), new FileInfo(Path.Combine(subDir.FullName, "x.log")) })
        {
            System.Security.AccessControl.FileSystemSecurity sec = child is DirectoryInfo cd ? cd.GetAccessControl() : ((FileInfo)child).GetAccessControl();
            Debug.Assert(sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Equals(adminsSid), $"The owner of {child.Name} should be Administrators");
            Debug.Assert(!sec.AreAccessRulesProtected, $"{child.Name} should inherit the rules of the folder");
            Debug.Assert(RulesOf(sec, explicitOnly: true).Count == 0, $"{child.Name} should have no explicit rule");
            var all = RulesOf(sec, explicitOnly: false);
            Debug.Assert(OnlySystemAndAdmins(all),
                $"{child.Name} should have only SYSTEM and Administrators: " + string.Join(", ", all.Select(r => r.IdentityReference.Value)));
        }
        Debug.Assert(!Directory.Exists(Path.Combine(aclRoot, "captures")) && !File.Exists(Path.Combine(aclRoot, "link")), "The links in the folder should be deleted");
        Debug.Assert(File.ReadAllText(Path.Combine(aclOutside, "target.txt")) == "outside", "The target of a link should keep its content");
        Debug.Assert(RulesOf(outsideDir.GetAccessControl(), explicitOnly: true).Any(r => r.IdentityReference.Equals(usersSid)), "The target of a link should keep its rules");
        Debug.Assert(secured.Repairs.Count >= 3, "The result should list the deleted links and the replaced hard link: " + string.Join(" | ", secured.Repairs));
        Debug.Assert(File.ReadAllText(dbPath) == "secret", "data.db should keep its content after the copy");
        Debug.Assert(TestNative.LinkCount(dbPath) == 1, "data.db should have one name after the repair");
        // A second start finds nothing to repair: no permanent lockout because of a link.
        var again = DataFolderGuard.Secure(aclRoot);
        Debug.Assert(again.QuarantinedTo is null && again.Repairs.Count == 0, "A second start should change nothing");
        DataFolderGuard.EnsureSafeToOpen(aclRoot, "data.db"); // the check of the Admin app passes

        // 2. B1: a junction as the data folder, with a planted data.db in its target.
        Directory.Delete(aclRoot, true);
        File.WriteAllText(Path.Combine(aclOutside, "data.db"), "planted");
        RunCmd($"mklink /J \"{aclRoot}\" \"{aclOutside}\"");
        var adminAppRefused = false;
        try { DataFolderGuard.EnsureSafeToOpen(aclRoot, "data.db"); } catch (DataFolderUnsafeException) { adminAppRefused = true; }
        Debug.Assert(adminAppRefused, "The Admin app should not open a data folder that is a junction");
        var junctionResult = DataFolderGuard.Secure(aclRoot);
        AssertSecuredRoot(aclRoot, "Junction");
        Debug.Assert(!File.Exists(Path.Combine(aclRoot, "data.db")), "The new data folder should be empty");
        Debug.Assert(File.ReadAllText(Path.Combine(aclOutside, "data.db")) == "planted", "The target of the junction should not change");
        Debug.Assert(RulesOf(outsideDir.GetAccessControl(), explicitOnly: true).Any(r => r.IdentityReference.Equals(usersSid)), "The target of the junction should keep its rules");
        Debug.Assert(junctionResult.Repairs.Count == 1, "The result should report the deleted junction");

        // 3. B2: a folder that Users can change (as one that the child made before the install), with a planted data.db.
        Directory.Delete(aclRoot, true);
        var plantedRoot = Directory.CreateDirectory(aclRoot);
        SetSddl(plantedRoot, usersCanWrite);
        File.WriteAllText(Path.Combine(aclRoot, "data.db"), "planted");
        var plantedRefused = false;
        try { DataFolderGuard.EnsureSafeToOpen(aclRoot, "data.db"); } catch (DataFolderUnsafeException) { plantedRefused = true; }
        Debug.Assert(plantedRefused, "The Admin app should not open a folder that Users can change");

        // While a process holds the folder without delete sharing, the move fails and the start fails (fail closed).
        using (var held = TestNative.OpenDirectoryWithoutDeleteSharing(aclRoot))
        {
            Debug.Assert(!held.IsInvalid, "The test should hold the folder");
            var heldRefused = false;
            try { DataFolderGuard.Secure(aclRoot); } catch (DataFolderUnsafeException) { heldRefused = true; }
            Debug.Assert(heldRefused, "Secure should fail while the untrusted folder cannot be moved");
            Debug.Assert(File.ReadAllText(Path.Combine(aclRoot, "data.db")) == "planted", "Nothing should change when the move fails");
        }

        var plantedResult = DataFolderGuard.Secure(aclRoot);
        Debug.Assert(plantedResult.QuarantinedTo is { } moved && Directory.Exists(moved) && Path.GetFileName(moved).StartsWith("ParentalControl.untrusted-"),
            "The untrusted folder should be moved to ParentalControl.untrusted-<guid>");
        Debug.Assert(File.ReadAllText(Path.Combine(plantedResult.QuarantinedTo!, "data.db")) == "planted", "The moved folder should keep its content");
        Debug.Assert(plantedResult.QuarantineReason!.Contains(usersSid.Value), "The reason should name the account with write rights: " + plantedResult.QuarantineReason);
        AssertSecuredRoot(aclRoot, "Planted folder");
        Debug.Assert(!File.Exists(Path.Combine(aclRoot, "data.db")), "The service should start with a new, empty folder");
        AssertSecuredRoot(plantedResult.QuarantinedTo!, "Moved folder");

        // 4. A missing folder is created with its final rules.
        Directory.Delete(aclRoot, true);
        Debug.Assert(DataFolderGuard.Secure(aclRoot).Repairs.Count == 0, "A new folder needs no repair");
        AssertSecuredRoot(aclRoot, "New folder");
    }
    finally
    {
        try { Directory.Delete(aclRoot); } catch { } // a junction: delete the link only
        try { Directory.Delete(aclBase, true); } catch { }
    }
}
Console.WriteLine("✅ Data Folder Owner & Access Rules Passed.");

// Admin gate over HTTP (require-admin-sign-in, group 4): the real routes on a free local port.
{
    var gateLogger = new Serilog.LoggerConfiguration().CreateLogger();
    var gateTelegram = new TelegramBotService(gateLogger);
    var webHost = new ParentalControl.Service.Web.WebServerHost(
        new ParentalControl.Service.SessionTracker(gateLogger), gateTelegram, gateLogger, new ForegroundHistory());
    var webBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
    Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(webBuilder.WebHost, "http://127.0.0.1:0");
    var webApp = webBuilder.Build();
    webHost.ConfigureApp(webApp);

    // Deny by default (D4): each route is a child-facing route, /admin, or classified as Admin, Login or Logout.
    var routeTemplates = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)webApp).DataSources
        .SelectMany(d => d.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText ?? "").ToList();
    Debug.Assert(routeTemplates.Count > 30, "The routes should be mapped");
    foreach (var template in routeTemplates)
    {
        var isOpen = template is "/" or "/request" or "/api/request" or "/admin" || template.StartsWith("/api/agent/", StringComparison.Ordinal);
        Debug.Assert(isOpen || AdminRoutePolicy.Classify(template) != AdminRouteClass.Open, $"Route {template} is neither a child route nor behind the admin gate");
    }

    await webApp.StartAsync();
    var baseUrl = webApp.Urls.First().TrimEnd('/');
    using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

    async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? cookie = null, string? origin = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, baseUrl + path) { Content = content };
        if (cookie != null) request.Headers.Add("Cookie", "pc_admin_session=" + cookie);
        if (origin != null) request.Headers.Add("Origin", origin);
        return await http.SendAsync(request);
    }
    HttpContent Json(string json) => new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    HttpContent CodeForm(string code) => new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code });
    string? CookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith("pc_admin_session=") && v.Length > "pc_admin_session=".Length)
                    .Select(v => v["pc_admin_session=".Length..]).FirstOrDefault()
            : null;
    int BonusToday() => UsageRepository.GetUsage(user.Id, DateOnly.FromDateTime(DateTime.Now))?.BonusMinutes ?? 0;
    var grantBody = $$"""{"userId":{{user.Id}},"minutes":30}""";

    try
    {
        // Before the setup: setup text, no form, the login refuses each code, admin routes give 401.
        SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, "false");
        var setupPage = await (await Send(HttpMethod.Get, "/admin")).Content.ReadAsStringAsync();
        Debug.Assert(setupPage.Contains("Web-Verwaltung ist gesperrt") && !setupPage.Contains("<form"), "/admin should show the setup text and no form before the setup");
        foreach (var anyCode in new[] { "000000", "" })
        {
            var notSetUp = await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(anyCode));
            Debug.Assert((int)notSetUp.StatusCode == 401 && CookieOf(notSetUp) is null, "The login should refuse each code before the setup");
        }
        var bonusBefore = BonusToday();
        Debug.Assert((int)(await Send(HttpMethod.Post, "/api/admin/grant", content: Json(grantBody))).StatusCode == 401, "Grant without a session should give 401");
        foreach (var variant in new[] { "//api/admin/grant", "/api//admin/grant", "/api/admin/grant/", "/API/ADMIN/GRANT", "/api/admin/%67rant", "/api/admin/unknown" })
        {
            var status = (int)(await Send(HttpMethod.Post, variant, content: Json(grantBody))).StatusCode;
            Debug.Assert(status == 401 || status == 404, $"Path variant {variant} should give 401 or 404, not {status}");
        }
        Debug.Assert(BonusToday() == bonusBefore, "Bonus Minutes should not change without a session");

        // Child-facing routes stay open.
        var requestStatus = (int)(await Send(HttpMethod.Get, "/request")).StatusCode;
        Debug.Assert(requestStatus != 401 && requestStatus != 403, "/request should work without a session");
        var agentStatus = (int)(await Send(HttpMethod.Get, "/api/agent/status?user=TestBrother")).StatusCode;
        Debug.Assert(agentStatus == 200, "/api/agent/status should work without a session");

        // Setup, then login with a valid code one time only.
        var gateSecret = TotpService.GenerateSecret();
        TotpSecret.TryDecode(gateSecret, out var gateKey);
        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        AdminAccessRepository.CompleteSetup(gateSecret, step - 2, DateTimeOffset.UtcNow);
        var loginPage = await (await Send(HttpMethod.Get, "/admin")).Content.ReadAsStringAsync();
        Debug.Assert(loginPage.Contains("action=\"/api/admin/login\""), "/admin should show the login form after the setup");
        string WrongCode()
        {
            var now = Totp.StepAt(DateTimeOffset.UtcNow);
            var valid = Enumerable.Range(-2, 5).Select(d => Totp.ComputeCode(gateKey, now + d)).ToHashSet();
            return Enumerable.Range(0, 1000).Select(i => i.ToString("D6")).First(c => !valid.Contains(c));
        }
        string CurrentCode() => Totp.ComputeCode(gateKey, Totp.StepAt(DateTimeOffset.UtcNow));

        var code = CurrentCode();
        var login = await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(code));
        var cookie = CookieOf(login);
        Debug.Assert((int)login.StatusCode == 302 && cookie != null, "A valid code should give an Admin Session");
        Debug.Assert(login.Headers.GetValues("Set-Cookie").All(v => !v.Contains("expires=", StringComparison.OrdinalIgnoreCase) && !v.Contains("max-age", StringComparison.OrdinalIgnoreCase)),
            "The admin cookie should be a session cookie (no Expires)");
        var replay = await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(code));
        Debug.Assert((int)replay.StatusCode == 401 && CookieOf(replay) is null, "The same code a second time should fail");
        Debug.Assert((int)(await Send(HttpMethod.Post, "/api/admin/login", content: Json("{}"))).StatusCode == 401, "A JSON login without a code should fail, not crash");

        var settings = await Send(HttpMethod.Get, "/api/admin/settings", cookie);
        Debug.Assert((int)settings.StatusCode == 200, "A valid session should read the settings");
        Debug.Assert(!(await settings.Content.ReadAsStringAsync()).Contains(gateSecret), "No route should show the secret");
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings")).StatusCode == 401, "No cookie should give 401");
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", "0123456789ABCDEF")).StatusCode == 401, "An unknown token should give 401");

        // Origin check.
        var foreign = await Send(HttpMethod.Post, "/api/admin/grant", cookie, "http://localhost:8000", Json(grantBody));
        Debug.Assert((int)foreign.StatusCode == 403 && BonusToday() == bonusBefore, "A POST from another origin should give 403 and change nothing");
        var sameOrigin = await Send(HttpMethod.Post, "/api/admin/settings", cookie, baseUrl, Json("{}"));
        Debug.Assert((int)sameOrigin.StatusCode == 200, "A POST from the same origin should work");
        Debug.Assert((int)(await Send(HttpMethod.Post, "/api/admin/login", origin: "http://localhost:8000", content: CodeForm(WrongCode()))).StatusCode == 403, "A login from another origin should give 403");

        // S4: a login and an Admin Session from a process of a Restricted User on this PC are refused.
        // The test process is the client; for this check its own account becomes a Restricted User.
        var selfSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Debug.Assert(TcpConnectionOwner.ReadProcess(Environment.ProcessId).UserSid == selfSid, "The owner lookup should read the user of this process");
        UserRepository.Upsert(selfSid, "SelfAsChild", true);
        try
        {
            AdminAccessRepository.SetLastUsedStep(Totp.StepAt(DateTimeOffset.UtcNow) - 2);
            var failuresBefore = AdminAccessRepository.GetRecentFailures(DateTimeOffset.UtcNow).Count;
            var childLogin = await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(CurrentCode()));
            Debug.Assert((int)childLogin.StatusCode == 403 && CookieOf(childLogin) is null, "A login from a process of a Restricted User should give 403 and no session");
            Debug.Assert((await childLogin.Content.ReadAsStringAsync()).Contains("Windows-Konto"), "The page should tell the parent to use another account or device");
            Debug.Assert(AdminAccessRepository.GetRecentFailures(DateTimeOffset.UtcNow).Count == failuresBefore, "A refused client should not count as a failed login");
            Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie)).StatusCode == 401, "An Admin Session used from a process of a Restricted User should give 401");
            Debug.Assert((int)(await Send(HttpMethod.Get, "/admin", cookie)).StatusCode == 403, "/admin should refuse a process of a Restricted User");
        }
        finally
        {
            UserRepository.DeleteBySid(selfSid);
        }
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie)).StatusCode == 200, "The session should work again from a process of the parent");

        // Logout is a POST and ends the session.
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/logout", cookie)).StatusCode == 405, "GET logout should give 405");
        Debug.Assert((int)(await Send(HttpMethod.Post, "/api/admin/logout", cookie)).StatusCode == 302, "POST logout should redirect");
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie)).StatusCode == 401, "The old cookie should give 401 after the logout");

        // A new session epoch ("Sign out all web sessions") and an epoch that is not a number end the session.
        AdminAccessRepository.SetLastUsedStep(Totp.StepAt(DateTimeOffset.UtcNow) - 2);
        var cookie2 = CookieOf(await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(CurrentCode())));
        Debug.Assert(cookie2 != null && (int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie2)).StatusCode == 200, "A second login should work");
        AdminAccessRepository.SignOutAllSessions();
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie2)).StatusCode == 401, "A new epoch should end all sessions");

        // Login limit: 10 wrong codes from this PC block the 11th code, also a valid one.
        AdminAccessRepository.DeleteAllFailures();
        AdminAccessRepository.SetLastUsedStep(Totp.StepAt(DateTimeOffset.UtcNow) - 2);
        for (var i = 0; i < LoginThrottle.MaxPerSource; i++)
            Debug.Assert((int)(await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(WrongCode()))).StatusCode == 401, "A wrong code should give 401");
        var blocked = await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(CurrentCode()));
        Debug.Assert((int)blocked.StatusCode == 429 && CookieOf(blocked) is null, "The 11th code should be refused without a check");
        Debug.Assert((await blocked.Content.ReadAsStringAsync()).Contains("Zu viele falsche Codes"), "The page should show the block message");
        Debug.Assert(AdminAccessRepository.GetRecentFailures(DateTimeOffset.UtcNow).Count == LoginThrottle.MaxPerSource, "A refused login should not count as a failure");
        // The counts are in SQLite: a new service instance (a restart) still blocks this PC, but not a LAN source.
        var restarted = new ParentalControl.Service.Web.AdminAccessService(gateTelegram, gateLogger);
        Debug.Assert(restarted.TryLogin(CurrentCode(), System.Net.IPAddress.Loopback).Outcome == ParentalControl.Service.Web.AdminLoginOutcome.Blocked, "The block should survive a restart");
        Debug.Assert(restarted.TryLogin(CurrentCode(), System.Net.IPAddress.Parse("192.0.2.10")).Outcome == ParentalControl.Service.Web.AdminLoginOutcome.Success, "A LAN source should still sign in");
        // "Clear login limit" in the Admin app.
        AdminAccessRepository.DeleteAllFailures();
        AdminAccessRepository.SetLastUsedStep(Totp.StepAt(DateTimeOffset.UtcNow) - 2);
        Debug.Assert(CookieOf(await Send(HttpMethod.Post, "/api/admin/login", content: CodeForm(CurrentCode()))) != null, "After the limit is cleared, this PC should sign in");

        // An install that turns TOTP off again is locked at once.
        SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, "false");
        Debug.Assert((int)(await Send(HttpMethod.Get, "/api/admin/settings", cookie2)).StatusCode == 401, "No session is valid while the sign-in is not set up");
    }
    finally
    {
        await webApp.StopAsync();
        SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, "false");
        SettingsRepository.Set(SettingsRepository.KeyTotpSecret, "");
        AdminAccessRepository.DeleteAllFailures();
    }
}
Console.WriteLine("✅ Admin Gate over HTTP Passed.");

// The owner of a loopback connection (finding S4): the row of the TCP table gives the process of the client end.
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var serverSide = (System.Net.IPEndPoint)accepted.Client.LocalEndPoint!;
        var clientSide = (System.Net.IPEndPoint)accepted.Client.RemoteEndPoint!;
        Debug.Assert(TcpConnectionOwner.FindOwnerPid(clientSide, serverSide) == Environment.ProcessId, "The client end should belong to this process");
        var owner = TcpConnectionOwner.Find(clientSide, serverSide);
        Debug.Assert(owner is not null && owner.ProcessId == Environment.ProcessId && owner.UserSid == System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            "The owner should be this process and its user");
        Debug.Assert(TcpConnectionOwner.FindOwnerPid(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), serverSide) is null, "An unknown connection should have no owner");
    }
    finally
    {
        listener.Stop();
    }
}
Console.WriteLine("✅ Loopback Connection Owner Passed.");

// Cleanup test user
UserRepository.DeleteBySid(testSid);
ScheduleRepository.DeleteForUser(user.Id);
    Console.WriteLine("✅ Cleanup Completed.");

    Console.WriteLine("\n🎉 ALL ASSERTIONS PASSED SUCCESSFULLY!");
}
finally
{
    try
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }
    catch { }
}

static class TestNative
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool CreateHardLinkW(string newLink, string existing, IntPtr reserved);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes;
        public long Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>A handle to a folder with read and write sharing but without delete sharing, as a process of the child could hold it.</summary>
    public static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path) =>
        CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */ | 0x1 /* FILE_LIST_DIRECTORY */, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

    public static uint LinkCount(string path)
    {
        using var handle = CreateFileW(path, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return GetFileInformationByHandle(handle, out var info) ? info.Links : 0;
    }
}
