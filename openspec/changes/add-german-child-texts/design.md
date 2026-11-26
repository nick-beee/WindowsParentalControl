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
- Several session messages have fixed text in the code: `NotificationManager.SendLogonBanner` (Indonesian), the titles in `SendWarning` and `SendCurfewWarning` (English), the countdown of `/api/admin/lock-session` (Indonesian), the terminate text of `/api/admin/force-logoff` (English), the parent message titles in `WebServerHost` (Indonesian), and the approve and decline popups and decline reasons in `TelegramBotService` (Indonesian).
- The Session Agent (`src/ParentalControl.Agent`) has Indonesian text in `WidgetWindow.xaml`, `PromptDialogWindow.xaml`, `FullscreenSettingsWindow.xaml` and in the code of `AgentController` (tray menu, tooltips), `WidgetWindow`, `PromptDialogWindow` and `FullscreenSettingsWindow`. It runs as the Restricted User and gets its data only from `/api/agent/status`. It references `ParentalControl.Core`, which references `ParentalControl.Domain`.
- The child request page (`RequestPage.Render`) has Indonesian HTML, JS strings and status texts. The `/request` handler in `WebServerHost` adds the Indonesian day names and codes, "Pengecualian Hari Ini" and the default summary. `/api/request` returns Indonesian error and success messages, which the page shows in `#feedback`. PR #23 escapes each text from the database with `WebUtility.HtmlEncode` and `JsonSerializer.Serialize`.
- The texts with a number use one fixed word form: "{minutes} minutes" gives "1 minutes" in English.
- The web admin has no preset picker. It edits `msg_category_warn`, `msg_category_suspended` and `prompt_response_presets`. The WPF Admin has two preset buttons and editors for the 8 older `msg_*` keys.

No WinAPI call changes. The session messages keep `WTSSendMessageW` through `NotificationManager.SendMessage`. That call takes UTF-16 text, so the German characters (ä, ö, ü, ß, „ “) need no change. No table and no column is added. The preset stays in `app_settings.language_preset`, and the editable texts stay in their `app_settings` rows.

## Goals / Non-Goals

**Goals:**
- One catalog of all child texts of the three presets, in `ParentalControl.Domain`, so that the tests of the catalog, of the word forms and of the switch rule run on the Mac.
- One accessor for the preset, with the default "de".
- Each text that the child sees follows the preset: the service messages, the Session Agent and the child request page.
- The correct word form for a number in German and English.
- No text of the parent is lost: not on an update, not on a preset switch, not on one empty key.

**Non-Goals:**
- A general translation framework (resource files, `CultureInfo`, `IStringLocalizer`). Three presets in C# dictionaries are enough, and the `msg_*` texts must stay editable in `app_settings`.
- A change of the "en" and "id" texts of the `msg_*` keys other than the word forms. The "en" texts say "Your session will close now", which is not correct (the service locks the session). A later change can fix that.
- Editable texts for the Session Agent and the request page. Their texts follow the preset, but the parent cannot edit them.
- The admin UIs, `RequestPage.RenderUnrestricted` (no caller) and the admin test banner of `/request` (only the parent sees it).

## Decisions

### 1. A text catalog in ParentalControl.Domain

New folder `src/ParentalControl.Domain/Messages` with:

- `LanguagePreset`: the constants `De`, `En`, `Id`, the list `All`, `Default = De`, and `Normalize(string? stored)`. `Normalize` returns the stored value if it is a known preset (letter case ignored), and `Default` for each other value. `ParentLanguage(preset)` returns "id" for "id" and "en" for each other preset (decision 7).
- `TextTemplate.Fill(text, values)` and `TextTemplate.Placeholders(text)`: the fill moves out of `GetMessage` and knows the word forms of decision 2. The tests use `Placeholders` to compare the presets.
- `TextSet`: the texts of one catalog for one preset, with `this[key]` and `Format(key, values)`. `Format` calls `TextTemplate.Fill`.
- `ChildMessageKeys`: the 10 `msg_*` keys and `prompt_response_presets`. `SettingsRepository` keeps its `Key*` constants as aliases of these, so that the callers do not change.
- `MessageCatalog.Defaults(preset)`: the default text of each child message key for a preset. `MessageCatalog.EarlierDefaults(key)`: the default texts of earlier program versions that are not in the current catalog (decision 3).
- `SessionTexts.For(preset)`: the fixed session texts of decision 6, the weekday names and the date format. `SessionTexts.Duration(set, minutes)` makes "2 Stunden 30 Minuten" from a number of minutes.
- `CategoryNames.For(category, preset)`: the category name in a message (decision 5).
- `AgentTexts.For(preset)`: the texts of the Session Agent (decision 9).
- `RequestPageTexts.For(preset)`: the texts of the child request page and of `/api/request` (decision 10).
- `PresetSwitch.Plan(stored, target)`: the pure rule of decision 3.

