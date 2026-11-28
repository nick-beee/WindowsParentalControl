using System.Globalization;
using System.Text.Json;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>
/// The running Resume Locks (table <c>resume_locks</c>, one row per Restricted User). The service restores them after a
/// restart of the service or of Windows (lock-after-repeated-resume, design D6). The count of Child Resumes is not stored.
/// </summary>
public static class ResumeLockRepository
{
    private const string DayFormat = "yyyy-MM-dd";

    public static List<ResumeLockState> GetAll()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id, day, bonus_at_start, exe_paths FROM resume_locks ORDER BY user_id";

        var list = new List<ResumeLockState>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // A row that does not parse is skipped: the lock of that user then ends, as after a restart before this change.
            if (!DateOnly.TryParseExact(reader.GetString(1), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;
            List<string>? paths;
            try
            {
                paths = JsonSerializer.Deserialize<List<string>>(reader.GetString(3));
            }
            catch (JsonException)
            {
                continue;
            }
            if (paths is null || paths.Count == 0 || paths.Any(string.IsNullOrEmpty))
                continue;
            list.Add(new ResumeLockState(reader.GetInt32(0), day, reader.GetInt32(2), paths));
        }
        return list;
    }

    /// <summary>Writes the Resume Lock of the user. It replaces a stored lock of the same user.</summary>
    public static void Save(ResumeLockState state)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO resume_locks (user_id, day, bonus_at_start, exe_paths, updated_at)
            VALUES (@user, @day, @bonus, @paths, @now)
            ON CONFLICT(user_id) DO UPDATE SET
                day = @day, bonus_at_start = @bonus, exe_paths = @paths, updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@user", state.UserId);
        cmd.Parameters.AddWithValue("@day", state.Day.ToString(DayFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@bonus", state.BonusAtStart);
        cmd.Parameters.AddWithValue("@paths", JsonSerializer.Serialize(state.ExePaths));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public static bool Delete(int userId)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM resume_locks WHERE user_id = @user";
        cmd.Parameters.AddWithValue("@user", userId);
        return cmd.ExecuteNonQuery() > 0;
    }
}
