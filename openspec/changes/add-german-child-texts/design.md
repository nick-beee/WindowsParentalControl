# Design

## Context

See proposal.md for the motivation. The current code has these properties:

- `SettingsRepository.ApplyLanguagePreset(lang)` knows two presets. Each value other than "en" becomes "id". A call with "de" stores "id" and writes Indonesian texts.
- `ApplyLanguagePreset` writes all 10 `msg_*` keys. It does not touch `prompt_response_presets`. The default of that key is the Indonesian constant `DefaultPromptResponsePresets` for each preset.
- `SettingsRepository.GetMessage(key)` applies the whole current preset again when one key is empty. This replaces each text that the parent edited (see the comment in `WebServerHost.cs`, the `/api/admin/settings` POST).
- Eight places read the preset with the fallback "id": `GetMessage`, `CategoryRepository.SeedIfNeeded`, `SettingsViewModel.LoadSettings` and five places in `TelegramBotService`.
- Nothing writes the preset at the first start of the service. The first writer is either a `GetMessage` call ("id") or the first-launch question of the WPF Admin (`MainViewModel.CheckFirstLaunchLanguagePreset`, Yes = "id", No = "en"). `CategoryRepository.SeedIfNeeded` writes the Indonesian category texts before a preset exists.
- `TelegramBotService` checks `lang == "en"` and uses Indonesian in each other case.
- `CategoryEnforcer` puts `AppCategory.ToString()` ("Games") into `{name}` and `{category}`.
- Several session messages have fixed text in the code: `NotificationManager.SendLogonBanner` (Indonesian), the titles in `SendWarning` and `SendCurfewWarning` (English), the countdown of `/api/admin/lock` (Indonesian), the terminate text of `/api/admin/terminate` (English), the parent message titles in `WebServerHost` (Indonesian), and the approve and decline popups and decline reasons in `TelegramBotService` (Indonesian).
- The web admin has no preset picker. It edits `msg_category_warn`, `msg_category_suspended` and `prompt_response_presets`. The WPF Admin has two preset buttons and editors for the 8 older `msg_*` keys.

No WinAPI call changes. The session messages keep `WTSSendMessageW` through `NotificationManager.SendMessage`. That call takes UTF-16 text, so the German characters (ä, ö, ü, ß, „ “) need no change. No table and no column is added. The preset stays in `app_settings.language_preset`, and the texts stay in their `app_settings` rows.

## Goals / Non-Goals

**Goals:**
- One catalog of all child texts of the three presets, in `ParentalControl.Domain`, so that the tests of the catalog and of the switch rule run on the Mac.
- One accessor for the preset, with the default "de".
- No text of the parent is lost: not on an update, not on a preset switch, not on one empty key.

**Non-Goals:**
- A general translation framework (resource files, `CultureInfo`, `IStringLocalizer`). Three presets in a C# dictionary are enough, and the texts must stay editable in `app_settings`.
- A change of the `en` and `id` texts of the 10 `msg_*` keys. They stay the same, so that the switch rule still finds them as unedited defaults. The English texts say "Your session will close now", which is not correct (the service locks the session). A later change can fix that.
- The texts of the Session Agent and of the child request page (see decision 9).

## Decisions

### 1. A text catalog in ParentalControl.Domain

New folder `src/ParentalControl.Domain/Messages` with:

- `LanguagePreset`: the constants `De`, `En`, `Id`, the list `All`, `Default = De`, and `Normalize(string? stored)`. `Normalize` returns the stored value if it is a known preset (letter case ignored), and `Default` for each other value. `ParentLanguage(preset)` returns "id" for "id" and "en" for each other preset (decision 7).
- `ChildMessageKeys`: the 10 `msg_*` keys and `prompt_response_presets`. `SettingsRepository` keeps its `Key*` constants as aliases of these, so that the callers do not change.
- `MessageCatalog.Defaults(preset)`: the default text of each child message key for a preset.
- `SessionTexts.For(preset)`: the fixed session texts of decision 6, the weekday names and the date format.
- `CategoryNames.For(category, preset)`: the category name in a message (decision 5).
- `TextTemplate.Fill(text, placeholders)` and `TextTemplate.Placeholders(text)`: the fill moves out of `GetMessage`, and the tests use `Placeholders` to compare the presets.
- `PresetSwitch.Plan(stored, target)`: the pure rule of decision 3.

