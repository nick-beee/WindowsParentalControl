using System.Globalization;

namespace ParentalControl.Domain.AdminAccess;

/// <summary>
/// The German texts of the admin sign-in for the parent: the web page and the Telegram messages
/// (decision 8 of require-admin-sign-in). They do not follow the language preset.
/// The Telegram texts use Telegram Markdown; the caller escapes values that can contain Markdown characters.
/// </summary>
public static class AdminAccessTexts
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public const string SetupTitle = "Web-Verwaltung gesperrt";
    public const string SetupText = "Die Web-Verwaltung ist gesperrt. Richte die Anmeldung in Parental Control Admin auf diesem PC ein.";
    public const string SetupHint = "Öffne Parental Control Admin, gehe zu Settings und wähle \"Set up sign-in\". Scanne den QR-Code mit deiner Authenticator-App und gib den angezeigten Code ein.";
    public const string InvalidCode = "Ungültiger Code. Bitte versuche es noch einmal.";

    public static string FormatTime(DateTime localTime) => localTime.ToString("dd.MM.yyyy, HH:mm", German) + " Uhr";

    public static string Blocked(LoginLimitScope scope, DateTime blockedUntilLocal) => scope == LoginLimitScope.AllSources
        ? $"Zu viele falsche Codes. Die Anmeldung ist für alle Geräte gesperrt bis {FormatTime(blockedUntilLocal)}."
        : $"Zu viele falsche Codes. Die Anmeldung von diesem Gerät ist gesperrt bis {FormatTime(blockedUntilLocal)}.";

    public static string SourceName(string source) => source switch
    {
        LoginSource.Local => "dieser PC",
        LoginSource.Unknown => "unbekannt",
        _ => source,
    };

    public static string TelegramLoginBlocked(string machineName, string sourceName, LoginLimitScope scope, DateTime blockedUntilLocal)
    {
        var scopeLine = scope == LoginLimitScope.AllSources
            ? $"Alle Geräte sind gesperrt ({LoginThrottle.MaxTotal} falsche Codes in 24 Stunden)."
            : $"Dieses Gerät ist gesperrt ({LoginThrottle.MaxPerSource} falsche Codes in 24 Stunden).";
        return "🚫 *Web-Verwaltung: zu viele falsche Codes*\n\n"
             + $"PC: {machineName}\n"
             + $"Quelle: {sourceName}\n"
             + $"Gesperrt bis: {FormatTime(blockedUntilLocal)}\n\n"
             + scopeLine + "\n"
             + "Wenn du das nicht warst, hat jemand Codes ausprobiert. In Parental Control Admin auf diesem PC kannst du die Sperre aufheben (\"Clear login limit\").";
    }

    public static string TelegramSetupNeeded(string machineName) =>
        "🔐 *Web-Verwaltung gesperrt*\n\n"
        + $"Die Web-Verwaltung von Parental Control auf {machineName} braucht ab jetzt immer eine Anmeldung mit einer Authenticator-App. Bis zur Einrichtung ist sie gesperrt.\n\n"
        + "So richtest du sie ein: Öffne Parental Control Admin auf diesem PC, gehe zu Settings und wähle \"Set up sign-in\". Scanne den QR-Code mit deiner Authenticator-App und gib den angezeigten Code ein.\n\n"
        + "Anfragen für mehr Zeit kannst du weiter hier in Telegram beantworten.\n"
        + "Falls dein altes Geheimnis für andere sichtbar war (zum Beispiel auf einem Foto), richte ein neues ein.";
}
