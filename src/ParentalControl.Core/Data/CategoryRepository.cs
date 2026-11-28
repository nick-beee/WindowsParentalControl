using Microsoft.Data.Sqlite;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>The Category Limits (table <c>app_categories</c>). NULL minutes = no limit.</summary>
public static class CategoryRepository
{
    public const string KeyCategoriesSeeded = "categories_seeded";
    public const string KeyBlockedDefaultsSeeded = "blocked_defaults_seeded";

    public static Dictionary<AppCategory, DailyLimit> GetLimits()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT category, weekday_minutes, weekend_minutes FROM app_categories";

        var limits = AppCategories.All.ToDictionary(c => c, _ => DailyLimit.None);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!AppCategories.TryParse(reader.GetString(0), out var category))
                continue;
            limits[category] = new DailyLimit(
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }
        return limits;
    }

    /// <summary>Sets the limit of a category. Throws ArgumentException for System with a limit or a value out of range.</summary>
    public static void SetLimit(AppCategory category, DailyLimit limit)
    {
        if (!limit.IsValid)
            throw new ArgumentException($"A limit must be from 0 to {DailyLimit.MaxMinutes} minutes.");
        if (category == AppCategory.System && !limit.IsNone)
            throw new ArgumentException("System cannot have a limit.");

        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_categories (category, weekday_minutes, weekend_minutes)
            VALUES (@category, @weekday, @weekend)
            ON CONFLICT(category) DO UPDATE SET weekday_minutes = @weekday, weekend_minutes = @weekend
            """;
        cmd.Parameters.AddWithValue("@category", category.ToString());
        cmd.Parameters.AddWithValue("@weekday", (object?)limit.WeekdayMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@weekend", (object?)limit.WeekendMinutes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        CategoryConfigCache.Invalidate();
    }

    /// <summary>
    /// Creates a row for each category that has none, with its default limit, and never changes a row.
    /// While the setting <see cref="KeyCategoriesSeeded"/> is absent, it also writes the default App Rules
    /// and then sets the key. Thus a default rule that the parent deletes stays deleted.
    /// </summary>
    internal static void SeedIfNeeded(SqliteConnection connection, DefaultRuleFolders folders)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var category in AppCategories.All)
        {
            var limit = CategoryDefaults.Limits[category];
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO app_categories (category, weekday_minutes, weekend_minutes)
                VALUES (@category, @weekday, @weekend)
                """;
            insert.Parameters.AddWithValue("@category", category.ToString());
            insert.Parameters.AddWithValue("@weekday", (object?)limit.WeekdayMinutes ?? DBNull.Value);
            insert.Parameters.AddWithValue("@weekend", (object?)limit.WeekendMinutes ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        // The default texts of the category messages come from SettingsRepository.SeedChildMessages.

        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM app_settings WHERE key = @key";
        check.Parameters.AddWithValue("@key", KeyCategoriesSeeded);
        var seeded = Convert.ToInt32(check.ExecuteScalar()) > 0;

        if (!seeded)
        {
            var now = DateTime.UtcNow.ToString("o");
            foreach (var rule in CategoryDefaults.Rules(folders))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO app_rules (match_type, pattern, category, weekday_minutes, weekend_minutes, is_default, created_at)
                    VALUES (@type, @pattern, @category, NULL, NULL, 1, @now)
                    """;
                insert.Parameters.AddWithValue("@type", AppRuleRepository.ToDb(rule.MatchType));
                insert.Parameters.AddWithValue("@pattern", rule.Pattern);
                insert.Parameters.AddWithValue("@category", rule.Category.ToString());
                insert.Parameters.AddWithValue("@now", now);
                insert.ExecuteNonQuery();
            }

            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO app_settings (key, value) VALUES (@key, @value)";
            mark.Parameters.AddWithValue("@key", KeyCategoriesSeeded);
            mark.Parameters.AddWithValue("@value", DateTime.UtcNow.ToString("o"));
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
        CategoryConfigCache.Invalidate();
    }

    /// <summary>
    /// While the setting <see cref="KeyBlockedDefaultsSeeded"/> is absent, it writes the default blocked browser rules
    /// and turns on the block of an existing rule with the same pattern, then sets the key. All in one transaction.
    /// Thus a default rule that the parent deletes stays deleted, and a block that the parent removes stays removed.
    /// </summary>
    internal static void SeedBlockedBrowsersIfNeeded(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM app_settings WHERE key = @key";
            check.Parameters.AddWithValue("@key", KeyBlockedDefaultsSeeded);
            if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                return; // the transaction rolls back on dispose; it wrote nothing
        }

        var existing = new List<AppRule>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, pattern, category, is_blocked FROM app_rules WHERE match_type = 'file'";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (!AppCategories.TryParse(reader.GetString(2), out var category))
                    category = AppCategory.Unknown;
                existing.Add(new AppRule(reader.GetInt64(0), RuleMatchType.File, reader.GetString(1), category, null, reader.GetInt32(3) != 0));
            }
        }

        var plan = CategoryDefaults.PlanBlockedBrowserSeed(existing);
        var now = DateTime.UtcNow.ToString("o");
        foreach (var rule in plan.ToInsert)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO app_rules (match_type, pattern, category, weekday_minutes, weekend_minutes, is_default, created_at, is_blocked)
                VALUES ('file', @pattern, @category, NULL, NULL, 1, @now, 1)
                """;
            insert.Parameters.AddWithValue("@pattern", rule.Pattern);
            insert.Parameters.AddWithValue("@category", rule.Category.ToString());
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }

        foreach (var id in plan.ToBlock)
        {
            using var block = connection.CreateCommand();
            block.Transaction = transaction;
            block.CommandText = "UPDATE app_rules SET is_blocked = 1 WHERE id = @id";
            block.Parameters.AddWithValue("@id", id);
            block.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO app_settings (key, value) VALUES (@key, @value)";
            mark.Parameters.AddWithValue("@key", KeyBlockedDefaultsSeeded);
            mark.Parameters.AddWithValue("@value", DateTime.UtcNow.ToString("o"));
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
        CategoryConfigCache.Invalidate();
    }

    private static readonly Lazy<ProtectedPrograms> LocalProtected = new(() =>
    {
        // The install folder of the installer, and the folder of this program. The service, the agent and the
        // admin app run from {app}\service, {app}\agent and {app}\admin, so the parent of that folder is {app}.
        var baseDir = AppContext.BaseDirectory;
        var folders = new List<string?> { LocalDefaultRuleFolders().InstallFolder, baseDir };
        var trimmed = baseDir.TrimEnd('\\', '/');
        var last = Path.GetFileName(trimmed);
        if (last is "service" or "agent" or "admin")
            folders.Add(Path.GetDirectoryName(trimmed));
        // The Trusted WebView2 Runtime (suspend-untrusted-webview2, design D1): one instance for the block, the
        // suspension and the counting of the Session Probe.
        var trustedWebView2 = new TrustedWebView2(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        return new ProtectedPrograms(Environment.GetFolderPath(Environment.SpecialFolder.Windows), folders, trustedWebView2);
    });

    /// <summary>The programs of this computer that a block never closes or suspends.</summary>
    public static ProtectedPrograms LocalProtectedPrograms() => LocalProtected.Value;

    /// <summary>The folders of this computer for the default rules.</summary>
    internal static DefaultRuleFolders LocalDefaultRuleFolders()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(programFilesX86))
            programFilesX86 = programFiles;
        return new DefaultRuleFolders(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            programFiles,
            programFilesX86,
            Path.Combine(programFiles, "ParentalControl")); // DefaultDirName in installer/ParentalControl.iss
    }
}
