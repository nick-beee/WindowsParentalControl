using System.Globalization;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Breaks;

/// <summary>The Break Rule and the break state of one Restricted User, for Telegram <c>/break</c> and the admin API.</summary>
public sealed record BreakStatus(
    int UserId,
    string Username,
    BreakRule Rule,
    int ContinuousUseMinutes,
    DateTime? BreakStartedUtc,
    DateTime? BreakEndsUtc);

/// <summary>
/// The Telegram texts of the Breaks for the parent: Indonesian for the preset "id", English for each other preset
/// (<see cref="LanguagePreset.ParentLanguage"/>). The caller escapes the user name for Markdown.
/// </summary>
public static class BreakTelegramTexts
{
    private static bool Id(string lang) => lang == LanguagePreset.Id;

    private static string Time(DateTime utc, TimeZoneInfo zone) => BreakTexts.EndText(utc, zone);

    public static string Header(string lang) => Id(lang) ? "⏸️ *Istirahat*" : "⏸️ *Breaks*";

    public static string NoUsers(string lang) =>
        Id(lang) ? "ℹ️ Belum ada akun pengguna terbatas yang terdaftar." : "ℹ️ There is no Restricted User.";

    /// <summary>One line per user: the rule is off, a Break runs until a time, or the minutes until the next Break.</summary>
    public static string Line(BreakStatus status, string name, DateTime nowUtc, TimeZoneInfo zone, string lang)
    {
        if (status.BreakEndsUtc is { } end && nowUtc < end)
            return Id(lang)
                ? $"• {name}: istirahat sampai pukul {Time(end, zone)}"
                : $"• {name}: break until {Time(end, zone)}";

        if (!status.Rule.Enabled)
            return Id(lang) ? $"• {name}: istirahat nonaktif" : $"• {name}: breaks are off";

        var left = Math.Max(0, status.Rule.UseMinutes - status.ContinuousUseMinutes);
        var use = status.ContinuousUseMinutes.ToString(CultureInfo.InvariantCulture);
        var n = status.Rule.UseMinutes.ToString(CultureInfo.InvariantCulture);
        return Id(lang)
            ? $"• {name}: istirahat dalam {left} menit ({use} dari {n} menit pemakaian)"
            : $"• {name}: break in {left} {(left == 1 ? "minute" : "minutes")} ({use} of {n} minutes of use)";
    }

    public static string EndButton(string username, string lang) =>
        Id(lang) ? $"▶️ Akhiri istirahat {username}" : $"▶️ End the break of {username}";

    public static string Ended(string name, string lang) =>
        Id(lang)
            ? $"✅ Istirahat {name} telah diakhiri. Anak dapat membuka kunci komputer sekarang."
            : $"✅ The break of {name} ended. The child can unlock the computer now.";

    public static string AlreadyEnded(string lang) =>
        Id(lang) ? "ℹ️ Istirahat ini sudah berakhir." : "ℹ️ This break already ended.";

    public static string NotReady(string lang) =>
        Id(lang) ? "⚠️ Layanan istirahat belum siap." : "⚠️ The break service is not ready.";

    /// <summary>The notice at a Break start (setting <c>telegram_notify_break</c>).</summary>
    public static string Notice(string name, DateTime startUtc, DateTime endUtc, TimeZoneInfo zone, string lang)
    {
        var minutes = BreakPlanner.MinutesLeft(startUtc, endUtc);
        return Id(lang)
            ? $"⏸️ *Istirahat dimulai*\n{name} istirahat selama {minutes} menit, sampai pukul {Time(endUtc, zone)}."
            : $"⏸️ *Break started*\n{name} has a break of {minutes} {(minutes == 1 ? "minute" : "minutes")}, until {Time(endUtc, zone)}.";
    }

    public static string HelpLine(string lang) =>
        Id(lang)
            ? "• `/break` — Lihat istirahat anak dan akhiri istirahat yang berjalan"
            : "• `/break` — Show the breaks and end a running break";
}