Alternative: keep the texts in `SettingsRepository`, the agent and the page. Rejected, because `ParentalControl.Core` uses SQLite and WinAPI, so its tests run only on the VM. Also one catalog lets one test check that each preset has each key and the same placeholders.

### 2. Word forms with `{name|one|other}`

A text marks the word form of a number in the text itself. `{minutes}` gives the number. `{minutes|Minute|Minuten}` gives "Minute" when the value of `minutes` is 1, and "Minuten" for each other value (0, 2, 5, 15). The rule "1 is singular, each other number is plural" is correct for German and for English. Indonesian has no plural form after a number ("1 menit", "5 menit"), so the Indonesian texts use no word form. The same mechanism serves `{seconds}`, `{hours}` and `{max}` (the number of requests).

`TextTemplate.Fill` replaces `{name}` and `{name|one|other}`. A placeholder without a value stays as it is, so that a typing error of the parent is visible. `Placeholders(text)` returns `name` for both forms, so the placeholder check of the catalog compares names only. An Indonesian text without the word form has the same placeholders as the German text.

Alternatives:
- A unit placeholder that the code fills, for example `{minutes_unit}`. Rejected: the code picks the word in the language of the preset, but a kept text of the parent can be in another language (decision 3). Then one sentence mixes two languages: "in 5 minutes um". With the word form in the text, the text carries its own words.
- Two texts per key (singular and plural). Rejected: this doubles the editable rows and the editors of the WPF Admin.
- The short form "Min." in German. Rejected by the user: the child should read "1 Minute" and "5 Minuten".

### 3. The preset switch replaces only unedited texts

`PresetSwitch.Plan(stored, target)` gets the stored texts (key to text) and the target preset. For each child message key it returns a write of the target default if one of these is true:

- the key has no stored text, or the text is empty or white space
- the stored text is equal to the default of that key in one of the three presets
- the stored text is equal to a default text of an earlier program version (`MessageCatalog.EarlierDefaults`)

Otherwise the result lists the key as kept. The compare ignores white space at the start and at the end and treats `\r\n` as `\n`, because `prompt_response_presets` comes from a text box.

The earlier default texts are the four "en" texts with `{minutes}` of the upstream version (`msg_limit_warn`, `msg_curfew_warn`, `msg_bonus_granted`, `msg_category_warn`), letter for letter. They change in this version because of the word forms. The "id" texts do not change, because Indonesian needs no word form.

`SettingsRepository.ApplyLanguagePreset(preset)` stores the normalized preset, writes the planned texts and returns the list of kept keys. The WPF Admin shows the count of kept keys in its status line, for example "Applied German preset. 2 edited texts kept: msg_remote_lock, msg_bonus_granted."

Alternative: replace all texts, as today. Rejected, because the issue asks to keep the texts of the parent, and the user confirmed that a switch keeps them, even if the languages then mix.

### 4. "de" is the default of a new install; the seed moves to DatabaseManager

`DatabaseManager.Initialize` gets a step `SeedChildMessages` before `CategoryRepository.SeedIfNeeded`, in one transaction:

1. If `language_preset` has no row, it applies "de" with the switch rule of decision 3. On a new database all texts are missing, so all get the German text. An earlier install without a preset row keeps each edited text and gets German for each default text.
2. If `language_preset` has a row, it replaces each stored text that is equal to an earlier default text (decision 3) with the current default of the stored preset. It does not change the preset and does not touch another text. This gives an "en" install the word forms, and it changes nothing in an "id" install.
3. For each child message key without a row, it inserts the default of the stored preset (`INSERT OR IGNORE`). This step covers a key that a later version adds.

The category text seed in `CategoryRepository.SeedIfNeeded` goes away, because step 3 covers it. `SettingsRepository.GetLanguagePreset()` replaces each `Get(KeyLanguagePreset, "id")` and returns `LanguagePreset.Normalize(stored)`.

`MainViewModel.CheckFirstLaunchLanguagePreset` goes away. After step 1 the preset row always exists, so the question would never show.

The user confirmed that an existing install does not switch to "de" by itself.

### 5. A missing text uses the default of its own key only

`GetMessage(key)` reads the stored text. If the text is missing, empty or white space, it uses `MessageCatalog.Defaults(GetLanguagePreset())[key]`. It does not write to the database and does not touch another key. Then it fills the placeholders with `TextTemplate.Fill`.

### 6. Category names per preset

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

### 7. Fixed session texts follow the preset, but the parent cannot edit them

The fixed session texts get keys in `SessionTexts`, but no `app_settings` row. The parent cannot edit them. This keeps the settings pages as they are. The generic title "Parental Control" is the program name and stays the same in each preset. The "id" texts are the current texts in the code, letter for letter, with one exception: a duration of whole hours is "2 jam", not "2 jam 0 menit". The "en" texts are new where the current text is Indonesian.

