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
    public const string LocalClientRefused = "Die Anmeldung aus diesem Windows-Konto ist nicht möglich. Melde dich mit deinem eigenen Windows-Konto auf diesem PC an, oder nutze ein Handy oder einen Laptop im Heimnetz.";

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

    /// <summary>The Telegram alert after the service made a new data folder, because the old one was not usable (D14).</summary>
    public static string TelegramDataFolderRelocated(string machineName, string from, string to, DateTime whenLocal) =>
        "⚠️ *Neuer Datenordner*\n\n"
        + $"Parental Control auf {machineName} konnte am {whenLocal:dd.MM.yyyy} um {whenLocal:HH:mm} Uhr den Datenordner {from} nicht sicher verwenden. "
        + $"Der Dienst hat den neuen Datenordner {to} angelegt und mit einer neuen Datenbank begonnen. Den alten Ordner hat er nicht verändert.\n\n"
        + "Prüfe in Parental Control Admin die Limits, die Regeln und die Anmeldung. Lösche den alten Ordner, wenn du ihn nicht mehr brauchst.";

    /// <summary>The Telegram alert after the service moved an untrusted data folder away (finding B2).</summary>
    public static string TelegramDataFolderQuarantined(string machineName, string movedTo, DateTime whenLocal) =>
        "⚠️ *Datenordner ersetzt*\n\n"
        + $"Parental Control auf {machineName} hat am {whenLocal:dd.MM.yyyy} um {whenLocal:HH:mm} Uhr einen Datenordner gefunden, dem es nicht vertraut: ein anderes Konto konnte ihn ändern. "
        + $"Der Dienst hat ihn nach {movedTo} verschoben und mit einer neuen Datenbank begonnen.\n\n"
        + "Prüfe in Parental Control Admin die Limits, die Regeln und die Anmeldung. Lösche den verschobenen Ordner, wenn du ihn nicht mehr brauchst.";

    /// <summary>
    /// The Telegram alert about a program that holds port 5050 of the web server (third review, SF3). The program text
    /// comes from an exe; the caller escapes it.
    /// </summary>
    public static string TelegramWebPortBlocked(string machineName, string owner, DateTime whenLocal) =>
        "⚠️ *Port der Web-Verwaltung belegt*\n\n"
        + $"Auf {machineName} belegt seit {whenLocal:dd.MM.yyyy} um {whenLocal:HH:mm} Uhr ein anderes Programm den Port 5050 der Web-Verwaltung: {owner}.\n\n"
        + "Eine Seite auf http://localhost:5050 kann von diesem Programm kommen. Gib dort keinen Anmeldecode ein. "
        + "Die Limits gelten weiter, aber die Nutzung der Kategorien zählt nicht, solange das Programm läuft. "
        + "Parental Control beendet das Programm nicht. Schließe es oder starte den PC neu.";
}
