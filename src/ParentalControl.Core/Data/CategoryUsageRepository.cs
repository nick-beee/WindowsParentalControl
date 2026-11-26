using System.Collections.Concurrent;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>
/// The Category Usage (table <c>category_usage</c>): whole minutes per user, day and category.
/// Seconds collect in memory and go to the table as whole minutes, like <see cref="AppUsageRepository"/>.
/// </summary>
public static class CategoryUsageRepository
{
    private sealed class Accumulator
    {
        public int Seconds;
        public DateOnly Date;
    }

    private static readonly ConcurrentDictionary<(int UserId, AppCategory Category), Accumulator> Accumulators = new();

    public static void ResetAccumulators() => Accumulators.Clear();

    /// <summary>Adds seconds of usage. System is never counted. Returns the whole minutes written now.</summary>
    public static int AddSeconds(int userId, DateOnly date, AppCategory category, int seconds)
    {
        var minutes = CollectMinutes(userId, date, category, seconds);
        if (minutes > 0)
            AddMinutes(userId, date, category, minutes);
        return minutes;
    }

    /// <summary>
    /// Adds seconds to the accumulator and takes out the whole minutes, without a write. The service gives them to its
    /// usage ledger, so a failed write keeps them in the Pending Usage (storage-failure D6). System is never counted.
    /// </summary>
    public static int CollectMinutes(int userId, DateOnly date, AppCategory category, int seconds)
    {
        if (category == AppCategory.System || seconds <= 0)
            return 0;

        var acc = Accumulators.GetOrAdd((userId, category), _ => new Accumulator());
        int minutes;
        lock (acc)
        {
            if (acc.Date != date)
            {
                acc.Date = date;
                acc.Seconds = 0;
            }

            acc.Seconds += seconds;
            minutes = acc.Seconds / 60;
            acc.Seconds %= 60;
        }

        return minutes;
    }

    public static void AddMinutes(int userId, DateOnly date, AppCategory category, int minutes)
    {
        if (category == AppCategory.System || minutes <= 0)
            return;

        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO category_usage (user_id, date, category, minutes)
            VALUES (@userId, @date, @category, @minutes)
            ON CONFLICT(user_id, date, category) DO UPDATE SET minutes = minutes + @minutes
            """;
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@category", category.ToString());
        cmd.Parameters.AddWithValue("@minutes", minutes);
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<AppCategory, int> GetForUserAndDate(int userId, DateOnly date)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT category, minutes FROM category_usage WHERE user_id = @userId AND date = @date";
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));

        var usage = new Dictionary<AppCategory, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (AppCategories.TryParse(reader.GetString(0), out var category))
                usage[category] = reader.GetInt32(1);
        }
        return usage;
    }

    public static int DeleteOlderThan(DateOnly cutoff)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_usage WHERE date < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));
        return cmd.ExecuteNonQuery();
    }
}
