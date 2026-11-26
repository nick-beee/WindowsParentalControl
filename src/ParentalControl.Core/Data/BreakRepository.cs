using System.Globalization;
using ParentalControl.Domain.Breaks;

namespace ParentalControl.Core.Data;

/// <summary>
/// The Break Rule (table <c>break_rules</c>) and the break state (table <c>break_state</c>) of each Restricted User.
/// A user without a rule row has <see cref="BreakRule.Default"/>. A user without a state row has <see cref="BreakState.Empty"/>.
/// All times are UTC in ISO 8601.
/// </summary>
public static class BreakRepository
{
    private static string? ToDb(DateTime? utc) =>
        utc is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture) : null;

    private static DateTime? FromDb(object value) =>
        value is string text && !string.IsNullOrEmpty(text)
            ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
            : null;

    /// <summary>The stored rule, or the default rule if the user has no row.</summary>
    public static BreakRule GetRule(int userId)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT enabled, use_minutes, break_minutes FROM break_rules WHERE user_id = @user";
        cmd.Parameters.AddWithValue("@user", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return BreakRule.Default;
        return new BreakRule(reader.GetInt32(0) != 0, reader.GetInt32(1), reader.GetInt32(2));
    }

    public static void SetRule(int userId, BreakRule rule)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO break_rules (user_id, enabled, use_minutes, break_minutes, updated_at)
            VALUES (@user, @enabled, @use, @break, @now)
            ON CONFLICT(user_id) DO UPDATE SET
                enabled = @enabled, use_minutes = @use, break_minutes = @break, updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@enabled", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@use", rule.UseMinutes);
        cmd.Parameters.AddWithValue("@break", rule.BreakMinutes);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The stored state, or the empty state if the user has no row.</summary>
    public static BreakState GetState(int userId)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT continuous_use_minutes, last_use_utc, break_started_utc, break_ends_utc, warned_5, warned_1
            FROM break_state WHERE user_id = @user
            """;
        cmd.Parameters.AddWithValue("@user", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return BreakState.Empty;
        return new BreakState(
            reader.GetInt32(0),
            FromDb(reader.GetValue(1)),
            FromDb(reader.GetValue(2)),
            FromDb(reader.GetValue(3)),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0);
    }

    public static void SaveState(int userId, BreakState state)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO break_state (user_id, continuous_use_minutes, last_use_utc, break_started_utc, break_ends_utc, warned_5, warned_1)
            VALUES (@user, @use, @last, @start, @end, @w5, @w1)
            ON CONFLICT(user_id) DO UPDATE SET
                continuous_use_minutes = @use, last_use_utc = @last, break_started_utc = @start,
                break_ends_utc = @end, warned_5 = @w5, warned_1 = @w1
            """;
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@use", state.ContinuousUseMinutes);
        cmd.Parameters.AddWithValue("@last", (object?)ToDb(state.LastUseUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@start", (object?)ToDb(state.BreakStartedUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@end", (object?)ToDb(state.BreakEndsUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@w5", state.Warned5 ? 1 : 0);
        cmd.Parameters.AddWithValue("@w1", state.Warned1 ? 1 : 0);
        cmd.ExecuteNonQuery();
    }
}
