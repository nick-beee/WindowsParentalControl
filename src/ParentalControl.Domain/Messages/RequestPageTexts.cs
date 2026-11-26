namespace ParentalControl.Domain.Messages;

/// <summary>
/// The texts of the child request page (<c>/request</c>) and of <c>/api/request</c> per Language Preset.
/// The parent cannot edit them. The "id" texts are the texts of the earlier page, letter for letter.
/// A text is plain text: the page encodes it for HTML or JS.
/// </summary>
public static class RequestPageTexts
{
    private static readonly (string Key, string De, string En, string Id)[] Rows =
    {
        ("page_title", "Parental Control — Mehr Zeit & Zeitplan", "Parental Control — Time & Schedule Requests", "Parental Control — Minta Waktu & Jadwal"),
        ("heading", "🎮 Bildschirmzeit", "🎮 Screen Time", "🎮 Waktu Layar"),
        ("account", "Konto:", "Account:", "Akun:"),
        ("session_inactive", "⏹ Nicht angemeldet (Zeit läuft nicht)", "⏹ Session Inactive (Time Paused)", "⏹ Sesi Tidak Aktif (Waktu Dijeda)"),
        ("session_locked", "⏸ Gesperrt (Zeit läuft nicht)", "⏸ Session Locked (Time Paused)", "⏸ Sesi Terkunci (Waktu Dijeda)"),
        ("session_active", "▶ Zeit läuft", "▶ Session Active", "▶ Sesi Aktif"),
        ("timer_label", "Deine Restzeit heute", "Time Left Today", "Sisa Waktu Penggunaan Hari Ini"),
        ("timer_curfew", "Computerzeit:", "Allowed hours:", "Jadwal Jam Malam:"),
        ("timer_over", "(vorbei)", "(over)", "(Habis)"),
        ("offline",
            "⚠️ Der Computer ist offline. Du kannst eine Anfrage nur mit Internet senden.",
            "⚠️ The computer is offline. You can send a request only with an internet connection.",
            "⚠️ Komputer sedang offline. Permintaan hanya dapat dikirim saat terhubung ke internet."),
        ("status_pending", "Wartet auf Antwort", "Waiting for Approval", "Menunggu Persetujuan"),
        ("status_approved", "Erlaubt", "Approved", "Disetujui"),
        ("status_declined", "Abgelehnt", "Declined", "Ditolak"),
        ("requests_disabled",
            "⚠️ Deine Eltern haben Anfragen für mehr Zeit ausgeschaltet.",
            "⚠️ Requests for extra time are turned off by the administrator.",
            "⚠️ Permintaan waktu tambahan saat ini dinonaktifkan oleh administrator."),
        ("request_pending",
            "ℹ️ Deine Anfrage wartet noch auf eine Antwort (Status: {status}). Warte bitte, bevor du eine neue sendest.",
            "ℹ️ You still have a request that waits for a decision (Status: {status}). Please wait for the decision before you send another one.",
            "ℹ️ Anda masih memiliki permintaan yang sedang menunggu keputusan administrator (Status: {status}). Harap tunggu keputusan sebelum mengirim lagi."),
        ("request_max_reached",
            "ℹ️ Du hast heute schon {max} {max|Anfrage|Anfragen} gesendet. Mehr geht heute nicht (letzter Status: {status}).",
            "ℹ️ You reached the maximum of {max} {max|request|requests} for today (Last status: {status}).",
            "ℹ️ Anda telah mencapai batas maksimal ({max}) permintaan untuk hari ini (Status terakhir: {status})."),
        ("request_used",
            "ℹ️ Du hast heute {count} von {max} {max|Anfrage|Anfragen} benutzt (letzter Status: {status}).",
            "ℹ️ You used {count} of {max} {max|request|requests} today (Last status: {status}).",
            "ℹ️ Anda telah menggunakan {count} dari {max} permintaan hari ini (Status terakhir: {status})."),
        ("decline_title", "Deine Eltern haben abgelehnt{time}", "Decline Note from Your Parent{time}", "Catatan Penolakan dari Orang Tua{time}"),
        ("schedule_title", "📅 Dein Wochenplan", "📅 Weekly Play Schedule", "📅 Jadwal Main Mingguan"),
        ("schedule_default",
            "Standard: {minutes} {minutes|Minute|Minuten} ({start}–{end} Uhr)",
            "Default: {minutes} {minutes|minute|minutes} ({start}–{end})",
            "Standar: {minutes}m ({start}–{end})"),
        ("dot_exception", "Besonderer Tag", "Special Exception", "Pengecualian Khusus"),
        ("dot_custom", "Eigener Plan", "Custom Schedule", "Jadwal Khusus"),
        ("badge_standard", "Standard", "Default", "Standar"),
        ("badge_custom", "Eigener Plan", "Custom", "Jadwal Khusus"),
        ("badge_exception", "Ausnahme", "Exception", "Pengecualian"),
        ("today_suffix", "(heute)", "(Today)", "(Hari Ini)"),
        ("exception_today", "Ausnahme für heute", "Exception for today", "Pengecualian Hari Ini"),
        ("metric_quota", "Bildschirmzeit", "Screen Time", "Kuota Waktu Layar"),
        ("metric_window", "Computerzeit", "Allowed Hours", "Rentang Jam Main"),
        ("minutes_with_duration",
            "{minutes} {minutes|Minute|Minuten} ({duration})",
            "{minutes} {minutes|minute|minutes} ({duration})",
            "{minutes} Menit ({duration})"),
        ("tab_extension", "⏱️ Mehr Zeit für heute", "⏱️ More Time Today", "⏱️ Tambah Waktu Hari Ini"),
        ("tab_schedule", "📅 Plan für einen anderen Tag", "📅 Plan a Future Schedule", "📅 Rencana Jadwal Mendatang"),
        ("ext_choose", "Wie viel mehr Zeit?", "Choose extra time:", "Pilih Tambahan Waktu:"),
        ("ext_reason_label", "Warum brauchst du mehr Zeit?", "Why do you need more time?", "Alasan meminta tambahan waktu:"),
        ("ext_reason_placeholder",
            "Zum Beispiel: Ich mache gerade Hausaufgaben, ich spiele mit Freunden ...",
            "For example: I am finishing my homework, I play a game with friends...",
            "Contoh: Sedang menyelesaikan tugas sekolah, bermain game bersama teman..."),
        ("ext_submit", "Anfrage an meine Eltern senden", "Send Request to Parent", "Kirim Permintaan ke Orang Tua"),
        ("ext_sending", "Anfrage wird gesendet ...", "Sending request...", "Mengirim Permintaan..."),
        ("ext_failed", "Die Anfrage hat nicht geklappt.", "Could not send the request.", "Gagal mengirim permintaan."),
        ("sched_date", "Für welchen Tag?", "Target date:", "Tanggal Sasaran:"),
        ("sched_minutes", "Wie viel Bildschirmzeit?", "Total time wanted:", "Kuota Total Diinginkan:"),
        ("sched_start", "Ab wann?", "Start time:", "Jam Boleh Mulai:"),
        ("sched_end", "Bis wann?", "End time (evening):", "Jam Selesai (Malam):"),
        ("sched_reason_label", "Warum brauchst du diesen Plan?", "Reason for the schedule change:", "Alasan perubahan jadwal mendatang:"),
        ("sched_reason_placeholder",
            "Zum Beispiel: Feiertag, online mit Freunden lernen ...",
            "For example: a public holiday, an online study group...",
            "Contoh: Hari libur nasional, rencana belajar kelompok online, dll..."),
        ("sched_submit", "Plan an meine Eltern senden", "Send Schedule Request to Parent", "Ajukan Perubahan Jadwal ke Orang Tua"),
        ("sched_sending", "Plan wird gesendet ...", "Sending schedule...", "Mengajukan Jadwal..."),
        ("sched_failed", "Der Plan hat nicht geklappt.", "Could not send the schedule request.", "Gagal mengajukan jadwal."),
        ("connect_failed", "Keine Verbindung zu Parental Control.", "Could not connect to the parental control service.", "Gagal terhubung ke layanan parental control."),
        ("api_invalid_schedule",
            "Die Minuten sind ungültig (1–480) oder der Grund fehlt.",
            "The number of minutes is not valid (1–480 minutes), or the reason is missing.",
            "Jumlah menit tidak valid (1–480 menit) atau alasan harus diisi."),
        ("api_invalid_extension",
            "Die Minuten sind ungültig (1–120) oder der Grund fehlt.",
            "The number of minutes is not valid (1–120 minutes), or the reason is missing.",
            "Jumlah menit tidak valid (1–120 menit) atau alasan harus diisi."),
        ("api_no_user", "Kein eingeschränktes Konto gefunden.", "No restricted user found.", "Pengguna terbatas tidak ditemukan."),
        ("api_disabled",
            "Deine Eltern haben Anfragen für mehr Zeit ausgeschaltet.",
            "Requests for extra time are turned off by the administrator.",
            "Permintaan tambahan waktu dinonaktifkan oleh administrator."),
        ("api_pending",
            "Deine letzte Anfrage wartet noch auf eine Antwort.",
            "You still have a request that waits for a decision.",
            "Anda masih memiliki permintaan yang sedang menunggu keputusan administrator."),
        ("api_max_reached",
            "Du hast heute schon {max} {max|Anfrage|Anfragen} gesendet.",
            "You reached the maximum of {max} {max|request|requests} for today.",
            "Anda telah mencapai batas maksimal ({max}) permintaan untuk hari ini."),
        ("api_invalid_date", "Das Datum ist ungültig.", "The target date is not valid.", "Tanggal sasaran tidak valid."),
        ("api_schedule_sent",
            "Dein Plan für den {date} ({minutes} {minutes|Minute|Minuten}) ist bei deinen Eltern.",
            "Your schedule request for {date} ({minutes} {minutes|minute|minutes}) was sent to your parent.",
            "Pengajuan jadwal baru untuk {date} ({minutes}m) berhasil dikirim ke orang tua."),
        ("api_extension_sent",
            "Deine Anfrage für +{minutes} {minutes|Minute|Minuten} ist bei deinen Eltern.",
            "Your request for +{minutes} {minutes|minute|minutes} was sent to your parent.",
            "Permintaan tambahan waktu +{minutes} menit berhasil dikirim ke orang tua."),
        ("api_invalid", "Die Anfrage ist ungültig.", "The request is not valid.", "Permintaan tidak valid."),
        ("code_monday", "Mo", "Mon", "Sen"),
        ("code_tuesday", "Di", "Tue", "Sel"),
        ("code_wednesday", "Mi", "Wed", "Rab"),
        ("code_thursday", "Do", "Thu", "Kam"),
        ("code_friday", "Fr", "Fri", "Jum"),
        ("code_saturday", "Sa", "Sat", "Sab"),
        ("code_sunday", "So", "Sun", "Min"),
    };

    private static readonly IReadOnlyDictionary<string, TextSet> Sets = TextCatalog.Build(Rows);

    public static TextSet For(string? preset) => TextCatalog.For(Sets, preset);

    public static string DayCode(TextSet texts, DayOfWeek day) => texts["code_" + day.ToString().ToLowerInvariant()];

    /// <summary>
    /// The minutes of a day or an option: the duration alone below one hour ("45 Minuten"),
    /// else the minutes and the duration ("90 Minuten (1 Stunde 30 Minuten)").
    /// </summary>
    public static string MinutesText(TextSet pageTexts, TextSet sessionTexts, int minutes)
    {
        var duration = SessionTexts.Duration(sessionTexts, minutes);
        return minutes < 60
            ? duration
            : pageTexts.Format("minutes_with_duration", ("minutes", minutes), ("duration", duration));
    }
}