`TelegramBotService` reads `LanguagePreset.ParentLanguage(GetLanguagePreset())` and compares that value with "en". Thus "de" gives the English texts for the parent. Telegram texts that exist only in Indonesian (for example the decline buttons) stay as they are.

### 8. WPF Admin

`SettingsView.xaml` gets a third button "🇩🇪 Deutsch Preset" with the command `ApplyPresetDe`. The three buttons use the switch rule of decision 3. `SettingsViewModel.LoadSettings` reads the preset with `GetLanguagePreset()`. The admin UI texts stay in English.

The `/api/admin/settings` GET of the web admin uses the default of the current preset for `promptResponsePresets` when the key has no row. The web admin gets no preset picker.

### 9. The Session Agent gets the preset from the status API

`/api/agent/status` gets the field `language` with `GetLanguagePreset()`, in both responses (restricted and not restricted). `AgentStatusResponse` gets `string? language = null`. The agent calls `LanguagePreset.Normalize`, so a missing field (an earlier service) gives "de".

The agent keeps the current preset in `AgentController`. Before the first poll it uses "de". After each poll, if the preset changed, it:

- calls `WidgetWindow.ApplyTexts(AgentTexts.For(preset))`, which sets each static text of the widget. The static `TextBlock` and `Button` elements of the XAML get an `x:Name`;
- builds the tray menu again (`BuildContextMenu`);
- uses the new texts for the tooltip of the tray icon and for the next prompt dialog and settings window.

`PromptDialogWindow` and `FullscreenSettingsWindow` get the `TextSet` in their constructor and set their texts there. The message and the reason options of the parent stay as they are. The text "Ditolak tanpa alasan khusus" (the reason when the child declines without a reason) also follows the preset; the parent sees it in Telegram.

`NotifyIcon.Text` has a limit of 63 characters. The code keeps its cut at 63 characters, and the texts of the tray tooltip are shorter than that.

The service sends the preset to the agent. The security rule of CLAUDE.md is about data that the agent sends to the service, so this direction is safe. No limit depends on the preset.

Alternative: the agent reads the preset from the database. Rejected, because the agent runs as the Restricted User and must not read the database.

### 10. The child request page reads the preset on the server

The `/request` handler reads `GetLanguagePreset()` and gives `RequestPageTexts.For(preset)` and `SessionTexts.For(preset)` to `RequestPage.Render`. The page:

- sets `<html lang="{preset}">`;
- puts each catalog text into the HTML with `WebUtility.HtmlEncode`. A text with a placeholder for HTML (for example `{status}` in a banner) is encoded first, then the placeholder gets the HTML fragment, for example `<strong>` and the encoded status;
- puts each text of the JS code into one object, `const T = {{JsonSerializer.Serialize(jsTexts)}};`. Thus an apostrophe in a text cannot break a JS string;
- gets the minutes text of each day of the schedule strip from the server (`minutesText` in the JSON of the days), so that the JS code does not need the word forms.

The `/request` handler uses the weekday names of `SessionTexts` and the day codes of `RequestPageTexts`. `/api/request` reads the preset and returns its error and success messages from `RequestPageTexts`, with the date format of `SessionTexts`.

The page does not take a language from the URL. The preset comes only from `app_settings`, which the child cannot write.

The admin test banner ("Mode Uji Coba Administrator") stays Indonesian, because only the parent sees it.

## Texts

The placeholders stay the same in each preset. The German texts use "du" and short sentences.

### Child message keys (editable)

The "id" texts do not change. The "en" texts change only where the word form of `{minutes}` is new.