Alternative: keep the texts in `SettingsRepository`. Rejected, because `ParentalControl.Core` uses SQLite and WinAPI, so its tests run only on the VM.

### 2. "de" is the default of a new install; the seed moves to DatabaseManager

`DatabaseManager.Initialize` gets a step `SeedChildMessages` before `CategoryRepository.SeedIfNeeded`, in one transaction:

1. If `language_preset` has no row, it applies "de" with the switch rule of decision 3. On a new database all texts are missing, so all get the German text. An earlier install without a preset row keeps each edited text and gets German for each default text.
2. For each child message key without a row, it inserts the default of the stored preset (`INSERT OR IGNORE`). This step covers a key that a later version adds.

The category text seed in `CategoryRepository.SeedIfNeeded` goes away, because step 2 covers it. `SettingsRepository.GetLanguagePreset()` replaces each `Get(KeyLanguagePreset, "id")` and returns `LanguagePreset.Normalize(stored)`.

`MainViewModel.CheckFirstLaunchLanguagePreset` goes away. After step 1 the preset row always exists, so the question would never show.

Alternative: keep the first-launch question with "Deutsch" as Yes. Rejected, because the service seeds the preset before the parent starts the WPF Admin, and today the question shows only if the WPF Admin starts first.

### 3. The preset switch replaces only unedited texts

`PresetSwitch.Plan(stored, target)` gets the stored texts (key to text) and the target preset. For each child message key it returns a write of the target default if one of these is true:

- the key has no stored text, or the text is empty or white space
- the stored text is equal to the default of that key in one of the three presets

Otherwise the result lists the key as kept. The compare ignores white space at the start and at the end and treats `\r\n` as `\n`, because `prompt_response_presets` comes from a text box. The comparison uses the current catalog only. A default text of an earlier program version that is not in the catalog counts as edited and stays. The `msg_*` texts of "en" and "id" have not changed since the upstream version, so the effect is small.

`SettingsRepository.ApplyLanguagePreset(preset)` stores the normalized preset, writes the planned texts and returns the list of kept keys. The WPF Admin shows the count of kept keys in its status line, for example "Applied German preset. 2 edited texts kept: msg_remote_lock, msg_bonus_granted."

Alternative: replace all texts, as today. Rejected, because the issue asks to keep the texts of the parent, and a German parent who edits one text must not lose it when they click the preset again. To reset an edited text, the parent can copy the default from this design or make the text empty (decision 4).

### 4. A missing text uses the default of its own key only

`GetMessage(key)` reads the stored text. If the text is missing, empty or white space, it uses `MessageCatalog.Defaults(GetLanguagePreset())[key]`. It does not write to the database and does not touch another key. Then it fills the placeholders with `TextTemplate.Fill`.

### 5. Category names per preset

`CategoryEnforcer` puts `CategoryNames.For(category, preset)` into `{name}` and `{category}` when the message is about a category. The name of an app (`AppName(rule)`) stays as it is. The names:

| Category | de | en and id |
|---|---|---|
| Games | Spiele | Games |
| Video | Videos | Video |
| Social | Soziale Medien | Social |
| Study | Lernen | Study |
| Creative | Kreativ | Creative |
| System | System | System |
| Unknown | Sonstiges | Unknown |

The German texts say "Deine Zeit für {name}" and "Der Computer hat {name} pausiert". These forms read correctly with a plural category name ("Spiele"), with a singular name ("Lernen") and with an app name ("Minecraft").

The event log and the service log keep the English enum names.

### 6. Fixed session texts follow the preset, but the parent cannot edit them

The fixed session texts get keys in `SessionTexts`, but no `app_settings` row. The parent cannot edit them. This keeps the settings pages as they are. The generic title "Parental Control" is the program name and stays the same in each preset. The "id" texts are the current texts in the code, letter for letter, so that an Indonesian install sees no change. The "en" texts are new where the current text is Indonesian.

