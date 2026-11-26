namespace ParentalControl.Domain.Messages;

/// <summary>The child message keys: the texts in <c>app_settings</c> that the parent can edit.</summary>
public static class ChildMessageKeys
{
    public const string LimitWarn = "msg_limit_warn";
    public const string CurfewWarn = "msg_curfew_warn";
    public const string LimitReached = "msg_limit_reached";
    public const string CurfewReached = "msg_curfew_reached";
    public const string LoginDeniedLimit = "msg_login_denied_limit";
    public const string LoginDeniedCurfew = "msg_login_denied_curfew";
    public const string RemoteLock = "msg_remote_lock";
    public const string BonusGranted = "msg_bonus_granted";
    // App Categories. Placeholders: {name} (category or app), {category}, {minutes}.
    public const string CategoryWarn = "msg_category_warn";
    public const string CategorySuspended = "msg_category_suspended";
    // Breaks. Placeholders: {minutes}; {break_end} (local time HH:mm) in the start and running texts.
    public const string BreakWarn = "msg_break_warn";
    public const string BreakStart = "msg_break_start";
    public const string BreakRunning = "msg_break_running";
    // Blocked programs. Placeholder: {name} (the exe file name without ".exe").
    public const string ProgramBlocked = "msg_program_blocked";
    /// <summary>The reasons that the child can pick to decline a parent prompt, one per line.</summary>
    public const string PromptResponsePresets = "prompt_response_presets";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        LimitWarn, CurfewWarn, LimitReached, CurfewReached, LoginDeniedLimit, LoginDeniedCurfew,
        RemoteLock, BonusGranted, CategoryWarn, CategorySuspended, BreakWarn, BreakStart, BreakRunning,
        ProgramBlocked, PromptResponsePresets,
    };
}