| Key | Placeholders | de | en |
|---|---|---|---|
| `msg_limit_warn` | `{minutes}` | Deine Bildschirmzeit für heute ist in {minutes} {minutes\|Minute\|Minuten} um. Speichere jetzt, was du gerade machst. | You have {minutes} {minutes\|minute\|minutes} of daily screen time remaining. Please save your work. |
| `msg_curfew_warn` | `{curfew_end}`, `{minutes}` | Die Computerzeit endet um {curfew_end} Uhr, also in {minutes} {minutes\|Minute\|Minuten}. Speichere jetzt, was du gerade machst. | Allowed usage time ends at {curfew_end} ({minutes} {minutes\|minute\|minutes} remaining). Please save your work. |
| `msg_limit_reached` | – | Deine Bildschirmzeit für heute ist um. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen. | (unchanged) |
| `msg_curfew_reached` | – | Jetzt ist keine Computerzeit. Der Computer wird jetzt gesperrt. Deine Programme bleiben offen. | (unchanged) |
| `msg_login_denied_limit` | – | Anmelden geht gerade nicht: Deine Bildschirmzeit für heute ist schon um. | (unchanged) |
| `msg_login_denied_curfew` | – | Anmelden geht gerade nicht: Jetzt ist keine Computerzeit. | (unchanged) |
| `msg_remote_lock` | – | Zeit für eine Pause! Deine Eltern sperren gleich den Computer. | (unchanged) |
| `msg_bonus_granted` | `{minutes}` | Deine Eltern haben dir +{minutes} {minutes\|Minute\|Minuten} Bildschirmzeit geschenkt! | Administrator has granted you +{minutes} {minutes\|minute\|minutes} of screen time! |
| `msg_category_warn` | `{name}`, `{minutes}` | Deine Zeit für {name} ist in {minutes} {minutes\|Minute\|Minuten} um. Dann pausiert der Computer {name}, aber nichts wird geschlossen. Speichere am besten jetzt. | {name} time ends in {minutes} {minutes\|minute\|minutes}. Then the {name} programs pause. They do not close. Please save your progress. |
| `msg_category_suspended` | `{name}` | Deine Zeit für {name} ist für heute um. Der Computer hat {name} pausiert, nicht geschlossen. Morgen oder mit Extra-Minuten geht es weiter. | (unchanged) |

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
| `duration_hours` | `{hours}` | {hours} {hours\|Stunde\|Stunden} | {hours} {hours\|hour\|hours} | {hours} jam |
| `duration_hours_minutes` | `{hours}`, `{minutes}` | {hours} {hours\|Stunde\|Stunden} {minutes} {minutes\|Minute\|Minuten} | {hours} {hours\|hour\|hours} {minutes} {minutes\|minute\|minutes} | {hours} jam {minutes} menit |
| `duration_minutes` | `{minutes}` | {minutes} {minutes\|Minute\|Minuten} | {minutes} {minutes\|minute\|minutes} | {minutes} menit |
| `warn_title` | – | Parental Control — Bildschirmzeit | Parental Control — Screen Time Warning | Parental Control — Screen Time Warning |
| `curfew_warn_title` | – | Parental Control — Computerzeit | Parental Control — Curfew Warning | Parental Control — Curfew Warning |
| `lock_title` | – | Parental Control — Computer wird gesperrt | Parental Control — Screen Lock | Parental Control — Layar Dikunci |
| `lock_countdown` | `{seconds}` | (Der Computer wird in {seconds} {seconds\|Sekunde\|Sekunden} gesperrt.) | (The computer locks in {seconds} {seconds\|second\|seconds}.) | (Layar akan otomatis dikunci dalam {seconds} detik) |
| `session_ended` | – | Deine Eltern beenden jetzt deine Sitzung. | Administrator has terminated this session. | Administrator has terminated this session. |
| `bonus_title` | – | Parental Control — Mehr Zeit! | Parental Control — Extra Time Approved! | Parental Control — Tambahan Waktu Disetujui! |
| `bonus_body` | `{minutes}` | Super! Deine Eltern haben dir für heute +{minutes} {minutes\|Minute\|Minuten} Bildschirmzeit gegeben. | Good news! Your parent approved +{minutes} {minutes\|minute\|minutes} of screen time for today. | Kabar baik! Administrator telah menyetujui tambahan waktu layar sebesar +{minutes} menit untuk hari ini. |
| `exception_title` | – | Parental Control — Neuer Zeitplan | Parental Control — Schedule Approved! | Parental Control — Jadwal Disetujui! |
| `exception_body` | `{date}`, `{minutes}`, `{start}`, `{end}` | Deine Eltern haben für den {date} einen besonderen Zeitplan erlaubt: {minutes} {minutes\|Minute\|Minuten}, von {start} bis {end} Uhr. | Your parent approved a special schedule for {date} ({minutes} {minutes\|minute\|minutes}, {start}–{end}). | Jadwal khusus untuk tanggal {date} ({minutes}m, {start}–{end}) telah disetujui orang tua. |
| `weekly_title` | – | Parental Control — Neuer Wochenplan | Parental Control — Weekly Schedule Approved! | Parental Control — Jadwal Permanen Disetujui! |
| `weekly_body` | `{day}`, `{minutes}`, `{start}`, `{end}` | Deine Eltern haben deinen Plan für jeden {day} geändert: {minutes} {minutes\|Minute\|Minuten}, von {start} bis {end} Uhr. | Your parent changed the weekly schedule for {day} ({minutes} {minutes\|minute\|minutes}, {start}–{end}). | Jadwal mingguan hari {day} ({minutes}m, {start}–{end}) telah diperbarui permanen oleh orang tua. |
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

⏎ is a line break (`\n`). `{time}` of `logon_body` is a duration: `duration_minutes` below 60 minutes, `duration_hours` for whole hours, else `duration_hours_minutes`. The times use `HH:mm` in each preset. The date format is `dd.MM.yyyy` for "de", `dd/MM/yyyy` for "id" (current) and `yyyy-MM-dd` for "en". The weekday names for `{day}` are Montag … Sonntag for "de", Monday … Sunday for "en", and Senin … Minggu (the current names of `GetIndonesianDay`) for "id".

The decline reasons go into the Grace Request record, so the parent and the widget see the reason in the language of the preset at the time of the decline. The Telegram buttons that the parent uses to pick a reason stay Indonesian (decision 7).

