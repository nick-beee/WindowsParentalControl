namespace ParentalControl.Domain.Messages;

/// <summary>
/// The texts of the Session Agent (widget, tray menu, prompt dialog, full-screen settings) per Language Preset.
/// The parent cannot edit them. The "id" texts are the texts of the earlier agent, letter for letter, except the new texts of each Lock Reason.
/// </summary>
public static class AgentTexts
{
    /// <summary>The limit of <c>NotifyIcon.Text</c>.</summary>
    public const int TrayTooltipMaxLength = 63;

    private static readonly (string Key, string De, string En, string Id)[] Rows =
    {
        ("tray_user", "Konto: {username}", "Account: {username}", "Pengguna: {username}"),
        ("tray_toggle_widget", "⏱ Widget zeigen / verstecken", "⏱ Show / Hide Widget", "⏱ Tampilkan / Sembunyikan Widget"),
        ("tray_fullscreen_settings", "⚙️ Einstellungen für Vollbild", "⚙️ Fullscreen Settings", "⚙️ Pengaturan Layar Penuh"),
        ("tray_request", "🎮 Mehr Bildschirmzeit wünschen...", "🎮 Ask for More Screen Time...", "🎮 Minta Tambahan Waktu Layar..."),
        ("tray_unrestricted", "Parental Control: Konto ohne Grenzen", "Parental Control: Unrestricted Account", "Parental Control: Akun Tidak Dibatasi"),
        // The lock texts per Lock Reason (widget-shows-schedule-lock-after-break). Only the Break text says "Pause".
        ("tray_locked", "Parental Control: Gesperrt (Zeit läuft nicht)", "Parental Control: Locked (time stopped)", "Parental Control: Sesi Dikunci (waktu berhenti)"),
        ("tray_locked_schedule", "Parental Control: Gesperrt (keine Computerzeit)", "Parental Control: Locked (outside allowed hours)", "Parental Control: Terkunci (di luar jam diizinkan)"),
        ("tray_locked_allowance", "Parental Control: Gesperrt (Zeit für heute um)", "Parental Control: Locked (no time left today)", "Parental Control: Terkunci (waktu hari ini habis)"),
        ("tray_locked_break", "Parental Control: Gesperrt (Pause)", "Parental Control: Locked (break)", "Parental Control: Terkunci (istirahat)"),
        ("tray_remaining_hours",
            "Parental Control: noch {hours} Std. {minutes} Min.",
            "Parental Control: {hours}h {minutes}m left",
            "Parental Control: Sisa {hours}j {minutes}m"),
        ("tray_remaining_minutes",
            "Parental Control: noch {minutes} {minutes|Minute|Minuten}",
            "Parental Control: {minutes} {minutes|minute|minutes} left",
            "Parental Control: Sisa {minutes}m"),
        ("widget_remaining_label", "RESTZEIT HEUTE", "TIME LEFT TODAY", "SISA WAKTU HARI INI"),
        ("widget_settings_tooltip", "Einstellungen für Vollbild", "Fullscreen Settings", "Pengaturan Layar Penuh"),
        ("widget_collapse_tooltip", "Widget verkleinern", "Collapse Widget", "Kecilkan Widget"),
        ("widget_expand_tooltip", "Widget vergrößern", "Expand Widget", "Perbesar Widget"),
        ("widget_curfew_tooltip",
            "Die Computerzeit endet vor deiner Bildschirmzeit.",
            "The allowed hours end before your daily time.",
            "Waktu dibatasi oleh Jam Malam, bukan kuota harian"),
        ("widget_curfew_empty", "Computerzeit: --:--", "Allowed hours: --:--", "Jam malam: --:--"),
        ("widget_locked", "⏸ Gesperrt (Zeit läuft nicht)", "⏸ Locked (time stopped)", "⏸ Sesi Terkunci (waktu berhenti)"),
        ("widget_locked_schedule", "🌙 Gesperrt (keine Computerzeit)", "🌙 Locked (outside allowed hours)", "🌙 Terkunci (di luar jam diizinkan)"),
        ("widget_locked_allowance", "⏸ Gesperrt (Zeit für heute um)", "⏸ Locked (no time left today)", "⏸ Terkunci (waktu hari ini habis)"),
        ("widget_locked_break", "⏸ Gesperrt (Pause)", "⏸ Locked (break)", "⏸ Terkunci (istirahat)"),
        ("widget_request_button", "🎮 Mehr Zeit wünschen", "🎮 Ask for More Time", "🎮 Minta Tambahan Waktu"),
        ("request_window_title", "Mehr Zeit wünschen", "Ask for More Time", "Minta Tambahan Waktu"),
        ("request_window_unavailable",
            "Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt.",
            "The request window does not work right now. Ask your parents directly.",
            "Jendela permintaan sedang tidak bisa dibuka. Tanyakan langsung ke orang tuamu."),
        ("widget_declined_title", "❌ Anfrage abgelehnt", "❌ Request Declined", "❌ Permintaan Ditolak"),
        ("widget_declined_default", "Deine Eltern haben die Anfrage abgelehnt.", "Your parent declined the request.", "Permintaan ditolak oleh orang tua."),
        ("status_unrestricted", "Konto ohne Grenzen", "Unrestricted account", "Akun tidak dibatasi"),
        ("prompt_window_title", "Nachricht von deinen Eltern", "Message from Your Parent", "Pemberitahuan Orang Tua"),
        ("prompt_title", "Nachricht von deinen Eltern", "Message from Your Parent", "Pesan dari Orang Tua"),
        ("prompt_urgent_title", "🚨 WICHTIGE NACHRICHT VON DEINEN ELTERN", "🚨 URGENT MESSAGE FROM YOUR PARENT", "🚨 PESAN MENDESAK DARI ORANG TUA"),
        ("prompt_yes", "✅ Ja / Okay", "✅ Yes / OK", "✅ Ya / Siap"),
        ("prompt_no", "❌ Nein", "❌ No", "❌ Tidak"),
        ("prompt_reason_label", "Warum nicht? Wähle einen Grund oder schreib selbst:", "Pick or type a reason:", "Pilih atau tulis alasan penolakan:"),
        ("prompt_back", "Zurück", "Back", "Kembali"),
        ("prompt_send", "Grund senden 📤", "Send Reason 📤", "Kirim Alasan 📤"),
        ("prompt_no_reason", "Ohne Grund abgelehnt", "Declined without a reason", "Ditolak tanpa alasan khusus"),
        ("fs_window_title", "Einstellungen für Vollbild", "Fullscreen Display Settings", "Pengaturan Tampilan Layar Penuh"),
        ("fs_heading", "⚙️ Position bei Vollbild (Spiel / Video)", "⚙️ Position in Fullscreen (Game / Video)", "⚙️ Posisi Saat Layar Penuh (Game / Video)"),
        ("fs_corner_label", "Ecke:", "Corner:", "Posisi Sudut (Corner Dock):"),
        ("fs_corner_top_right", "Oben rechts", "Top right", "Atas-Kanan (Top-Right)"),
        ("fs_corner_top_left", "Oben links", "Top left", "Atas-Kiri (Top-Left)"),
        ("fs_corner_bottom_right", "Unten rechts", "Bottom right", "Bawah-Kanan (Bottom-Right)"),
        ("fs_corner_bottom_left", "Unten links", "Bottom left", "Bawah-Kiri (Bottom-Left)"),
        ("fs_monitor_label", "Bildschirm (bei mehreren):", "Target Screen (Multi-Monitor):", "Layar Tujuan (Multi-Monitor):"),
        ("fs_monitor_item", "Bildschirm {number} [{width}x{height}]", "Monitor {number} [{width}x{height}]", "Monitor {number} [{width}x{height}]"),
        ("fs_monitor_item_primary",
            "Bildschirm {number} (Hauptbildschirm) [{width}x{height}]",
            "Monitor {number} (Primary) [{width}x{height}]",
            "Monitor {number} (Utama) [{width}x{height}]"),
        ("fs_opacity_label", "Durchsichtig im Spiel:", "Opacity in Games:", "Transparansi Saat Game:"),
        ("fs_auto_collapse", "Bei nur einem Bildschirm klein anzeigen", "Collapse to a mini pill on a single screen", "Kecilkan ke mini pill pada layar tunggal"),
        ("fs_preview", "👁️ Position testen (8 Sekunden)", "👁️ Test Position (8 Seconds)", "👁️ Uji / Preview Posisi (8 Detik)"),
        ("fs_cancel", "Abbrechen", "Cancel", "Batal"),
        ("fs_save", "Speichern", "Save", "Simpan"),
    };

    private static readonly IReadOnlyDictionary<string, TextSet> Sets = TextCatalog.Build(Rows);

    public static TextSet For(string? preset) => TextCatalog.For(Sets, preset);

    /// <summary>The tooltip of the tray icon for the remaining time.</summary>
    public static string RemainingTooltip(TextSet texts, int remainingSeconds)
    {
        var seconds = Math.Max(0, remainingSeconds);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return hours > 0
            ? texts.Format("tray_remaining_hours", ("hours", hours), ("minutes", minutes))
            : texts.Format("tray_remaining_minutes", ("minutes", minutes));
    }
}
