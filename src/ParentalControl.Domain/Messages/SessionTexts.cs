using System.Globalization;

namespace ParentalControl.Domain.Messages;

/// <summary>
/// The fixed session messages of the service per Language Preset. The parent cannot edit them.
/// The "id" texts are the texts of the earlier code, letter for letter.
/// </summary>
public static class SessionTexts
{
    private static readonly (string Key, string De, string En, string Id)[] Rows =
    {
        ("logon_title", "Parental Control — Hallo!", "Parental Control — Session Started", "Parental Control — Sesi Aktif"),
        ("logon_body",
            "Hallo, {username}!\n\n• Deine Bildschirmzeit heute: {time}\n• Computerzeit: {start} – {end} Uhr\n\nDu bekommst eine Warnung, bevor deine Zeit um ist.",
            "Hello, {username}!\n\n• Screen time today: {time}\n• Allowed hours: {start} – {end}\n\nYou get a warning before your time ends.",
            "Halo, {username}!\n\n• Jatah Waktu Layar Hari Ini: {time}\n• Jam Yang Diizinkan: {start} – {end}\n\nAnda akan menerima peringatan otomatis sebelum waktu sesi habis."),
        ("duration_hours", "{hours} {hours|Stunde|Stunden}", "{hours} {hours|hour|hours}", "{hours} jam"),
        ("duration_hours_minutes",
            "{hours} {hours|Stunde|Stunden} {minutes} {minutes|Minute|Minuten}",
            "{hours} {hours|hour|hours} {minutes} {minutes|minute|minutes}",
            "{hours} jam {minutes} menit"),
        ("duration_minutes", "{minutes} {minutes|Minute|Minuten}", "{minutes} {minutes|minute|minutes}", "{minutes} menit"),
        ("warn_title", "Parental Control — Bildschirmzeit", "Parental Control — Screen Time Warning", "Parental Control — Screen Time Warning"),
        ("curfew_warn_title", "Parental Control — Computerzeit", "Parental Control — Curfew Warning", "Parental Control — Curfew Warning"),
        ("break_title", "Parental Control — Pause", "Parental Control — Break", "Parental Control — Istirahat"),
        ("lock_title", "Parental Control — Computer wird gesperrt", "Parental Control — Screen Lock", "Parental Control — Layar Dikunci"),
        ("lock_countdown",
            "(Der Computer wird in {seconds} {seconds|Sekunde|Sekunden} gesperrt.)",
            "(The computer locks in {seconds} {seconds|second|seconds}.)",
            "(Layar akan otomatis dikunci dalam {seconds} detik)"),
        ("session_ended", "Deine Eltern beenden jetzt deine Sitzung.", "Administrator has terminated this session.", "Administrator has terminated this session."),
        ("bonus_title", "Parental Control — Mehr Zeit!", "Parental Control — Extra Time Approved!", "Parental Control — Tambahan Waktu Disetujui!"),
        ("bonus_body",
            "Super! Deine Eltern haben dir für heute +{minutes} {minutes|Minute|Minuten} Bildschirmzeit gegeben.",
            "Good news! Your parent approved +{minutes} {minutes|minute|minutes} of screen time for today.",
            "Kabar baik! Administrator telah menyetujui tambahan waktu layar sebesar +{minutes} menit untuk hari ini."),
        ("exception_title", "Parental Control — Neuer Zeitplan", "Parental Control — Schedule Approved!", "Parental Control — Jadwal Disetujui!"),
        ("exception_body",
            "Deine Eltern haben für den {date} einen besonderen Zeitplan erlaubt: {minutes} {minutes|Minute|Minuten}, von {start} bis {end} Uhr.",
            "Your parent approved a special schedule for {date} ({minutes} {minutes|minute|minutes}, {start}–{end}).",
            "Jadwal khusus untuk tanggal {date} ({minutes}m, {start}–{end}) telah disetujui orang tua."),
        ("weekly_title", "Parental Control — Neuer Wochenplan", "Parental Control — Weekly Schedule Approved!", "Parental Control — Jadwal Permanen Disetujui!"),
        ("weekly_body",
            "Deine Eltern haben deinen Plan für jeden {day} geändert: {minutes} {minutes|Minute|Minuten}, von {start} bis {end} Uhr.",
            "Your parent changed the weekly schedule for {day} ({minutes} {minutes|minute|minutes}, {start}–{end}).",
            "Jadwal mingguan hari {day} ({minutes}m, {start}–{end}) telah diperbarui permanen oleh orang tua."),
        ("declined_title", "Parental Control — Anfrage abgelehnt", "Parental Control — Request Declined", "Parental Control — Permintaan Ditolak"),
        ("declined_with_reason", "Deine Eltern haben deine Anfrage abgelehnt: „{reason}“", "Your request was declined: \"{reason}\"", "Permintaan Anda ditolak: \"{reason}\""),
        ("declined",
            "Deine Eltern haben deine Anfrage abgelehnt.",
            "Your parent declined your request for more screen time.",
            "Permintaan tambahan waktu layar Anda ditolak oleh administrator."),
        ("message_title", "Parental Control — Nachricht von deinen Eltern", "Parental Control — Message", "Parental Control — Pesan"),
        ("message_urgent_title", "🚨 Parental Control — Wichtig", "🚨 Parental Control — Urgent", "🚨 Parental Control — Mendesak"),
        ("decline_reason_1", "Mach zuerst deine Hausaufgaben.", "Finish your homework first.", "Selesaikan tugas sekolah terlebih dahulu."),
        ("decline_reason_2", "Es ist Zeit zum Schlafen oder Ausruhen.", "It is time to sleep or rest.", "Sudah waktunya tidur/istirahat."),
        ("decline_reason_3", "Gleich gibt es Essen mit der Familie.", "It is time for a family meal.", "Waktunya makan bersama keluarga."),
        ("decline_reason_4",
            "Heute gibt es weniger Spielzeit, weil du dich nicht an eine Regel gehalten hast.",
            "Play time is limited because a rule was broken.",
            "Waktu bermain dibatasi karena melanggar aturan."),
        ("decline_reason_default", "Deine Eltern haben die Anfrage abgelehnt.", "Your parent declined the request.", "Permintaan ditolak oleh orang tua."),
        ("date_format", "dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yyyy"),
        ("day_monday", "Montag", "Monday", "Senin"),
        ("day_tuesday", "Dienstag", "Tuesday", "Selasa"),
        ("day_wednesday", "Mittwoch", "Wednesday", "Rabu"),
        ("day_thursday", "Donnerstag", "Thursday", "Kamis"),
        ("day_friday", "Freitag", "Friday", "Jumat"),
        ("day_saturday", "Samstag", "Saturday", "Sabtu"),
        ("day_sunday", "Sonntag", "Sunday", "Minggu"),
    };

    private static readonly IReadOnlyDictionary<string, TextSet> Sets = TextCatalog.Build(Rows);

    public static TextSet For(string? preset) => TextCatalog.For(Sets, preset);

    /// <summary>The key of a weekday name, for example "day_monday".</summary>
    public static string DayKey(DayOfWeek day) => "day_" + day.ToString().ToLowerInvariant();

    public static string DayName(TextSet texts, DayOfWeek day) => texts[DayKey(day)];

    public static string FormatDate(TextSet texts, DateOnly date) =>
        date.ToString(texts["date_format"], CultureInfo.InvariantCulture);

    /// <summary>
    /// A duration in words: "45 Minuten" below one hour, "2 Stunden" for whole hours, else "1 Stunde 30 Minuten".
    /// </summary>
    public static string Duration(TextSet texts, int totalMinutes)
    {
        var minutes = Math.Max(0, totalMinutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours == 0) return texts.Format("duration_minutes", ("minutes", rest));
        if (rest == 0) return texts.Format("duration_hours", ("hours", hours));
        return texts.Format("duration_hours_minutes", ("hours", hours), ("minutes", rest));
    }
}