### Session Agent texts (not editable)

| Key | Placeholders | de | en | id (current) |
|---|---|---|---|---|
| `tray_user` | `{username}` | Konto: {username} | Account: {username} | Pengguna: {username} |
| `tray_toggle_widget` | – | ⏱ Widget zeigen / verstecken | ⏱ Show / Hide Widget | ⏱ Tampilkan / Sembunyikan Widget |
| `tray_fullscreen_settings` | – | ⚙️ Einstellungen für Vollbild | ⚙️ Fullscreen Settings | ⚙️ Pengaturan Layar Penuh |
| `tray_request` | – | 🎮 Mehr Bildschirmzeit wünschen... | 🎮 Ask for More Screen Time... | 🎮 Minta Tambahan Waktu Layar... |
| `tray_unrestricted` | – | Parental Control: Konto ohne Grenzen | Parental Control: Unrestricted Account | Parental Control: Akun Tidak Dibatasi |
| `tray_locked` | – | Parental Control: Gesperrt (Pause) | Parental Control: Session Locked (Paused) | Parental Control: Sesi Dikunci (Dijeda) |
| `tray_remaining_hours` | `{hours}`, `{minutes}` | Parental Control: noch {hours} Std. {minutes} Min. | Parental Control: {hours}h {minutes}m left | Parental Control: Sisa {hours}j {minutes}m |
| `tray_remaining_minutes` | `{minutes}` | Parental Control: noch {minutes} {minutes\|Minute\|Minuten} | Parental Control: {minutes} {minutes\|minute\|minutes} left | Parental Control: Sisa {minutes}m |
| `widget_remaining_label` | – | RESTZEIT HEUTE | TIME LEFT TODAY | SISA WAKTU HARI INI |
| `widget_settings_tooltip` | – | Einstellungen für Vollbild | Fullscreen Settings | Pengaturan Layar Penuh |
| `widget_collapse_tooltip` | – | Widget verkleinern | Collapse Widget | Kecilkan Widget |
| `widget_expand_tooltip` | – | Widget vergrößern | Expand Widget | Perbesar Widget |
| `widget_curfew_tooltip` | – | Die Computerzeit endet vor deiner Bildschirmzeit. | The allowed hours end before your daily time. | Waktu dibatasi oleh Jam Malam, bukan kuota harian |
| `widget_curfew_empty` | – | Computerzeit: --:-- | Allowed hours: --:-- | Jam malam: --:-- |
| `widget_locked` | – | ⏸ Gesperrt (Pause) | ⏸ Session Locked (Paused) | ⏸ Sesi Terkunci (Dijeda) |
| `widget_request_button` | – | 🎮 Mehr Zeit wünschen | 🎮 Ask for More Time | 🎮 Minta Tambahan Waktu |
| `widget_declined_title` | – | ❌ Anfrage abgelehnt | ❌ Request Declined | ❌ Permintaan Ditolak |
| `widget_declined_default` | – | Deine Eltern haben die Anfrage abgelehnt. | Your parent declined the request. | Permintaan ditolak oleh orang tua. |
| `status_unrestricted` | – | Konto ohne Grenzen | Unrestricted account | Akun tidak dibatasi |
| `prompt_window_title` | – | Nachricht von deinen Eltern | Message from Your Parent | Pemberitahuan Orang Tua |
| `prompt_title` | – | Nachricht von deinen Eltern | Message from Your Parent | Pesan dari Orang Tua |
| `prompt_urgent_title` | – | 🚨 WICHTIGE NACHRICHT VON DEINEN ELTERN | 🚨 URGENT MESSAGE FROM YOUR PARENT | 🚨 PESAN MENDESAK DARI ORANG TUA |
| `prompt_yes` | – | ✅ Ja / Okay | ✅ Yes / OK | ✅ Ya / Siap |
| `prompt_no` | – | ❌ Nein | ❌ No | ❌ Tidak |
| `prompt_reason_label` | – | Warum nicht? Wähle einen Grund oder schreib selbst: | Pick or type a reason: | Pilih atau tulis alasan penolakan: |
| `prompt_back` | – | Zurück | Back | Kembali |
| `prompt_send` | – | Grund senden 📤 | Send Reason 📤 | Kirim Alasan 📤 |
| `prompt_no_reason` | – | Ohne Grund abgelehnt | Declined without a reason | Ditolak tanpa alasan khusus |
| `fs_window_title` | – | Einstellungen für Vollbild | Fullscreen Display Settings | Pengaturan Tampilan Layar Penuh |
| `fs_heading` | – | ⚙️ Position bei Vollbild (Spiel / Video) | ⚙️ Position in Fullscreen (Game / Video) | ⚙️ Posisi Saat Layar Penuh (Game / Video) |
| `fs_corner_label` | – | Ecke: | Corner: | Posisi Sudut (Corner Dock): |
| `fs_corner_top_right` | – | Oben rechts | Top right | Atas-Kanan (Top-Right) |
| `fs_corner_top_left` | – | Oben links | Top left | Atas-Kiri (Top-Left) |
| `fs_corner_bottom_right` | – | Unten rechts | Bottom right | Bawah-Kanan (Bottom-Right) |
| `fs_corner_bottom_left` | – | Unten links | Bottom left | Bawah-Kiri (Bottom-Left) |
| `fs_monitor_label` | – | Bildschirm (bei mehreren): | Target Screen (Multi-Monitor): | Layar Tujuan (Multi-Monitor): |
| `fs_monitor_item` | `{number}`, `{width}`, `{height}` | Bildschirm {number} [{width}x{height}] | Monitor {number} [{width}x{height}] | Monitor {number} [{width}x{height}] |
| `fs_monitor_item_primary` | `{number}`, `{width}`, `{height}` | Bildschirm {number} (Hauptbildschirm) [{width}x{height}] | Monitor {number} (Primary) [{width}x{height}] | Monitor {number} (Utama) [{width}x{height}] |
| `fs_opacity_label` | – | Durchsichtig im Spiel: | Opacity in Games: | Transparansi Saat Game: |
| `fs_auto_collapse` | – | Bei nur einem Bildschirm klein anzeigen | Collapse to a mini pill on a single screen | Kecilkan ke mini pill pada layar tunggal |
| `fs_preview` | – | 👁️ Position testen (8 Sekunden) | 👁️ Test Position (8 Seconds) | 👁️ Uji / Preview Posisi (8 Detik) |
| `fs_cancel` | – | Abbrechen | Cancel | Batal |
| `fs_save` | – | Speichern | Save | Simpan |

