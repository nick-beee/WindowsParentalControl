using System.Globalization;

namespace ParentalControl.Core.Data;

/// <summary>One row of the parent report: the blocked starts of one program, Restricted User and day.</summary>
public sealed record BlockedStartRow(
    int UserId,
    DateOnly Date,
    string FileName,
    string ExePath,
    int ClosedCount,
    int SuspendedCount,
    DateTime FirstAtUtc,
    DateTime LastAtUtc,
    bool ChildNotified,
    bool ParentNotified);

/// <summary>
/// The blocked starts per Restricted User, program (file name, letter case ignored) and local day
/// (table <c>blocked_starts</c>). The two flags make sure that the child message and the Telegram notice go out
/// one time per row, also after a service restart and if two threads act on the same program.
/// </summary>
public static class BlockedStartRepository
{
    private static string DateToDb(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string TimeToDb(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime TimeFromDb(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>
    /// Adds one close or one suspension to the row of the program and day. A process that does not count
    /// (a sub-process of a browser) still creates the row and updates the path and the last time.
    /// Returns true if the row is new: the caller then writes the event log entry.
    /// </summary>
    public static bool Record(int userId, DateOnly date, string fileName, string exePath, bool closed, bool counted, DateTime nowUtc)
    {
        var closedAdd = counted && closed ? 1 : 0;
        var suspendedAdd = counted && !closed ? 1 : 0;

        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO blocked_starts (user_id, date, file_name, exe_path, closed_count, suspended_count, first_at, last_at)
            VALUES (@user, @date, @file, @path, @closed, @suspended, @now, @now)
            ON CONFLICT (user_id, date, file_name) DO NOTHING
            """;
        insert.Parameters.AddWithValue("@user", userId);
        insert.Parameters.AddWithValue("@date", DateToDb(date));
        insert.Parameters.AddWithValue("@file", fileName);
        insert.Parameters.AddWithValue("@path", exePath);
        insert.Parameters.AddWithValue("@closed", closedAdd);
        insert.Parameters.AddWithValue("@suspended", suspendedAdd);
        insert.Parameters.AddWithValue("@now", TimeToDb(nowUtc));
        var isNew = insert.ExecuteNonQuery() > 0;

        if (!isNew)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE blocked_starts
                SET closed_count = closed_count + @closed, suspended_count = suspended_count + @suspended,
                    exe_path = @path, last_at = @now
                WHERE user_id = @user AND date = @date AND file_name = @file
                """;
            update.Parameters.AddWithValue("@user", userId);
            update.Parameters.AddWithValue("@date", DateToDb(date));
            update.Parameters.AddWithValue("@file", fileName);
            update.Parameters.AddWithValue("@path", exePath);
            update.Parameters.AddWithValue("@closed", closedAdd);
            update.Parameters.AddWithValue("@suspended", suspendedAdd);
            update.Parameters.AddWithValue("@now", TimeToDb(nowUtc));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return isNew;
    }

    /// <summary>Sets <c>child_notified</c>. Returns true only for the one call that changed it: that caller sends the message.</summary>
    public static bool TryMarkChildNotified(int userId, DateOnly date, string fileName) =>
        TryMark("child_notified", userId, date, fileName);

    /// <summary>Sets <c>parent_notified</c>. Returns true only for the one call that changed it: that caller sends the notice.</summary>
    public static bool TryMarkParentNotified(int userId, DateOnly date, string fileName) =>
        TryMark("parent_notified", userId, date, fileName);

    private static bool TryMark(string column, int userId, DateOnly date, string fileName)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE blocked_starts SET {column} = 1 WHERE user_id = @user AND date = @date AND file_name = @file AND {column} = 0";
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@date", DateToDb(date));
        cmd.Parameters.AddWithValue("@file", fileName);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>The rows of one day, the latest first.</summary>
    public static List<BlockedStartRow> GetForDate(DateOnly date)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, date, file_name, exe_path, closed_count, suspended_count, first_at, last_at, child_notified, parent_notified
            FROM blocked_starts WHERE date = @date ORDER BY last_at DESC
            """;
        cmd.Parameters.AddWithValue("@date", DateToDb(date));

        var list = new List<BlockedStartRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BlockedStartRow(
                reader.GetInt32(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                TimeFromDb(reader.GetString(6)),
                TimeFromDb(reader.GetString(7)),
                reader.GetInt32(8) != 0,
                reader.GetInt32(9) != 0));
        }
        return list;
    }

    /// <summary>Deletes the rows before the cutoff day. Returns the number of rows.</summary>
    public static int DeleteOlderThan(DateOnly cutoff)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM blocked_starts WHERE date < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateToDb(cutoff));
        return cmd.ExecuteNonQuery();
    }
}