### 7. Telegram texts for the parent

`TelegramBotService` reads `LanguagePreset.ParentLanguage(GetLanguagePreset())` and compares that value with "en". Thus "de" gives the English texts. Telegram texts that exist only in Indonesian (for example the decline buttons) stay as they are.

### 8. WPF Admin

`SettingsView.xaml` gets a third button "🇩🇪 Deutsch Preset" with the command `ApplyPresetDe`. The three buttons use the switch rule of decision 3. `SettingsViewModel.LoadSettings` reads the preset with `GetLanguagePreset()`. The admin UI texts stay in English.

The `/api/admin/settings` GET of the web admin uses the default of the current preset for `promptResponsePresets` when the key has no row. The web admin gets no preset picker.

### 9. The Session Agent and the child request page stay out of scope

The widget, the prompt dialog and the full-screen settings of the Session Agent have Indonesian text in XAML and C# (for example "SISA WAKTU HARI INI", "Permintaan Ditolak"). The child request page (`RequestPage.cs`, about 950 lines) has Indonesian HTML and status texts. The service also sends some status texts to the agent (for example "Akun tidak dibatasi" in `WebServerHost.cs`). For these texts, the agent and the page must get the preset from the service. They also need a text catalog of their own. This is a separate change with its own UI checks on the VM. This change prepares it, because the catalog of decision 1 is in `ParentalControl.Domain`, and the agent can use that project.

## Texts

The placeholders stay the same in each preset. The German texts use "du" and short sentences. They write "Min." in place of "Minuten", because the warnings also come at 1 minute, and "in 1 Minuten" is wrong.

### Child message keys (editable)

The "de" text is new. The "en" text is the current text, for reference.

| Key | Placeholders | de | en (current) |
|---|---|---|---|
| `msg_limit_warn` | `{minutes}` | Deine Bildschirmzeit für heute ist in {minutes} Min. um. Speichere jetzt, was du gerade machst. | You have {minutes} minutes of daily screen time remaining. Please save your work. |
| `msg_curfew_warn` | `{curfew_end}`, `{minutes}` | Die Computerzeit endet um {curfew_end} Uhr, also in {minutes} Min. Speichere jetzt, was du gerade machst. | Allowed usage time ends at {curfew_end} ({minutes} minutes remaining). Please save your work. |
| `msg_limit_reached` | – | Deine Bildschirmzeit für heute ist um. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen. | Your daily screen time limit has been reached. Your session will close now. |
| `msg_curfew_reached` | – | Jetzt ist keine Computerzeit. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen. | Allowed usage time has ended (curfew). Your session will close now. |
| `msg_login_denied_limit` | – | Anmelden geht gerade nicht: Deine Bildschirmzeit für heute ist schon um. | Sign-in denied: Daily screen time limit has already been reached. |
| `msg_login_denied_curfew` | – | Anmelden geht gerade nicht: Jetzt ist keine Computerzeit. | Sign-in denied: Outside allowed schedule hours. |
| `msg_remote_lock` | – | Zeit für eine Pause! Deine Eltern sperren gleich den Computer. | Time for a break. Computer will be locked by administrator shortly. |
| `msg_bonus_granted` | `{minutes}` | Deine Eltern haben dir +{minutes} Min. Bildschirmzeit geschenkt! | Administrator has granted you +{minutes} minutes of screen time! |
| `msg_category_warn` | `{name}`, `{minutes}` | Deine Zeit für {name} ist in {minutes} Min. um. Dann pausiert der Computer {name}, aber nichts wird geschlossen. Speichere am besten jetzt. | {name} time ends in {minutes} minutes. Then the {name} programs pause. They do not close. Please save your progress. |
| `msg_category_suspended` | `{name}` | Deine Zeit für {name} ist für heute um. Der Computer hat {name} pausiert, nicht geschlossen. Morgen oder mit Extra-Minuten geht es weiter. | {name} time is over for today. The {name} programs are paused, not closed. They continue tomorrow or after bonus minutes. |