`tray_remaining_hours` uses short forms ("Std.", "Min.", "h", "m"), because the tray tooltip has a limit of 63 characters and the current "id" text uses short forms too. A short form has no plural.

### Request page texts (not editable)

In the HTML, `{status}` gets `<strong>` and the encoded status, and `{time}` gets " (HH:mm)" or nothing.

| Key | Placeholders | de | en | id (current) |
|---|---|---|---|---|
| `page_title` | – | Parental Control — Mehr Zeit & Zeitplan | Parental Control — Time & Schedule Requests | Parental Control — Minta Waktu & Jadwal |
| `heading` | – | 🎮 Bildschirmzeit | 🎮 Screen Time | 🎮 Waktu Layar |
| `account` | – | Konto: | Account: | Akun: |
| `session_inactive` | – | ⏹ Nicht angemeldet (Zeit läuft nicht) | ⏹ Session Inactive (Time Paused) | ⏹ Sesi Tidak Aktif (Waktu Dijeda) |
| `session_locked` | – | ⏸ Gesperrt (Zeit läuft nicht) | ⏸ Session Locked (Time Paused) | ⏸ Sesi Terkunci (Waktu Dijeda) |
| `session_active` | – | ▶ Zeit läuft | ▶ Session Active | ▶ Sesi Aktif |
| `timer_label` | – | Deine Restzeit heute | Time Left Today | Sisa Waktu Penggunaan Hari Ini |
| `timer_curfew` | – | Computerzeit: | Allowed hours: | Jadwal Jam Malam: |
| `timer_over` | – | (vorbei) | (over) | (Habis) |
| `offline` | – | ⚠️ Der Computer ist offline. Du kannst eine Anfrage nur mit Internet senden. | ⚠️ The computer is offline. You can send a request only with an internet connection. | ⚠️ Komputer sedang offline. Permintaan hanya dapat dikirim saat terhubung ke internet. |
| `status_pending` | – | Wartet auf Antwort | Waiting for Approval | Menunggu Persetujuan |
| `status_approved` | – | Erlaubt | Approved | Disetujui |
| `status_declined` | – | Abgelehnt | Declined | Ditolak |
| `requests_disabled` | – | ⚠️ Deine Eltern haben Anfragen für mehr Zeit ausgeschaltet. | ⚠️ Requests for extra time are turned off by the administrator. | ⚠️ Permintaan waktu tambahan saat ini dinonaktifkan oleh administrator. |
| `request_pending` | `{status}` | ℹ️ Deine Anfrage wartet noch auf eine Antwort (Status: {status}). Warte bitte, bevor du eine neue sendest. | ℹ️ You still have a request that waits for a decision (Status: {status}). Please wait for the decision before you send another one. | ℹ️ Anda masih memiliki permintaan yang sedang menunggu keputusan administrator (Status: {status}). Harap tunggu keputusan sebelum mengirim lagi. |
| `request_max_reached` | `{max}`, `{status}` | ℹ️ Du hast heute schon {max} {max\|Anfrage\|Anfragen} gesendet. Mehr geht heute nicht (letzter Status: {status}). | ℹ️ You reached the maximum of {max} {max\|request\|requests} for today (Last status: {status}). | ℹ️ Anda telah mencapai batas maksimal ({max}) permintaan untuk hari ini (Status terakhir: {status}). |
| `request_used` | `{count}`, `{max}`, `{status}` | ℹ️ Du hast heute {count} von {max} {max\|Anfrage\|Anfragen} benutzt (letzter Status: {status}). | ℹ️ You used {count} of {max} {max\|request\|requests} today (Last status: {status}). | ℹ️ Anda telah menggunakan {count} dari {max} permintaan hari ini (Status terakhir: {status}). |
| `decline_title` | `{time}` | Deine Eltern haben abgelehnt{time} | Decline Note from Your Parent{time} | Catatan Penolakan dari Orang Tua{time} |
| `schedule_title` | – | 📅 Dein Wochenplan | 📅 Weekly Play Schedule | 📅 Jadwal Main Mingguan |
| `schedule_default` | `{minutes}`, `{start}`, `{end}` | Standard: {minutes} {minutes\|Minute\|Minuten} ({start}–{end} Uhr) | Default: {minutes} {minutes\|minute\|minutes} ({start}–{end}) | Standar: {minutes}m ({start}–{end}) |
| `dot_exception` | – | Besonderer Tag | Special Exception | Pengecualian Khusus |
| `dot_custom` | – | Eigener Plan | Custom Schedule | Jadwal Khusus |
| `badge_standard` | – | Standard | Default | Standar |
| `badge_custom` | – | Eigener Plan | Custom | Jadwal Khusus |
| `badge_exception` | – | Ausnahme | Exception | Pengecualian |
| `today_suffix` | – | (heute) | (Today) | (Hari Ini) |
| `exception_today` | – | Ausnahme für heute | Exception for today | Pengecualian Hari Ini |
| `metric_quota` | – | Bildschirmzeit | Screen Time | Kuota Waktu Layar |
| `metric_window` | – | Computerzeit | Allowed Hours | Rentang Jam Main |
| `minutes_with_duration` | `{minutes}`, `{duration}` | {minutes} {minutes\|Minute\|Minuten} ({duration}) | {minutes} {minutes\|minute\|minutes} ({duration}) | {minutes} Menit ({duration}) |
| `tab_extension` | – | ⏱️ Mehr Zeit für heute | ⏱️ More Time Today | ⏱️ Tambah Waktu Hari Ini |
| `tab_schedule` | – | 📅 Plan für einen anderen Tag | 📅 Plan a Future Schedule | 📅 Rencana Jadwal Mendatang |
| `ext_choose` | – | Wie viel mehr Zeit? | Choose extra time: | Pilih Tambahan Waktu: |
| `ext_reason_label` | – | Warum brauchst du mehr Zeit? | Why do you need more time? | Alasan meminta tambahan waktu: |
| `ext_reason_placeholder` | – | Zum Beispiel: Ich mache gerade Hausaufgaben, ich spiele mit Freunden ... | For example: I am finishing my homework, I play a game with friends... | Contoh: Sedang menyelesaikan tugas sekolah, bermain game bersama teman... |
| `ext_submit` | – | Anfrage an meine Eltern senden | Send Request to Parent | Kirim Permintaan ke Orang Tua |
| `ext_sending` | – | Anfrage wird gesendet ... | Sending request... | Mengirim Permintaan... |
| `ext_failed` | – | Die Anfrage hat nicht geklappt. | Could not send the request. | Gagal mengirim permintaan. |
| `sched_date` | – | Für welchen Tag? | Target date: | Tanggal Sasaran: |
| `sched_minutes` | – | Wie viel Bildschirmzeit? | Total time wanted: | Kuota Total Diinginkan: |
| `sched_start` | – | Ab wann? | Start time: | Jam Boleh Mulai: |
| `sched_end` | – | Bis wann? | End time (evening): | Jam Selesai (Malam): |
| `sched_reason_label` | – | Warum brauchst du diesen Plan? | Reason for the schedule change: | Alasan perubahan jadwal mendatang: |
| `sched_reason_placeholder` | – | Zum Beispiel: Feiertag, online mit Freunden lernen ... | For example: a public holiday, an online study group... | Contoh: Hari libur nasional, rencana belajar kelompok online, dll... |
| `sched_submit` | – | Plan an meine Eltern senden | Send Schedule Request to Parent | Ajukan Perubahan Jadwal ke Orang Tua |
| `sched_sending` | – | Plan wird gesendet ... | Sending schedule... | Mengajukan Jadwal... |
| `sched_failed` | – | Der Plan hat nicht geklappt. | Could not send the schedule request. | Gagal mengajukan jadwal. |
| `connect_failed` | – | Keine Verbindung zu Parental Control. | Could not connect to the parental control service. | Gagal terhubung ke layanan parental control. |
| `api_invalid_schedule` | – | Die Minuten sind ungültig (1–480) oder der Grund fehlt. | The number of minutes is not valid (1–480 minutes), or the reason is missing. | Jumlah menit tidak valid (1–480 menit) atau alasan harus diisi. |
| `api_invalid_extension` | – | Die Minuten sind ungültig (1–120) oder der Grund fehlt. | The number of minutes is not valid (1–120 minutes), or the reason is missing. | Jumlah menit tidak valid (1–120 menit) atau alasan harus diisi. |
| `api_no_user` | – | Kein eingeschränktes Konto gefunden. | No restricted user found. | Pengguna terbatas tidak ditemukan. |
| `api_disabled` | – | Deine Eltern haben Anfragen für mehr Zeit ausgeschaltet. | Requests for extra time are turned off by the administrator. | Permintaan tambahan waktu dinonaktifkan oleh administrator. |
| `api_pending` | – | Deine letzte Anfrage wartet noch auf eine Antwort. | You still have a request that waits for a decision. | Anda masih memiliki permintaan yang sedang menunggu keputusan administrator. |
| `api_max_reached` | `{max}` | Du hast heute schon {max} {max\|Anfrage\|Anfragen} gesendet. | You reached the maximum of {max} {max\|request\|requests} for today. | Anda telah mencapai batas maksimal ({max}) permintaan untuk hari ini. |
| `api_invalid_date` | – | Das Datum ist ungültig. | The target date is not valid. | Tanggal sasaran tidak valid. |
| `api_schedule_sent` | `{date}`, `{minutes}` | Dein Plan für den {date} ({minutes} {minutes\|Minute\|Minuten}) ist bei deinen Eltern. | Your schedule request for {date} ({minutes} {minutes\|minute\|minutes}) was sent to your parent. | Pengajuan jadwal baru untuk {date} ({minutes}m) berhasil dikirim ke orang tua. |
| `api_extension_sent` | `{minutes}` | Deine Anfrage für +{minutes} {minutes\|Minute\|Minuten} ist bei deinen Eltern. | Your request for +{minutes} {minutes\|minute\|minutes} was sent to your parent. | Permintaan tambahan waktu +{minutes} menit berhasil dikirim ke orang tua. |
| `api_invalid` | – | Die Anfrage ist ungültig. | The request is not valid. | Permintaan tidak valid. |

