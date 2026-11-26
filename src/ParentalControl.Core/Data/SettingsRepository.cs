using Microsoft.Data.Sqlite;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Core.Data;

public static class SettingsRepository
{
    public const string KeyTelegramBotToken = "telegram_bot_token";
    public const string KeyTelegramChatId = "telegram_chat_id";
    public const string KeyTelegramNotifySignIn = "telegram_notify_sign_in";
    public const string KeyTelegramNotifySignOut = "telegram_notify_sign_out";
    public const string KeyTelegramNotifyAdminLogon = "telegram_notify_admin_logon";
    public const string KeyTotpSecret = "totp_secret";
    public const string KeyTotpEnabled = "totp_enabled"; // "true" = the setup of the admin sign-in is complete
    public const string KeyTotpLastStep = "totp_last_step"; // the TOTP time step of the last used code (replay guard)
    public const string KeyTotpSetupAt = "totp_setup_at"; // UTC time of the last setup, ISO 8601
    public const string KeyAdminSessionEpoch = "admin_session_epoch"; // Admin Sessions of an earlier epoch are not valid
    public const string KeyAdminSetupNoticeSent = "admin_setup_notice_sent"; // "true" after the one-time setup notice
    public const string KeyDataFolderQuarantineNotified = "data_folder_quarantine_notified"; // the moved folder of the last Telegram alert
    public const string KeyAlertIntervals = "alert_intervals"; // e.g. "15,5,1"
    public const string KeyMaxDailyRequests = "max_daily_requests"; // default: 1
    public const string KeyLanguagePreset = "language_preset"; // a Language Preset: "de", "en" or "id"
    public const string KeyPromptResponsePresets = ChildMessageKeys.PromptResponsePresets;

    // Customizable message keys. The default texts are in ParentalControl.Domain.Messages.MessageCatalog.
    public const string KeyMsgLimitWarn = ChildMessageKeys.LimitWarn;
    public const string KeyMsgCurfewWarn = ChildMessageKeys.CurfewWarn;
    public const string KeyMsgLimitReached = ChildMessageKeys.LimitReached;
    public const string KeyMsgCurfewReached = ChildMessageKeys.CurfewReached;
    public const string KeyMsgLoginDeniedLimit = ChildMessageKeys.LoginDeniedLimit;
    public const string KeyMsgLoginDeniedCurfew = ChildMessageKeys.LoginDeniedCurfew;
    public const string KeyMsgRemoteLock = ChildMessageKeys.RemoteLock;
    public const string KeyMsgBonusGranted = ChildMessageKeys.BonusGranted;
    // App Categories. Placeholders: {name} (category or app), {category}, {minutes}.
    public const string KeyMsgCategoryWarn = ChildMessageKeys.CategoryWarn;
    public const string KeyMsgCategorySuspended = ChildMessageKeys.CategorySuspended;

    public static string Get(string key, string defaultValue = "")
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? defaultValue;
    }

    public static int GetMaxDailyRequests()
    {
        var val = Get(KeyMaxDailyRequests, "1");
        return int.TryParse(val, out var max) && max >= 0 ? max : 1;
    }

    public static bool IsTelegramNotifySignInEnabled()
    {
        return Get(KeyTelegramNotifySignIn, "true") == "true";
    }

    public static bool IsTelegramNotifySignOutEnabled()
    {
        return Get(KeyTelegramNotifySignOut, "true") == "true";
    }

    public static bool IsTelegramNotifyAdminLogonEnabled()
    {
        return Get(KeyTelegramNotifyAdminLogon, "true") == "true";
    }

    public static List<string> GetPromptResponsePresets()
    {
        var raw = GetMessage(KeyPromptResponsePresets);
        return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => !string.IsNullOrEmpty(s))
                  .ToList();
    }

    /// <summary>The stored Language Preset. A missing or unknown value gives the default "de".</summary>
    public static string GetLanguagePreset() => LanguagePreset.Normalize(Get(KeyLanguagePreset));

    public static void Set(string key, string value)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes all keys in one transaction: all keys or none.</summary>
    public static void SetMany(IEnumerable<KeyValuePair<string, string>> values)
    {
        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
            Set(connection, transaction, key, value);
        transaction.Commit();
    }

    internal static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, string> GetAll()
    {
        using var connection = DatabaseManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM app_settings";

        var dict = new Dictionary<string, string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetString(1);
        }
        return dict;
    }

    /// <summary>
    /// Stores the Language Preset and writes its default text for each child message key that the parent did not edit.
    /// Returns the keys that keep an edited text.
    /// </summary>
    public static IReadOnlyList<string> ApplyLanguagePreset(string preset)
    {
        var target = LanguagePreset.Normalize(preset);
        using var connection = DatabaseManager.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var plan = PresetSwitch.Plan(ReadChildMessages(connection, transaction), target);
        Upsert(connection, transaction, KeyLanguagePreset, target);
        foreach (var (key, text) in plan.Writes) Upsert(connection, transaction, key, text);
        transaction.Commit();
        return plan.Kept;
    }

    /// <summary>
    /// Runs at each start (DatabaseManager.Initialize). A database without a preset gets "de" with the rule of a
    /// preset switch. A database with a preset keeps it: a missing key gets the default of the preset, and an
    /// earlier default text gets the current default. No edited text changes.
    /// </summary>
    internal static void SeedChildMessages(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", KeyLanguagePreset);
            var stored = cmd.ExecuteScalar() as string;

            var texts = ReadChildMessages(connection, transaction);
            PresetSwitchPlan plan;
            if (string.IsNullOrWhiteSpace(stored))
            {
                plan = PresetSwitch.Plan(texts, LanguagePreset.Default);
                Upsert(connection, transaction, KeyLanguagePreset, LanguagePreset.Default);
            }
            else
            {
                plan = PresetSwitch.PlanUpdate(texts, LanguagePreset.Normalize(stored));
            }
            foreach (var (key, text) in plan.Writes) Upsert(connection, transaction, key, text);
        }
        transaction.Commit();
    }

    /// <summary>
    /// The text of a child message key with its placeholders filled. A missing, empty or white-space text gives
    /// the default text of the current preset for this key only. It writes nothing.
    /// </summary>
    public static string GetMessage(string key, Dictionary<string, string>? placeholders = null)
    {
        var msg = Get(key);
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = MessageCatalog.Defaults(GetLanguagePreset())[key];
        }
        return TextTemplate.Fill(msg, placeholders);
    }

    private static Dictionary<string, string?> ReadChildMessages(SqliteConnection connection, SqliteTransaction transaction)
    {
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in ChildMessageKeys.All)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            if (cmd.ExecuteScalar() is string value) texts[key] = value;
        }
        return texts;
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }
}
