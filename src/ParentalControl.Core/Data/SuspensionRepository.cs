using System.Globalization;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>
/// The records of the processes that the service suspended (table <c>suspended_processes</c>).
/// The pid and the start time (UTC, full precision) identify a process.
/// </summary>
public static class SuspensionRepository
{
    private static string ToDb(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime FromDb(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    internal static string ToDb(SuspensionReason reason) => reason == SuspensionReason.Blocked ? "blocked" : "limit";

    // A row of stage 1 has "limit". An unknown text reads as Limit, the reason of stage 1.
    private static SuspensionReason ReasonFromDb(string? text) =>
        string.Equals(text, "blocked", StringComparison.Ordinal) ? SuspensionReason.Blocked : SuspensionReason.Limit;

    public static List<SuspensionRecord> GetAll()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pid, start_time_utc, session_id, user_id, category, exe_path, reason FROM suspended_processes ORDER BY suspended_at";

        var list = new List<SuspensionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            AppCategories.TryParse(reader.GetString(4), out var category);
            list.Add(new SuspensionRecord(
                reader.GetInt32(0), FromDb(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), category, reader.GetString(5),
                ReasonFromDb(reader.IsDBNull(6) ? null : reader.GetString(6))));
        }
        return list;
    }

    /// <summary>Writes a record. If a record for the same pid and start time exists, it replaces it.</summary>
    public static void Insert(SuspensionRecord record)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO suspended_processes (pid, start_time_utc, session_id, user_id, category, exe_path, suspended_at, reason)
            VALUES (@pid, @start, @session, @user, @category, @path, @now, @reason)
            """;
        cmd.Parameters.AddWithValue("@pid", record.Pid);
        cmd.Parameters.AddWithValue("@start", ToDb(record.StartTimeUtc));
        cmd.Parameters.AddWithValue("@session", record.SessionId);
        cmd.Parameters.AddWithValue("@user", record.UserId);
        cmd.Parameters.AddWithValue("@category", record.Category.ToString());
        cmd.Parameters.AddWithValue("@path", record.ExePath);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@reason", ToDb(record.Reason));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stores a new reason and category for a record. Returns false if the record does not exist.</summary>
    public static bool UpdateReason(int pid, DateTime startTimeUtc, SuspensionReason reason, AppCategory category)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE suspended_processes SET reason = @reason, category = @category WHERE pid = @pid AND start_time_utc = @start";
        cmd.Parameters.AddWithValue("@reason", ToDb(reason));
        cmd.Parameters.AddWithValue("@category", category.ToString());
        cmd.Parameters.AddWithValue("@pid", pid);
        cmd.Parameters.AddWithValue("@start", ToDb(startTimeUtc));
        return cmd.ExecuteNonQuery() > 0;
    }

    public static bool Delete(int pid, DateTime startTimeUtc)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM suspended_processes WHERE pid = @pid AND start_time_utc = @start";
        cmd.Parameters.AddWithValue("@pid", pid);
        cmd.Parameters.AddWithValue("@start", ToDb(startTimeUtc));
        return cmd.ExecuteNonQuery() > 0;
    }

    public static List<SuspensionRecord> GetForUser(int userId) => GetAll().Where(r => r.UserId == userId).ToList();
}