The day codes of the schedule strip are Mo, Di, Mi, Do, Fr, Sa, So for "de", Mon … Sun for "en" and Sen, Sel, Rab, Kam, Jum, Sab, Min for "id". The day names come from `SessionTexts`.

The extension options are "+" and the duration of `SessionTexts` ("+15 Minuten", "+1 Stunde"). The schedule options use `minutes_with_duration` ("90 Minuten (1 Stunde 30 Minuten)"). The day card uses `minutes_with_duration` from 60 minutes, and the duration alone below 60 minutes. Thus the "id" page shows "+1 jam" and "90 Menit (1 jam 30 menit)" in place of "+1 Jam" and "90 Menit (1.5 Jam)". This is a small change of the "id" page.

## Risks / Trade-offs

- [After a switch, the parent's edited texts stay in the old language, so the child sees mixed languages.] → The user accepted this. The WPF Admin shows the kept keys after the switch. The parent can then edit or empty them.
- [A parent who typed a text that is equal to a default of another preset loses it on a switch.] → That text is a default text, so the parent gets the default text of the new preset. The effect is small.
- [A parent who types `{minutes|...}` wrong sees the raw placeholder in the message.] → `Fill` keeps an unknown placeholder visible, so the parent sees the error. The default texts show the correct form.
- [A default text of an earlier version that is not in the catalog counts as edited.] → `EarlierDefaults` has the four "en" texts that this change replaces. The other upstream texts and the stage-1 category texts are in the catalog.
- [The "en" preset changes texts that were Indonesian (logon banner, prompt reasons, popups, agent, request page) and adds word forms.] → This is a correction. The proposal marks it as **BREAKING**. The user approved it.
- [The agent shows German texts for up to one poll (5 seconds) on an "id" or "en" install after it starts.] → The agent polls at once after its start, so the time is short.
- [The child can reach `127.0.0.1:5050`.] → The language changes only the text. No limit depends on the preset, and the preset comes only from `app_settings`, which the child cannot write.

## Migration Plan

- No manual step. `DatabaseManager.Initialize` runs at each start of the service and of the WPF Admin. A new database gets "de". A database with a preset keeps it. An "en" install gets the word forms in its four unedited texts with `{minutes}`.
- Rollback: an earlier version reads "de" as "id" in `ApplyLanguagePreset` and in Telegram. The stored German texts stay and the child still sees them, because the earlier `GetMessage` applies the preset only for an empty text. A stored text with `{minutes|Minute|Minuten}` then shows the raw word form, because the earlier fill does not know it. A preset switch in the earlier WPF Admin writes the earlier texts again.

## Open Questions

- The German wording: the user reviews the tables above. A change of a word does not change the specs, the approach or the tasks.
