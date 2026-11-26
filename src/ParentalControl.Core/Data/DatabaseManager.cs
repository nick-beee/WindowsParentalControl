using Microsoft.Data.Sqlite;

namespace ParentalControl.Core.Data;

public static class DatabaseManager
{
    private const string DatabaseFileName = "data.db";
    public const int RetentionDays = 30;
    public const int CapturesRetentionDays = 7;
    public const long MaxCapturesStorageBytes = 500L * 1024 * 1024; // 500 MB

    private static readonly object Gate = new();
    private static string? _checkedDirectory;
    private static bool _serviceStepRequired;

    /// <summary>
    /// The service only, at its start (require-admin-sign-in, CB1): until <see cref="InitializeSecured"/> runs, each use
    /// of <see cref="DataDirectory"/> throws. So no code of the service can open a database in a folder that its own
    /// data folder step did not secure yet (the path of the Admin app, which does not change the rules, is not used).
    /// </summary>
    public static void RequireServiceStep()
    {
        lock (Gate)
            _serviceStepRequired = true;
    }

    /// <summary>
    /// The data folder. The tests set <c>PARENTAL_CONTROL_DATA_DIR</c>. Else the location comes from the admin-only
    /// registry value (<see cref="Platform.DataFolderRegistry"/>, default <c>C:\ProgramData\ParentalControl</c>), and
    /// this process checks the folder with <see cref="Platform.DataFolderGuard.EnsureSafeToOpen"/> before the first use
    /// (require-admin-sign-in, D14). The service sets the folder after its own step (<see cref="InitializeSecured"/>).
    /// </summary>
    /// <exception cref="Platform.DataFolderUnsafeException">The location or the folder is not safe.</exception>
    public static string DataDirectory => CustomDataDirectory ?? EnsureCheckedDirectory();

    public static string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public static string CapturesDirectory => Path.Combine(DataDirectory, "captures");

    public static string ConnectionString => $"Data Source={DatabasePath}";

    /// <summary>The folder of the log files. It is in the data folder, so it has the same owner and rules.</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// True when the owner and the access rules of the data folder are known to be safe: the service set them
    /// at this start, or the tests use their own folder (<c>PARENTAL_CONTROL_DATA_DIR</c>). While it is false,
    /// the Remote Admin Interface is not set up (fail closed).
    /// </summary>
    public static bool DataFolderSecured { get; private set; }

    /// <summary>The result of the data folder step of the service at this start, or null.</summary>
    public static Platform.DataFolderResult? DataFolderResult { get; private set; }

