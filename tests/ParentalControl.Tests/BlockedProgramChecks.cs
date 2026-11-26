using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Categories;

/// <summary>Self-checks of change add-blocked-programs. Program.cs calls them; a failed Debug.Assert stops the run.</summary>
internal static class BlockedProgramChecks
{
    /// <summary>Tasks 5.1 to 5.6: the data of a block. Runs on the database of the main checks.</summary>
    public static void RunData()
    {
        // 5.6: settings key, event type.
        Debug.Assert(SettingsRepository.IsNotifyBlockedTelegramEnabled(), "The Telegram notice for a blocked program should be on by default");
        SettingsRepository.Set(SettingsRepository.KeyNotifyBlockedTelegram, "0");
        Debug.Assert(!SettingsRepository.IsNotifyBlockedTelegramEnabled(), "The notice setting 0 should be off");
        SettingsRepository.Set(SettingsRepository.KeyNotifyBlockedTelegram, "1");
        Debug.Assert(Enum.GetValues<EventType>()[^1] == EventType.PROGRAM_BLOCKED, "PROGRAM_BLOCKED should be the last event type");

        // 5.4: a new database has the 15 blocked browsers.
        var rules = AppRuleRepository.GetAll();
        var blocked = rules.Where(r => r.IsBlocked).ToList();
        Debug.Assert(blocked.Count == CategoryDefaults.BlockedBrowsers.Count, $"A new database should have 15 blocked rules, has {blocked.Count}");
        Debug.Assert(blocked.All(r => r.MatchType == RuleMatchType.File && r.Category == AppCategory.Unknown), "Default blocked rules should be Unknown file rules");
        Debug.Assert(CategoryConfigCache.Current.Resolver.Resolve(@"C:\Program Files\Mozilla Firefox\firefox.exe").Rule?.IsBlocked == true,
            "The resolver should see the block of firefox.exe");

        // 5.2: insert, update, read of the block. An update without a block value keeps the stored value.
        var id = AppRuleRepository.Insert(RuleMatchType.File, "game2.exe", AppCategory.Games, null, isBlocked: true);
        Debug.Assert(AppRuleRepository.GetById(id)!.IsBlocked, "Inserted rule should have the block");
        AppRuleRepository.Update(id, RuleMatchType.File, "game2.exe", AppCategory.Video, new DailyLimit(30, 30));
        var updated = AppRuleRepository.GetById(id)!;
        Debug.Assert(updated.IsBlocked && updated.Category == AppCategory.Video, "An update without a block value should keep the block");
        AppRuleRepository.Update(id, RuleMatchType.File, "game2.exe", AppCategory.Video, null, isBlocked: false);
        Debug.Assert(!AppRuleRepository.GetById(id)!.IsBlocked, "An update with blocked=false should remove the block");
        var refused = false;
        try { AppRuleRepository.Insert(RuleMatchType.File, "msedgewebview2.exe", AppCategory.Unknown, null, isBlocked: true); }
        catch (ArgumentException) { refused = true; }
        Debug.Assert(refused, "A blocked rule for msedgewebview2.exe should be refused");
        refused = false;
        AppRuleRepository.Update(id, RuleMatchType.File, "game2.exe", AppCategory.Video, null, isBlocked: true);
        try { AppRuleRepository.Update(id, RuleMatchType.File, "game2.exe", AppCategory.System, null); }
        catch (ArgumentException) { refused = true; }
        Debug.Assert(refused, "A change of a blocked rule to System should be refused");
        Debug.Assert(AppRuleRepository.GetById(id)!.Category == AppCategory.Video, "A refused change should keep the rule");
        refused = false;
        try { AppRuleRepository.Insert(RuleMatchType.Folder, Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppCategory.Unknown, null, isBlocked: true); }
        catch (ArgumentException) { refused = true; }
        catch (InvalidOperationException) { refused = true; } // the default System rule has this folder already
        Debug.Assert(refused, "A blocked rule for the Windows folder should be refused");
        AppRuleRepository.Delete(id);

        // 5.3: the reason of a suspension.
        var start = new DateTime(2026, 11, 26, 7, 0, 0, DateTimeKind.Utc).AddTicks(7);
        var rec = new SuspensionRecord(5151, start, 1, 9, AppCategory.Unknown, @"C:\Chrome\chrome.exe", SuspensionReason.Blocked);
        SuspensionRepository.Insert(rec);
        Debug.Assert(SuspensionRepository.GetAll().Single(r => r.Pid == 5151) == rec, "A blocked record should round-trip");
        Debug.Assert(SuspensionRepository.UpdateReason(5151, start, SuspensionReason.Limit, AppCategory.Video), "UpdateReason should find the record");
        var relabeled = SuspensionRepository.GetAll().Single(r => r.Pid == 5151);
        Debug.Assert(relabeled.Reason == SuspensionReason.Limit && relabeled.Category == AppCategory.Video, "UpdateReason should store reason and category");
        SuspensionRepository.Delete(5151, start);

        // 5.5: the parent report.
        var day = new DateOnly(2026, 11, 26);
        var t = new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);
        Debug.Assert(BlockedStartRepository.Record(9, day, "chrome.exe", @"C:\A\chrome.exe", closed: true, counted: true, t), "First record should be new");
        Debug.Assert(!BlockedStartRepository.Record(9, day, "CHROME.EXE", @"C:\B\chrome.exe", closed: true, counted: true, t.AddMinutes(1)), "Second record should not be new (case ignored)");
        BlockedStartRepository.Record(9, day, "chrome.exe", @"C:\B\chrome.exe", closed: true, counted: false, t.AddMinutes(2)); // a sub-process
        BlockedStartRepository.Record(9, day, "chrome.exe", @"C:\B\chrome.exe", closed: false, counted: true, t.AddMinutes(3));
        var row = BlockedStartRepository.GetForDate(day).Single();
        Debug.Assert(row.ClosedCount == 2 && row.SuspendedCount == 1, $"Counts should be 2 closed and 1 suspended, are {row.ClosedCount}/{row.SuspendedCount}");
        Debug.Assert(row.FirstAtUtc == t && row.LastAtUtc == t.AddMinutes(3) && row.ExePath == @"C:\B\chrome.exe", "Times and the last path should be stored");
        Debug.Assert(BlockedStartRepository.TryMarkChildNotified(9, day, "chrome.exe"), "First child mark should succeed");
        Debug.Assert(!BlockedStartRepository.TryMarkChildNotified(9, day, "Chrome.exe"), "Second child mark should fail");
        Debug.Assert(BlockedStartRepository.TryMarkParentNotified(9, day, "chrome.exe"), "First parent mark should succeed");
        Debug.Assert(!BlockedStartRepository.TryMarkParentNotified(9, day, "chrome.exe"), "Second parent mark should fail");
        Debug.Assert(BlockedStartRepository.Record(9, day.AddDays(-40), "opera.exe", @"C:\O\opera.exe", true, true, t.AddDays(-40)), "Old row should be new");
        Debug.Assert(BlockedStartRepository.DeleteOlderThan(day.AddDays(-DatabaseManager.RetentionDays)) == 1, "Cleanup should delete the old row only");
        Debug.Assert(BlockedStartRepository.GetForDate(day).Count == 1, "Today's row should stay");

