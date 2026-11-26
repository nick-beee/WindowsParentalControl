using System.Globalization;
using Microsoft.Data.Sqlite;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Core.Data;

/// <summary>
/// The stored state of the admin sign-in: the failed logins (table <c>admin_login_failures</c>),
/// the session epoch, the replay guard and the setup. The service and the Admin app share it through SQLite.
/// </summary>
public static class AdminAccessRepository
{
    // A fixed UTC format, so that a text compare in SQL is a time compare.
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static void AddFailure(string source, DateTimeOffset failedAtUtc)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO admin_login_failures (source, failed_at_utc) VALUES (@source, @at)";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@at", Format(failedAtUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The failures of the last 24 hours before <paramref name="nowUtc"/>.</summary>
    public static List<LoginFailure> GetRecentFailures(DateTimeOffset nowUtc)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source, failed_at_utc FROM admin_login_failures WHERE failed_at_utc > @cutoff ORDER BY failed_at_utc";
        cmd.Parameters.AddWithValue("@cutoff", Format(nowUtc - LoginThrottle.Window));

        var list = new List<LoginFailure>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new LoginFailure(reader.GetString(0), Parse(reader.GetString(1))));
        return list;
    }

    public static int DeleteFailuresOfSource(string source)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM admin_login_failures WHERE source = @source";
        cmd.Parameters.AddWithValue("@source", source);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Clears the login limit for all sources ("Clear login limit" in the Admin app).</summary>
    public static int DeleteAllFailures()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM admin_login_failures";
        return cmd.ExecuteNonQuery();
    }

    public static int DeleteFailuresOlderThan24Hours(DateTimeOffset nowUtc)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM admin_login_failures WHERE failed_at_utc <= @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", Format(nowUtc - LoginThrottle.Window));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The current session epoch: 0 if it is not set, null if the stored value is not a number.</summary>
    public static long? GetSessionEpoch()
    {
        var text = SettingsRepository.Get(SettingsRepository.KeyAdminSessionEpoch, "0");
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) ? epoch : null;
    }

    /// <summary>The time step of the last used TOTP code, or <see cref="Totp.NoStepUsed"/>.</summary>
    public static long GetLastUsedStep()
    {
        var text = SettingsRepository.Get(SettingsRepository.KeyTotpLastStep);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step) ? step : Totp.NoStepUsed;
    }

    public static void SetLastUsedStep(long step) =>
        SettingsRepository.Set(SettingsRepository.KeyTotpLastStep, step.ToString(CultureInfo.InvariantCulture));

    /// <summary>Ends all Admin Sessions: the service refuses each session of an earlier epoch at its next request.</summary>
    public static void SignOutAllSessions()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();
        IncrementSessionEpoch(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Saves a confirmed new secret in one transaction: the secret, the step of the confirmation code (so that this
    /// code cannot log in), the setup time and <c>totp_enabled</c>. It also ends all Admin Sessions and clears the
    /// login limit.
    /// </summary>
    public static void CompleteSetup(string secret, long confirmedStep, DateTimeOffset setupAtUtc)
    {
        if (!TotpSecret.IsValid(secret))
            throw new ArgumentException("The secret is not valid.", nameof(secret));

        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();
        SettingsRepository.Set(connection, transaction, SettingsRepository.KeyTotpSecret, TotpSecret.Normalize(secret));
        SettingsRepository.Set(connection, transaction, SettingsRepository.KeyTotpLastStep, confirmedStep.ToString(CultureInfo.InvariantCulture));
        SettingsRepository.Set(connection, transaction, SettingsRepository.KeyTotpSetupAt, setupAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        SettingsRepository.Set(connection, transaction, SettingsRepository.KeyTotpEnabled, AdminSetup.Enabled);
        IncrementSessionEpoch(connection, transaction);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM admin_login_failures";
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>True if the setup is complete: <c>totp_enabled</c> is <c>"true"</c> and the saved secret is valid.</summary>
    public static bool IsCredentialSetUp() =>
        AdminSetup.IsCredentialSetUp(
            SettingsRepository.Get(SettingsRepository.KeyTotpEnabled, "false"),
            SettingsRepository.Get(SettingsRepository.KeyTotpSecret));

    public static DateTimeOffset? GetSetupTime()
    {
        var text = SettingsRepository.Get(SettingsRepository.KeyTotpSetupAt);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    private static void IncrementSessionEpoch(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO app_settings (key, value) VALUES (@key, '1')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """;
        cmd.Parameters.AddWithValue("@key", SettingsRepository.KeyAdminSessionEpoch);
        cmd.ExecuteNonQuery();
    }
}