/// <summary>The default texts of the child message keys per Language Preset.</summary>
public static class MessageCatalog
{
    private static readonly (string Key, string De, string En, string Id)[] Rows =
    {
        (ChildMessageKeys.LimitWarn,
            "Deine Bildschirmzeit für heute ist in {minutes} {minutes|Minute|Minuten} um. Speichere jetzt, was du gerade machst.",
            "You have {minutes} {minutes|minute|minutes} of daily screen time remaining. Please save your work.",
            "Sisa waktu layar harian Anda tinggal {minutes} menit lagi. Harap simpan semua pekerjaan Anda."),
        (ChildMessageKeys.CurfewWarn,
            "Die Computerzeit endet um {curfew_end} Uhr, also in {minutes} {minutes|Minute|Minuten}. Speichere jetzt, was du gerade machst.",
            "Allowed usage time ends at {curfew_end} ({minutes} {minutes|minute|minutes} remaining). Please save your work.",
            "Waktu penggunaan yang diizinkan akan berakhir pada pukul {curfew_end} ({minutes} menit lagi). Harap simpan pekerjaan Anda."),
        (ChildMessageKeys.LimitReached,
            "Deine Bildschirmzeit für heute ist um. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen.",
            "Your daily screen time limit has been reached. Your session will close now.",
            "Batas waktu layar harian Anda telah habis. Sesi Anda akan ditutup sekarang."),
        (ChildMessageKeys.CurfewReached,
            "Jetzt ist keine Computerzeit. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen.",
            "Allowed usage time has ended (curfew). Your session will close now.",
            "Waktu penggunaan yang diizinkan telah berakhir (jam malam). Sesi Anda akan ditutup sekarang."),
        (ChildMessageKeys.LoginDeniedLimit,
            "Anmelden geht gerade nicht: Deine Bildschirmzeit für heute ist schon um.",
            "Sign-in denied: Daily screen time limit has already been reached.",
            "Login ditolak: Batas waktu layar harian telah tercapai."),
        (ChildMessageKeys.LoginDeniedCurfew,
            "Anmelden geht gerade nicht: Jetzt ist keine Computerzeit.",
            "Sign-in denied: Outside allowed schedule hours.",
            "Login ditolak: Di luar jadwal jam yang diizinkan."),
        (ChildMessageKeys.RemoteLock,
            "Zeit für eine Pause! Deine Eltern sperren gleich den Computer.",
            "Time for a break. Computer will be locked by administrator shortly.",
            "Waktunya istirahat. Komputer akan segera dikunci oleh administrator."),
        (ChildMessageKeys.BonusGranted,
            "Deine Eltern haben dir +{minutes} {minutes|Minute|Minuten} Bildschirmzeit geschenkt!",
            "Administrator has granted you +{minutes} {minutes|minute|minutes} of screen time!",
            "Administrator telah menambahkan +{minutes} menit waktu layar untuk Anda!"),
        (ChildMessageKeys.CategoryWarn,
            "Deine Zeit für {name} ist in {minutes} {minutes|Minute|Minuten} um. Dann pausiert der Computer {name}, aber nichts wird geschlossen. Speichere am besten jetzt.",
            "{name} time ends in {minutes} {minutes|minute|minutes}. Then the {name} programs pause. They do not close. Please save your progress.",
            "Waktu {name} akan habis dalam {minutes} menit. Setelah itu program {name} dijeda, tidak ditutup. Harap simpan progres Anda."),
        (ChildMessageKeys.CategorySuspended,
            "Deine Zeit für {name} ist für heute um. Der Computer hat {name} pausiert, nicht geschlossen. Morgen oder mit Extra-Minuten geht es weiter.",
            "{name} time is over for today. The {name} programs are paused, not closed. They continue tomorrow or after bonus minutes.",
            "Waktu {name} untuk hari ini sudah habis. Program {name} dijeda, tidak ditutup. Program berlanjut besok atau setelah menit bonus."),
        (ChildMessageKeys.BreakWarn,
            "In {minutes} {minutes|Minute|Minuten} ist Pause. Speichere jetzt, was du gerade machst. Deine Programme bleiben offen.",
            "Break in {minutes} {minutes|minute|minutes}. Please save your work. Your programs stay open.",
            "Waktu istirahat dalam {minutes} menit. Harap simpan pekerjaan Anda. Program Anda tetap terbuka."),
        (ChildMessageKeys.BreakStart,
            "Zeit für eine Pause! Der Computer wird jetzt für {minutes} {minutes|Minute|Minuten} gesperrt, bis {break_end} Uhr. Deine Programme bleiben offen.",
            "Time for a break! The computer locks now for {minutes} {minutes|minute|minutes}, until {break_end}. Your programs stay open.",
            "Waktunya istirahat! Komputer dikunci sekarang selama {minutes} menit, sampai pukul {break_end}. Program Anda tetap terbuka."),
        (ChildMessageKeys.BreakRunning,
            "Die Pause dauert noch bis {break_end} Uhr ({minutes} {minutes|Minute|Minuten}). Der Computer wird wieder gesperrt.",
            "The break lasts until {break_end} ({minutes} {minutes|minute|minutes} left). The computer locks again.",
            "Istirahat berlangsung sampai pukul {break_end} ({minutes} menit lagi). Komputer dikunci kembali."),
        (ChildMessageKeys.ProgramBlocked,
            "{name} ist auf diesem Computer gesperrt. Frag deine Eltern, wenn du das Programm brauchst.",
            "{name} is blocked on this computer. Ask your parents if you need this program.",
            "{name} diblokir di komputer ini. Tanya orang tuamu jika kamu membutuhkan program ini."),
        (ChildMessageKeys.PromptResponsePresets,
            "Ich bin gleich mit dem Spiel fertig\nIch mache gerade Hausaufgaben\nOkay, ich höre gleich auf\nBitte noch 5 Minuten",
            "Almost done with my game\nDoing my homework\nOkay, I will stop soon\nPlease, 5 more minutes",
            "Sebentar lagi selesai game\nSedang tugas sekolah\nOke, segera logout\nTanggung, minta waktu 5 menit lagi"),
    };

    private static readonly IReadOnlyDictionary<string, TextSet> Sets = TextCatalog.Build(Rows);

    /// <summary>
    /// Default texts of earlier program versions that the current catalog does not have. They are unedited texts:
    /// a preset switch and an update replace them. The four "en" texts got the word form of {minutes} in this version.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Earlier = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [ChildMessageKeys.LimitWarn] = new[] { "You have {minutes} minutes of daily screen time remaining. Please save your work." },
        [ChildMessageKeys.CurfewWarn] = new[] { "Allowed usage time ends at {curfew_end} ({minutes} minutes remaining). Please save your work." },
        [ChildMessageKeys.BonusGranted] = new[] { "Administrator has granted you +{minutes} minutes of screen time!" },
        [ChildMessageKeys.CategoryWarn] = new[] { "{name} time ends in {minutes} minutes. Then the {name} programs pause. They do not close. Please save your progress." },
    };

    /// <summary>The default text of each child message key for a preset. An unknown preset gives "de".</summary>
    public static TextSet Defaults(string? preset) => TextCatalog.For(Sets, preset);

    /// <summary>The default texts of a key in earlier program versions (see <see cref="Earlier"/>).</summary>
    public static IReadOnlyList<string> EarlierDefaults(string key) =>
        Earlier.TryGetValue(key, out var texts) ? texts : Array.Empty<string>();
}