        // 5.1 and 5.4: a database of stage 1 gets both columns, keeps its rows, and gets the browser seed one time.
        CheckStageOneMigration();
        Console.WriteLine("✅ Blocked Programs: Data Passed.");
    }

    /// <summary>Tasks 6.2 and 6.3: the close with the pid and start time check, the command line and the parent pid.</summary>
    public static void RunProcess()
    {
        using var helper = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        Thread.Sleep(500);
        var (_, helperStart) = ProcessControl.QueryPathAndStart(helper.Id);
        Debug.Assert(helperStart is not null, "Helper start time should be readable");

        var commandLine = ProcessControl.QueryCommandLine(helper.Id);
        Debug.Assert(commandLine is not null && commandLine.Contains("-n 30"), $"Command line should contain -n 30: {commandLine}");
        Debug.Assert(ProcessControl.QueryParentPid(helper.Id) == Environment.ProcessId, "The parent pid of the helper should be the test process");
        Debug.Assert(SilentStartClassifier.Classify(commandLine) == default, "A ping command line should not be silent");

        Debug.Assert(ProcessControl.Terminate(helper.Id, helperStart!.Value.AddTicks(1), out _) == ProcessOpResult.NotFound,
            "Terminate with a wrong start time should report NotFound");
        Thread.Sleep(200);
        Debug.Assert(!helper.HasExited, "The helper should still run after a refused Terminate");
        Debug.Assert(ProcessControl.Terminate(helper.Id, helperStart.Value, out var detail) == ProcessOpResult.Ok, "Terminate should succeed: " + detail);
        Debug.Assert(helper.WaitForExit(5000), "The helper should end after Terminate");
        Debug.Assert(ProcessControl.Terminate(helper.Id, helperStart.Value, out _) != ProcessOpResult.Ok, "Terminate of an ended process should not report Ok");
        Debug.Assert(ProcessControl.QueryCommandLine(helper.Id) is null || helper.HasExited, "An ended process has no readable command line");
        Console.WriteLine("✅ Blocked Programs: Terminate, Command Line & Parent Pid Passed.");
    }

    /// <summary>
    /// Task 7.2 (part): the WMI watcher gets the start of a process with its pid, its parent pid and its session.
    /// Needs administrator rights (the CI runner and the VM have them).
    /// </summary>
    public static void RunWatcher()
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var watcher = new ParentalControl.Service.ProcessStartWatcher(_ => true, logger);
        Debug.Assert(watcher.EnsureRunning(), "The WMI process start watcher should start (administrator rights needed)");
        Thread.Sleep(1000); // the WMI subscription needs a moment

        using var helper = Process.Start(new ProcessStartInfo("ping.exe", "-n 5 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        using var self = Process.GetCurrentProcess();

        ParentalControl.Service.ProcessStartEvent? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (found is null && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            try
            {
                var ev = watcher.Events.ReadAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                if (ev.Pid == helper.Id)
                    found = ev;
            }
            catch (OperationCanceledException) { break; }
        }

        Debug.Assert(found is not null, "The watcher should report the start of the helper within 15 seconds");
        Debug.Assert(found!.Value.ParentPid == Environment.ProcessId, $"The parent pid should be the test process: {found.Value.ParentPid}");
        Debug.Assert(found.Value.SessionId == self.SessionId, $"The session should be the session of the test: {found.Value.SessionId}");
        helper.WaitForExit(10000);
        Console.WriteLine("✅ Blocked Programs: WMI Process Start Watcher Passed.");
    }

    private static void CheckStageOneMigration()
    {
        const string envName = "PARENTAL_CONTROL_DATA_DIR";
        var mainDir = Environment.GetEnvironmentVariable(envName);
        var oldDir = Path.Combine(Path.GetTempPath(), "ParentalControlStage1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldDir);
        try
        {
            Environment.SetEnvironmentVariable(envName, oldDir);
            using (var c = new SqliteConnection($"Data Source={Path.Combine(oldDir, "data.db")};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE app_rules (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        match_type TEXT NOT NULL CHECK (match_type IN ('file', 'folder')),
                        pattern TEXT NOT NULL, category TEXT NOT NULL,
                        weekday_minutes INTEGER NULL, weekend_minutes INTEGER NULL,
                        is_default INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
                        UNIQUE (match_type, pattern COLLATE NOCASE));
                    CREATE TABLE suspended_processes (
                        pid INTEGER NOT NULL, start_time_utc TEXT NOT NULL, session_id INTEGER NOT NULL, user_id INTEGER NOT NULL,
                        category TEXT NOT NULL, exe_path TEXT NOT NULL, suspended_at TEXT NOT NULL,
                        PRIMARY KEY (pid, start_time_utc));
                    INSERT INTO app_settings (key, value) VALUES ('categories_seeded', '2026-11-01T00:00:00Z');
                    INSERT INTO app_rules (match_type, pattern, category, weekday_minutes, weekend_minutes, is_default, created_at)
                        VALUES ('file', 'chrome.exe', 'Video', 60, 90, 0, '2026-11-01T00:00:00Z');
                    INSERT INTO suspended_processes VALUES (77, '2026-11-25T07:00:00.0000000Z', 1, 2, 'Games', 'C:\Games\g.exe', '2026-11-25T07:01:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            DatabaseManager.Initialize();
            var chrome = AppRuleRepository.GetAll().Single(r => r.Pattern == "chrome.exe");
            Debug.Assert(chrome.IsBlocked && chrome.Category == AppCategory.Video && chrome.AppLimit == new DailyLimit(60, 90),
                "The existing chrome.exe rule should get the block and keep Video and its App Limit");
            Debug.Assert(AppRuleRepository.GetAll().Count(r => r.IsBlocked) == 15, "An existing database should get the 15 blocked rules");
            var oldRecord = SuspensionRepository.GetAll().Single();
            Debug.Assert(oldRecord.Pid == 77 && oldRecord.Reason == SuspensionReason.Limit, "A record of stage 1 should read as Limit");

            // A deleted default stays deleted, and a removed block stays removed.
            var opera = AppRuleRepository.GetAll().Single(r => r.Pattern == "opera.exe");
            AppRuleRepository.Delete(opera.Id);
            AppRuleRepository.Update(chrome.Id, RuleMatchType.File, "chrome.exe", AppCategory.Video, chrome.AppLimit, isBlocked: false);
            DatabaseManager.Initialize();
            Debug.Assert(AppRuleRepository.GetAll().All(r => r.Pattern != "opera.exe"), "A deleted default should stay deleted");
            Debug.Assert(!AppRuleRepository.GetAll().Single(r => r.Pattern == "chrome.exe").IsBlocked, "A second start should not block chrome.exe again");
            Debug.Assert(AppRuleRepository.GetAll().Count(r => r.IsBlocked) == 13, "A second start should add nothing");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, mainDir);
            SqliteConnection.ClearAllPools();
            CategoryConfigCache.Invalidate();
            try { Directory.Delete(oldDir, true); } catch { }
        }
    }
}