    private static string? CustomDataDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("PARENTAL_CONTROL_DATA_DIR");
            return string.IsNullOrEmpty(custom) ? null : custom;
        }
    }

    /// <summary>
    /// The data folder of this process after the trust check. The Admin app, the test tools and each other process
    /// that is not the service read the location from the registry and run the same check as the Admin app.
    /// </summary>
    private static string EnsureCheckedDirectory()
    {
        lock (Gate)
        {
            if (_checkedDirectory is not null)
                return _checkedDirectory;
            if (_serviceStepRequired)
                throw new Platform.DataFolderUnsafeException("The data folder step of the service has not finished.");
            var reading = Platform.DataFolderRegistry.Default.ReadLocation(Platform.DataFolderRegistry.ProgramDataRoot);
            if (!reading.IsUsable)
                throw new Platform.DataFolderUnsafeException(reading.Error ?? "The location of the data folder is not usable.");
            Platform.DataFolderGuard.EnsureSafeToOpen(reading.Folder!, DatabaseFileName);
            _checkedDirectory = reading.Folder!;
            return _checkedDirectory;
        }
    }

    /// <summary>
    /// The service only: use the folder of its data folder step (<see cref="Platform.DataFolderCoordinator"/>) and
    /// create or migrate the database in it.
    /// </summary>
    /// <param name="secured">
    /// False if the service uses a trusted folder whose rules it could not reset (D14). The limits work, but the Remote
    /// Admin Interface stays locked, because another account can still have read access to the secret.
    /// </param>
    public static void InitializeSecured(string folder, Platform.DataFolderResult? result, bool secured = true)
    {
        lock (Gate)
        {
            _checkedDirectory = folder;
            DataFolderResult = result;
            DataFolderSecured = secured;
        }
        Initialize();
    }

    /// <summary>
    /// Creates or migrates the database. In a process that is not the service, the first use of
    /// <see cref="DataDirectory"/> reads the location from the registry and checks the folder: it creates a missing
    /// folder with the final rules, but it never changes the rules of an existing one.
    /// </summary>
    /// <exception cref="Platform.DataFolderUnsafeException">
    /// The data folder is not safe. The method then does not open the database (fail closed).
    /// </exception>
    public static void Initialize()
    {
        if (CustomDataDirectory is { } custom)
        {
            Directory.CreateDirectory(custom);
            DataFolderSecured = true;
        }
        else
        {
            _ = DataDirectory; // reads the location and runs the trust check once
        }

        if (!Directory.Exists(CapturesDirectory))
        {
            Directory.CreateDirectory(CapturesDirectory);
        }

        using var connection = CreateConnection();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
        pragmaCmd.ExecuteNonQuery();

        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sid TEXT NOT NULL UNIQUE,
                username TEXT NOT NULL,
                is_restricted INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS limits (
                user_id INTEGER PRIMARY KEY,
                daily_minutes INTEGER NOT NULL DEFAULT 120,
                schedule_start TEXT NOT NULL DEFAULT '08:00',
                schedule_end TEXT NOT NULL DEFAULT '22:00',
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS usage (
                user_id INTEGER NOT NULL,
                date TEXT NOT NULL,
                minutes_used INTEGER NOT NULL DEFAULT 0,
                bonus_minutes INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, date),
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS schedule_days (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                day_of_week INTEGER NOT NULL,
                daily_minutes INTEGER NOT NULL DEFAULT 120,
                schedule_start TEXT NOT NULL DEFAULT '08:00',
                schedule_end TEXT NOT NULL DEFAULT '22:00',
                UNIQUE(user_id, day_of_week),
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS grace_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                date TEXT NOT NULL,
                request_type TEXT NOT NULL DEFAULT 'extension',
                target_date TEXT,
                requested_minutes INTEGER NOT NULL,
                requested_start TEXT,
                requested_end TEXT,
                reason TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'PENDING',
                decline_reason TEXT,
                created_at TEXT NOT NULL,
                resolved_at TEXT,
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS schedule_exceptions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                exception_date TEXT NOT NULL,
                daily_minutes INTEGER NOT NULL,
                schedule_start TEXT NOT NULL,
                schedule_end TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(user_id, exception_date),
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                date TEXT NOT NULL,
                process_name TEXT NOT NULL,
                window_title TEXT NOT NULL DEFAULT '',
                minutes INTEGER NOT NULL DEFAULT 0,
                UNIQUE(user_id, date, process_name),
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_activity_hourly (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                date TEXT NOT NULL,
                hour INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                minutes INTEGER NOT NULL DEFAULT 0,
                UNIQUE(user_id, date, hour, process_name),
                FOREIGN KEY (user_id) REFERENCES users(id)
            );

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                user_sid TEXT NOT NULL,
                event_type TEXT NOT NULL,
                details TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS screen_captures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                file_path TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                file_size_bytes INTEGER NOT NULL,
                trigger_type TEXT NOT NULL DEFAULT 'MANUAL',
                admin_ip TEXT,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS pending_telegram_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                notification_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                event_time TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_prompts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_sid TEXT NOT NULL,
                username TEXT NOT NULL,
                message TEXT NOT NULL,
                urgency TEXT NOT NULL DEFAULT 'NORMAL',
                target_display INTEGER NOT NULL DEFAULT -1,
                custom_options TEXT,
                status TEXT NOT NULL DEFAULT 'PENDING',
                response TEXT,
                response_reason TEXT,
                turnaround_seconds INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                answered_at TEXT
            );

            CREATE TABLE IF NOT EXISTS app_categories (
                category TEXT PRIMARY KEY,
                weekday_minutes INTEGER NULL,
                weekend_minutes INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS app_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_type TEXT NOT NULL CHECK (match_type IN ('file', 'folder')),
                pattern TEXT NOT NULL,
                category TEXT NOT NULL,
                weekday_minutes INTEGER NULL,
                weekend_minutes INTEGER NULL,
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                UNIQUE (match_type, pattern COLLATE NOCASE)
            );

            CREATE TABLE IF NOT EXISTS category_usage (
                user_id INTEGER NOT NULL,
                date TEXT NOT NULL,
                category TEXT NOT NULL,
                minutes INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, date, category)
            );

            CREATE TABLE IF NOT EXISTS suspended_processes (
                pid INTEGER NOT NULL,
                start_time_utc TEXT NOT NULL,
                session_id INTEGER NOT NULL,
                user_id INTEGER NOT NULL,
                category TEXT NOT NULL,
                exe_path TEXT NOT NULL,
                suspended_at TEXT NOT NULL,
                PRIMARY KEY (pid, start_time_utc)
            );

            CREATE TABLE IF NOT EXISTS admin_login_failures (
                id INTEGER PRIMARY KEY,
                source TEXT NOT NULL,
                failed_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_admin_login_failures_time ON admin_login_failures (failed_at_utc);

            CREATE TABLE IF NOT EXISTS break_rules (
                user_id INTEGER PRIMARY KEY REFERENCES users(id),
                enabled INTEGER NOT NULL,
                use_minutes INTEGER NOT NULL,
                break_minutes INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS break_state (
                user_id INTEGER PRIMARY KEY REFERENCES users(id),
                continuous_use_minutes INTEGER NOT NULL DEFAULT 0,
                last_use_utc TEXT NULL,
                break_started_utc TEXT NULL,
                break_ends_utc TEXT NULL,
                warned_5 INTEGER NOT NULL DEFAULT 0,
                warned_1 INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events (timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_user_timestamp ON events (user_sid, timestamp);
            CREATE INDEX IF NOT EXISTS idx_grace_user_date ON grace_requests (user_id, date);
            CREATE INDEX IF NOT EXISTS idx_app_usage_user_date ON app_usage (user_id, date);
            CREATE INDEX IF NOT EXISTS idx_app_activity_hourly_date ON app_activity_hourly (user_id, date);
            CREATE INDEX IF NOT EXISTS idx_screen_captures_user_time ON screen_captures (user_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_schedule_exceptions_user_date ON schedule_exceptions (user_id, exception_date);
            CREATE INDEX IF NOT EXISTS idx_pending_telegram_created ON pending_telegram_queue (created_at);
            CREATE INDEX IF NOT EXISTS idx_prompts_user_status ON session_prompts (username, status);
            """;
        schemaCmd.ExecuteNonQuery();

        // Migration: check if bonus_minutes exists in existing usage table
        using var checkColCmd = connection.CreateCommand();
        checkColCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('usage') WHERE name = 'bonus_minutes';";
        var count = Convert.ToInt32(checkColCmd.ExecuteScalar());
        if (count == 0)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE usage ADD COLUMN bonus_minutes INTEGER NOT NULL DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }

        // Migration: check if new columns exist in grace_requests table
        var columnsToAdd = new[]
        {
            ("request_type", "ALTER TABLE grace_requests ADD COLUMN request_type TEXT NOT NULL DEFAULT 'extension';"),
            ("target_date", "ALTER TABLE grace_requests ADD COLUMN target_date TEXT;"),
            ("requested_start", "ALTER TABLE grace_requests ADD COLUMN requested_start TEXT;"),
            ("requested_end", "ALTER TABLE grace_requests ADD COLUMN requested_end TEXT;"),
            ("decline_reason", "ALTER TABLE grace_requests ADD COLUMN decline_reason TEXT;")
        };

        foreach (var (colName, alterSql) in columnsToAdd)
        {
            using var checkCol = connection.CreateCommand();
            checkCol.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('grace_requests') WHERE name = '{colName}';";
            if (Convert.ToInt32(checkCol.ExecuteScalar()) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = alterSql;
                alter.ExecuteNonQuery();
            }
        }

        // Migration: repair legacy inflated app_activity_hourly and app_usage data (> 60m in 1 hour)
        using var repairCheckCmd = connection.CreateCommand();
        repairCheckCmd.CommandText = "SELECT COUNT(*) FROM app_activity_hourly WHERE minutes > 60;";
        var inflatedCount = Convert.ToInt32(repairCheckCmd.ExecuteScalar());
        if (inflatedCount > 0)
        {
            using var repairTrans = connection.BeginTransaction();
            using var repairCmd = connection.CreateCommand();
            repairCmd.Transaction = repairTrans;
            repairCmd.CommandText = """
                UPDATE app_activity_hourly SET minutes = 60 WHERE minutes > 60;
                UPDATE app_usage
                SET minutes = (
                    SELECT COALESCE(SUM(h.minutes), 0)
                    FROM app_activity_hourly h
                    WHERE h.user_id = app_usage.user_id
                      AND h.date = app_usage.date
                      AND h.process_name = app_usage.process_name
                )
                WHERE EXISTS (
                    SELECT 1 FROM app_activity_hourly h2
                    WHERE h2.user_id = app_usage.user_id
                      AND h2.date = app_usage.date
                      AND h2.process_name = app_usage.process_name
                );
                """;
            repairCmd.ExecuteNonQuery();
            repairTrans.Commit();
        }

        // Language Preset and child texts: "de" for a new install; an update keeps the edited texts.
        SettingsRepository.SeedChildMessages(connection);

        // App Categories: default limits for missing categories; default rules one time only.
        CategoryRepository.SeedIfNeeded(connection, CategoryRepository.LocalDefaultRuleFolders());
    }

    public static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout=5000;";
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }
}