`msg_limit_reached` and `msg_curfew_reached` come before `LockSession`. The login texts come before `ForceLogoff` at sign-in, and before `LockSession` when the child unlocks outside the allowed time. "Anmelden geht gerade nicht" fits both cases.

`prompt_response_presets` (one reason per line, the child picks one to decline a parent prompt):

| de | en (new) | id (current) |
|---|---|---|
| Ich bin gleich mit dem Spiel fertig | Almost done with my game | Sebentar lagi selesai game |
| Ich mache gerade Hausaufgaben | Doing my homework | Sedang tugas sekolah |
| Okay, ich höre gleich auf | Okay, I will stop soon | Oke, segera logout |
| Bitte noch 5 Minuten | Please, 5 more minutes | Tanggung, minta waktu 5 menit lagi |

### Fixed session texts (not editable)

| Key | Placeholders | de | en | id (current) |
|---|---|---|---|---|
| `logon_title` | – | Parental Control — Hallo! | Parental Control — Session Started | Parental Control — Sesi Aktif |
| `logon_body` | `{username}`, `{time}`, `{start}`, `{end}` | Hallo, {username}!⏎⏎• Deine Bildschirmzeit heute: {time}⏎• Computerzeit: {start} – {end} Uhr⏎⏎Du bekommst eine Warnung, bevor deine Zeit um ist. | Hello, {username}!⏎⏎• Screen time today: {time}⏎• Allowed hours: {start} – {end}⏎⏎You get a warning before your time ends. | Halo, {username}!⏎⏎• Jatah Waktu Layar Hari Ini: {time}⏎• Jam Yang Diizinkan: {start} – {end}⏎⏎Anda akan menerima peringatan otomatis sebelum waktu sesi habis. |
| `duration_hours` | `{hours}`, `{minutes}` | {hours} Std. {minutes} Min. | {hours} h {minutes} min | {hours} jam {minutes} menit |
| `duration_minutes` | `{minutes}` | {minutes} Min. | {minutes} min | {minutes} menit |
| `warn_title` | – | Parental Control — Bildschirmzeit | Parental Control — Screen Time Warning | Parental Control — Screen Time Warning |
| `curfew_warn_title` | – | Parental Control — Computerzeit | Parental Control — Curfew Warning | Parental Control — Curfew Warning |
| `lock_title` | – | Parental Control — Computer wird gesperrt | Parental Control — Screen Lock | Parental Control — Layar Dikunci |
| `lock_countdown` | `{seconds}` | (Der Computer wird in {seconds} Sekunden gesperrt.) | (The computer locks in {seconds} seconds.) | (Layar akan otomatis dikunci dalam {seconds} detik) |
| `session_ended` | – | Deine Eltern beenden jetzt deine Sitzung. | Administrator has terminated this session. | Administrator has terminated this session. |
| `bonus_title` | – | Parental Control — Mehr Zeit! | Parental Control — Extra Time Approved! | Parental Control — Tambahan Waktu Disetujui! |
| `bonus_body` | `{minutes}` | Super! Deine Eltern haben dir für heute +{minutes} Min. Bildschirmzeit gegeben. | Good news! Your parent approved +{minutes} minutes of screen time for today. | Kabar baik! Administrator telah menyetujui tambahan waktu layar sebesar +{minutes} menit untuk hari ini. |
| `exception_title` | – | Parental Control — Neuer Zeitplan | Parental Control — Schedule Approved! | Parental Control — Jadwal Disetujui! |
| `exception_body` | `{date}`, `{minutes}`, `{start}`, `{end}` | Deine Eltern haben für den {date} einen besonderen Zeitplan erlaubt: {minutes} Min., von {start} bis {end} Uhr. | Your parent approved a special schedule for {date} ({minutes}m, {start}–{end}). | Jadwal khusus untuk tanggal {date} ({minutes}m, {start}–{end}) telah disetujui orang tua. |
| `weekly_title` | – | Parental Control — Neuer Wochenplan | Parental Control — Weekly Schedule Approved! | Parental Control — Jadwal Permanen Disetujui! |
| `weekly_body` | `{day}`, `{minutes}`, `{start}`, `{end}` | Deine Eltern haben deinen Plan für jeden {day} geändert: {minutes} Min., von {start} bis {end} Uhr. | Your parent changed the weekly schedule for {day} ({minutes}m, {start}–{end}). | Jadwal mingguan hari {day} ({minutes}m, {start}–{end}) telah diperbarui permanen oleh orang tua. |
| `declined_title` | – | Parental Control — Anfrage abgelehnt | Parental Control — Request Declined | Parental Control — Permintaan Ditolak |
| `declined_with_reason` | `{reason}` | Deine Eltern haben deine Anfrage abgelehnt: „{reason}“ | Your request was declined: "{reason}" | Permintaan Anda ditolak: "{reason}" |
| `declined` | – | Deine Eltern haben deine Anfrage abgelehnt. | Your parent declined your request for more screen time. | Permintaan tambahan waktu layar Anda ditolak oleh administrator. |
| `message_title` | – | Parental Control — Nachricht von deinen Eltern | Parental Control — Message | Parental Control — Pesan |
| `message_urgent_title` | – | 🚨 Parental Control — Wichtig | 🚨 Parental Control — Urgent | 🚨 Parental Control — Mendesak |
| `decline_reason_1` | – | Mach zuerst deine Hausaufgaben. | Finish your homework first. | Selesaikan tugas sekolah terlebih dahulu. |
| `decline_reason_2` | – | Es ist Zeit zum Schlafen oder Ausruhen. | It is time to sleep or rest. | Sudah waktunya tidur/istirahat. |
| `decline_reason_3` | – | Gleich gibt es Essen mit der Familie. | It is time for a family meal. | Waktunya makan bersama keluarga. |
| `decline_reason_4` | – | Heute gibt es weniger Spielzeit, weil du dich nicht an eine Regel gehalten hast. | Play time is limited because a rule was broken. | Waktu bermain dibatasi karena melanggar aturan. |
| `decline_reason_default` | – | Deine Eltern haben die Anfrage abgelehnt. | Your parent declined the request. | Permintaan ditolak oleh orang tua. |

