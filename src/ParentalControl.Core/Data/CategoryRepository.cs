using Microsoft.Data.Sqlite;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>The Category Limits (table <c>app_categories</c>). NULL minutes = no limit.</summary>
public static class CategoryRepository
{
    public const string KeyCategoriesSeeded = "categories_seeded";

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

        // Default texts of the category messages. A text that exists stays.
        using (var presetCmd = connection.CreateCommand())
        {
            presetCmd.Transaction = transaction;
            presetCmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
            presetCmd.Parameters.AddWithValue("@key", SettingsRepository.KeyLanguagePreset);
            var preset = presetCmd.ExecuteScalar() as string ?? "id";
            var texts = preset.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? SettingsRepository.CategoryMessagesEn
                : SettingsRepository.CategoryMessagesId;
            foreach (var (key, text) in texts)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO app_settings (key, value) VALUES (@key, @value)";
                insert.Parameters.AddWithValue("@key", key);
                insert.Parameters.AddWithValue("@value", text);
                insert.ExecuteNonQuery();
            }
        }

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
