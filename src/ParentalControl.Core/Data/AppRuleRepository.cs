using Microsoft.Data.Sqlite;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>An App Rule with its stored metadata.</summary>
public sealed record AppRuleRow(AppRule Rule, bool IsDefault, DateTime CreatedAtUtc);

/// <summary>The App Rules (table <c>app_rules</c>). The two minute columns are the App Limit; <c>is_blocked</c> is the block.</summary>
public static class AppRuleRepository
{
    internal static string ToDb(RuleMatchType type) => type == RuleMatchType.File ? "file" : "folder";

    private static RuleMatchType? FromDb(string text) => text switch
    {
        "file" => RuleMatchType.File,
        "folder" => RuleMatchType.Folder,
        _ => null,
    };

    public static List<AppRuleRow> GetAllRows()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, match_type, pattern, category, weekday_minutes, weekend_minutes, is_default, created_at, is_blocked
            FROM app_rules ORDER BY id
            """;

        var list = new List<AppRuleRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (FromDb(reader.GetString(1)) is not { } type || !AppCategories.TryParse(reader.GetString(3), out var category))
                continue;
            DailyLimit? appLimit = reader.IsDBNull(4) && reader.IsDBNull(5)
                ? null
                : new DailyLimit(reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5));
            var rule = new AppRule(reader.GetInt64(0), type, reader.GetString(2), category, appLimit, reader.GetInt32(8) != 0);
            DateTime.TryParse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind, out var created);
            list.Add(new AppRuleRow(rule, reader.GetInt32(6) != 0, created));
        }
        return list;
    }

    public static List<AppRule> GetAll() => GetAllRows().Select(r => r.Rule).ToList();

    public static AppRule? GetById(long id) => GetAllRows().FirstOrDefault(r => r.Rule.Id == id)?.Rule;

    /// <summary>
    /// Adds a rule. Throws ArgumentException if the rule is not valid, and InvalidOperationException
    /// if a rule with the same match type and pattern exists. Returns the new id.
    /// </summary>
    public static long Insert(RuleMatchType matchType, string pattern, AppCategory category, DailyLimit? appLimit, bool isBlocked = false)
    {
        var normalized = Validate(matchType, pattern, category, appLimit, isBlocked);

        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_rules (match_type, pattern, category, weekday_minutes, weekend_minutes, is_default, created_at, is_blocked)
            VALUES (@type, @pattern, @category, @weekday, @weekend, 0, @now, @blocked);
            SELECT last_insert_rowid();
            """;
        AddRuleParameters(cmd, matchType, normalized, category, appLimit);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@blocked", isBlocked ? 1 : 0);

        try
        {
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            CategoryConfigCache.Invalidate();
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new InvalidOperationException("A rule for this file name or folder already exists.", ex);
        }
    }

    /// <summary>
    /// Changes a rule. Returns false if no rule has this id. Throws like <see cref="Insert"/>.
    /// A null <paramref name="isBlocked"/> keeps the stored block, and the validation uses the stored block.
    /// </summary>
    public static bool Update(long id, RuleMatchType matchType, string pattern, AppCategory category, DailyLimit? appLimit, bool? isBlocked = null)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();

        bool blocked;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT is_blocked FROM app_rules WHERE id = @id";
            read.Parameters.AddWithValue("@id", id);
            if (read.ExecuteScalar() is not { } stored)
                return false;
            blocked = isBlocked ?? Convert.ToInt32(stored) != 0;
        }

        var normalized = Validate(matchType, pattern, category, appLimit, blocked);

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE app_rules
            SET match_type = @type, pattern = @pattern, category = @category,
                weekday_minutes = @weekday, weekend_minutes = @weekend, is_blocked = @blocked
            WHERE id = @id
            """;
        AddRuleParameters(cmd, matchType, normalized, category, appLimit);
        cmd.Parameters.AddWithValue("@blocked", blocked ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", id);

        try
        {
            var changed = cmd.ExecuteNonQuery() > 0;
            transaction.Commit();
            CategoryConfigCache.Invalidate();
            return changed;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A rule for this file name or folder already exists.", ex);
        }
    }

    public static bool Delete(long id)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM app_rules WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var deleted = cmd.ExecuteNonQuery() > 0;
        CategoryConfigCache.Invalidate();
        return deleted;
    }

    private static string Validate(RuleMatchType matchType, string pattern, AppCategory category, DailyLimit? appLimit, bool isBlocked)
    {
        var error = AppRule.Validate(matchType, pattern, category, appLimit, isBlocked, CategoryRepository.LocalProtectedPrograms());
        if (error is not null)
            throw new ArgumentException(error);
        return AppRule.NormalizePattern(matchType, pattern)!;
    }

    private static void AddRuleParameters(SqliteCommand cmd, RuleMatchType matchType, string pattern, AppCategory category, DailyLimit? appLimit)
    {
        cmd.Parameters.AddWithValue("@type", ToDb(matchType));
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.Parameters.AddWithValue("@category", category.ToString());
        cmd.Parameters.AddWithValue("@weekday", (object?)appLimit?.WeekdayMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@weekend", (object?)appLimit?.WeekendMinutes ?? DBNull.Value);
    }
}
