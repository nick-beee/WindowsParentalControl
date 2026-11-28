using System.Diagnostics;
using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Time;
using ParentalControl.Service;

/// <summary>
/// The Windows checks of harden-clock-and-disk: the test switch of the database writes, the usage ledger over a
/// failing database, the registry values of the Control Time Zone, the control time service and the alert outbox.
/// The pure logic has xUnit tests in ParentalControl.Domain.Tests.
/// </summary>
static class ClockAndStorageChecks
{
    public static void Run(User user)
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var today = new DateOnly(2030, 1, 15); // a day without other test rows

        // --- 4.5 The test switch: each write fails, each read works. ---
        Console.WriteLine("\nTesting the write switch and the usage ledger...");
        SettingsRepository.Set("storage_test", "before");
        DatabaseManager.TestBlockWrites = true;
        try
        {
            var threw = false;
            try { SettingsRepository.Set("storage_test", "during"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { threw = true; }
            Debug.Assert(threw, "A write with the test switch should throw");
            Debug.Assert(SettingsRepository.Get("storage_test") == "before", "A read with the test switch should work and see the old value");

            var insertThrew = false;
            try { SuspensionRepository.Insert(new SuspensionRecord(424242, DateTime.UtcNow, 1, user.Id, AppCategory.Games, @"C:\Games\game.exe")); }
            catch (Microsoft.Data.Sqlite.SqliteException) { insertThrew = true; }
            Debug.Assert(insertThrew, "A suspension record should not be stored with the test switch");

            var health = new StorageHealth();
            Debug.Assert(!health.TryWrite(() => SettingsRepository.Set("storage_test", "x"), "test", logger), "TryWrite should return false on a failed write");
            Debug.Assert(health.TakeFailures() == 1, "TryWrite should report one failure");
            Debug.Assert(health.TakeFailures() == 0, "TakeFailures should reset the count");

            // harden-storage-lockdown D1, D2: the switch gives SQLITE_READONLY, so the kind is ReadOnly, not DiskFull, and
            // the report names the operation label, not the text with the path.
            Debug.Assert(!health.TryWrite(() => SettingsRepository.Set("storage_test", "x"), @"suspension record of C:\Games\game.exe", logger, "suspension record"),
                "TryWrite should return false on a failed write");
            var report = health.TakeReport();
            Debug.Assert(report.Count == 1 && report.Last is not null, "TakeReport should give one failure and the last one");
            Debug.Assert(report.Last!.Kind == ParentalControl.Domain.DataFolder.StorageFailureKind.ReadOnly, $"The write switch should be ReadOnly: {report.Last}");
            Debug.Assert(report.Last.Operation == "suspension record", $"The report should name the operation label: {report.Last}");
            Debug.Assert(report.Last.ExceptionType == "SqliteException", $"The report should name the exception type: {report.Last}");
            Debug.Assert(StorageHealth.Classify("limit check", new InvalidOperationException("x")).Kind == ParentalControl.Domain.DataFolder.StorageFailureKind.Internal,
                "An exception that is not a database error should be Internal");
            Debug.Assert(health.TakeReport() == new StorageFailureReport(0, null), "TakeReport should reset the report");

            // 4.1: the ledger keeps the minutes in the Pending Usage while each write fails.
            var ledgerHealth = new StorageHealth();
            var ledger = new UsageLedger(UsageRepository.AddMinutes, CategoryUsageRepository.AddMinutes, ledgerHealth);
            var storedBefore = UsageRepository.GetUsage(user.Id, today)?.MinutesUsed ?? 0;
            ledger.AddAllowance(user.Id, today, 1);
            ledger.AddCategory(user.Id, today, AppCategory.Games, 1);
            ledger.AddAllowance(user.Id, today, 1);
            ledger.AddCategory(user.Id, today, AppCategory.Games, 1);
            Debug.Assert(ledgerHealth.TakeFailures() >= 4, "Each failed store should be reported");
            Debug.Assert(ledger.Pending.AllowanceFor(user.Id, today) == 2, "2 minutes of Allowance Time should be pending");
            Debug.Assert(ledger.AllowanceUsed(user.Id, today, UsageRepository.GetUsage(user.Id, today)) == storedBefore + 2,
                "The reader sum should include the pending minutes");
            Debug.Assert(ledger.CategoryUsage(user.Id, today).GetValueOrDefault(AppCategory.Games) == 2,
                "The Category Usage of a limit check should include the pending minutes");

            DatabaseManager.TestBlockWrites = false;
            ledger.AddAllowance(user.Id, today, 1);
            ledger.AddCategory(user.Id, today, AppCategory.Games, 1);
            Debug.Assert(ledger.Pending.IsEmpty, "The Pending Usage should be empty after a successful write");
            Debug.Assert(UsageRepository.GetUsage(user.Id, today)!.MinutesUsed == storedBefore + 3, "The database should have the minutes of all 3 adds, none two times");
            Debug.Assert(CategoryUsageRepository.GetForUserAndDate(user.Id, today)[AppCategory.Games] == 3, "The Category Usage should have 3 minutes");
            Debug.Assert(ledgerHealth.TakeFailures() == 0, "A successful store should report no failure");

            // The break state: a failed write keeps Continuous Use in memory, so a Break still starts.
            ParentalControl.Core.Data.BreakRepository.SetRule(user.Id, new ParentalControl.Domain.Breaks.BreakRule(true, 45, 10));
            ParentalControl.Core.Data.BreakRepository.SaveState(user.Id, ParentalControl.Domain.Breaks.BreakState.Empty);
            var breaks = new BreakEnforcer(logger);
            DatabaseManager.TestBlockWrites = true;
            breaks.OnMinutesCounted(user, 5);
            Debug.Assert(breaks.GetStatus().Single(b => b.UserId == user.Id).ContinuousUseMinutes == 5,
                "Continuous Use should count while the break state cannot be written");
            Debug.Assert(ParentalControl.Core.Data.BreakRepository.GetState(user.Id).ContinuousUseMinutes == 0, "The database should still have the old state");
            DatabaseManager.TestBlockWrites = false;
            breaks.OnMinutesCounted(user, 1);
            Debug.Assert(ParentalControl.Core.Data.BreakRepository.GetState(user.Id).ContinuousUseMinutes == 6,
                "The next successful write should store the unsaved Continuous Use");

            // CollectMinutes takes the whole minutes out without a write.
            CategoryUsageRepository.ResetAccumulators();
            Debug.Assert(CategoryUsageRepository.CollectMinutes(user.Id, today, AppCategory.Video, 90) == 1, "90 seconds should give 1 whole minute");
            Debug.Assert(!CategoryUsageRepository.GetForUserAndDate(user.Id, today).ContainsKey(AppCategory.Video), "CollectMinutes should not write");
            Debug.Assert(CategoryUsageRepository.CollectMinutes(user.Id, today, AppCategory.Video, 30) == 1, "The rest of 30 seconds should give the next minute");
        }
        finally
        {
            DatabaseManager.TestBlockWrites = false;
        }
        Console.WriteLine("✅ Write switch and usage ledger Passed.");

        // --- 3.1 The registry values, under a test key. ---
        Console.WriteLine("\nTesting the Control Time Zone registry values and the control time service...");
        Debug.Assert(SystemTimeZone.Read() is not null, "Windows should give the key name of the system time zone");
        Debug.Assert(SystemTimeZone.Find("Tokyo Standard Time") is not null, "Windows should know Tokyo Standard Time");
        Debug.Assert(SystemTimeZone.Find("Mars Standard Time") is null, "An unknown ID should give null");
        Debug.Assert(DiskSpace.FreeBytes(Path.GetTempPath()) is > 0, "The free space of the temp volume should be known");

        var key = new DataFolderRegistry(@"SOFTWARE\ParentalControl.Test-" + Guid.NewGuid().ToString("N"));
        var savedClock = ControlTime.Current;
        try
        {
            Debug.Assert(key.ReadControlTimeZone() == new ControlZoneReading(null, null), "An absent key should give no value and no error");
            key.WriteControlTimeZone("Tokyo Standard Time");
            var reading = key.ReadControlTimeZone();
            Debug.Assert(reading.StoredId == "Tokyo Standard Time" && reading.KeyError is null, $"The stored zone should read back from a trusted key: {reading}");
            Debug.Assert(key.ReadLastSystemTimeZone() is null, "No last system zone yet");
            Debug.Assert(key.TryWriteLastSystemTimeZone(new SystemZoneSetting("W. Europe Standard Time", true)), "The last system zone should be written");
            Debug.Assert(key.ReadLastSystemTimeZone() == new SystemZoneSetting("W. Europe Standard Time", true), "The last system zone should read back");
            key.DeleteKey();

            // 3.2 and 3.5: the control time service with a fake system time zone and a fake Telegram.
            var system = new SystemZoneSetting("W. Europe Standard Time", false);
            var sent = new List<string>();
            var windowsEvents = new List<int>();
            var sendOk = true;
            var outbox = new ParentAlertOutbox((_, text) => { if (sendOk) sent.Add(text); return Task.FromResult(sendOk); }, () => "4242", logger);
            var service = new ControlTimeService(key, outbox, logger, () => system, SystemTimeZone.Find,
                (id, _) => windowsEvents.Add(id), new StorageHealth());

            service.Refresh();
            Debug.Assert(key.ReadControlTimeZone().StoredId == "W. Europe Standard Time", "The first start should write the system time zone");
            Debug.Assert(ControlTime.Zone.Id == "W. Europe Standard Time", "The clock should use the written zone");
            Debug.Assert(sent.Count == 1 && sent[0].Contains("W. Europe Standard Time"), "The parent should get one first-start message with the zone");
            service.Refresh();
            Debug.Assert(sent.Count == 1, "A second refresh should send no second first-start message");

            service.CheckSystemZone();
            Debug.Assert(key.ReadLastSystemTimeZone() == system, "The first check should store the system zone without an event");
            var eventsBefore = EventRepository.GetEvents(null, null, null).Count(e => e.EventType == EventType.TIME_ZONE_CHANGE);

            system = new SystemZoneSetting("Tokyo Standard Time", false);
            service.CheckSystemZone();
            var eventsAfter = EventRepository.GetEvents(null, null, null).Count(e => e.EventType == EventType.TIME_ZONE_CHANGE);
            Debug.Assert(eventsAfter == eventsBefore + 1, "A change of the system time zone should write one event");
            Debug.Assert(sent.Count == 2 && sent[1].Contains("Tokyo Standard Time"), "A change should alert the parent");
            Debug.Assert(ControlTime.Zone.Id == "W. Europe Standard Time", "A change of the system time zone should not change the Control Time Zone");
            Debug.Assert(key.ReadLastSystemTimeZone() == system, "The new system zone should be stored after the event");

            system = new SystemZoneSetting("Tokyo Standard Time", true);
            service.CheckSystemZone();
            Debug.Assert(EventRepository.GetEvents(null, null, null).Count(e => e.EventType == EventType.TIME_ZONE_CHANGE) == eventsAfter + 1,
                "A change of the daylight saving flag should write an event");
            Debug.Assert(sent.Count == 2, "A second change on the same day should send no second alert");

            // D1: an unknown ID keeps the last valid zone, writes event 1017 and alerts once.
            key.WriteControlTimeZone("Mars Standard Time");
            service.Refresh();
            Debug.Assert(ControlTime.Zone.Id == "W. Europe Standard Time", "An unknown ID should keep the last valid zone");
            Debug.Assert(windowsEvents.Contains(ControlTimeService.UnknownZoneEventId), "An unknown ID should write event 1017");
            Debug.Assert(key.ReadControlTimeZone().StoredId == "Mars Standard Time", "The service should not write over a present value");
            Debug.Assert(sent.Count == 3, "An unknown ID should alert the parent");
            service.Refresh();
            Debug.Assert(sent.Count == 3, "The alert about an unknown ID should come at most once per day");

            // A change by the parent takes effect at the next refresh.
            key.WriteControlTimeZone("GMT Standard Time");
            service.Refresh();
            Debug.Assert(ControlTime.Zone.Id == "GMT Standard Time", "A new value should be used at the next refresh");

            // The outbox keeps an alert after a failed send and sends it at the next flush.
            sendOk = false;
            outbox.Post("test alert");
            Debug.Assert(outbox.Count == 1, "A failed send should keep the alert in memory");
            sendOk = true;
            outbox.FlushAsync().GetAwaiter().GetResult();
            Debug.Assert(outbox.Count == 0 && sent[^1] == "test alert", "The next flush should send the alert");

            // The capture quota counts from midnight of the Control Time Zone.
            ControlTime.Set(new ControlClock(SystemTimeZone.Find("Tokyo Standard Time")!, () => new DateTime(2026, 11, 26, 15, 30, 0, DateTimeKind.Utc)));
            Debug.Assert(ControlTime.Current.Today == new DateOnly(2026, 11, 27), "15:30 UTC is the next day in Tokyo");
            Debug.Assert(ControlTime.Current.StartOfToday == new DateTime(2026, 11, 27), "The start of the control day should be midnight in Tokyo");
        }
        finally
        {
            ControlTime.Set(savedClock);
            key.DeleteKey();
        }
        Console.WriteLine("✅ Control Time Zone registry values and control time service Passed.");
    }
}