⏎ is a line break (`\n`). The times use `HH:mm` in each preset. The date format is `dd.MM.yyyy` for "de", `dd/MM/yyyy` for "id" (current) and `yyyy-MM-dd` for "en". The weekday names for `{day}` are Montag … Sonntag for "de", Monday … Sunday for "en", and the current names of `GetIndonesianDay` for "id".

The decline reasons go into the Grace Request record, so the parent and the widget see the reason in the language of the preset at the time of the decline. The Telegram buttons that the parent uses to pick a reason stay Indonesian (decision 7).

## Risks / Trade-offs

- [After a switch, the parent's edited texts stay in the old language, so the child sees mixed languages.] → The WPF Admin shows the kept keys after the switch. The parent can then edit or empty them.
- [A parent who typed a text that is equal to a default of another preset loses it on a switch.] → That text is a default text, so the parent gets the default text of the new preset. The effect is small.
- [A default text of an earlier version that is not in the catalog counts as edited.] → The `msg_*` texts of "en" and "id" are the same since upstream. The category texts are from stage 1 and are in the catalog.
- [The "en" preset changes texts that were Indonesian (logon banner, prompt reasons, popups).] → This is a correction. The proposal marks it as **BREAKING**. Only an install with "en" sees it.
- [The texts of the Session Agent and of the request page stay Indonesian, so the child still sees some Indonesian text.] → The follow-up change of decision 9.
- [The child can reach `127.0.0.1:5050`.] → The language changes only the text. No limit depends on the preset, and the preset comes only from `app_settings`, which the child cannot write.

## Migration Plan

- No manual step. `DatabaseManager.Initialize` runs at each start of the service and of the WPF Admin. A new database gets "de". A database with a preset keeps it.
- Rollback: an earlier version reads "de" as "id" in `ApplyLanguagePreset` and in Telegram. The stored German texts stay and the child still sees them, because the earlier `GetMessage` applies the preset only for an empty text. A preset switch in the earlier WPF Admin works as before.

## Open Questions

- The German wording: the user reviews the tables above. A change of a word does not change the specs, the approach or the tasks.
- "Min." or "Minuten" in the German texts.
