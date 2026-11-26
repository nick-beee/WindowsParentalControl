# Proposal

## Why

The child is 10 years old and speaks German. Today a new install shows the child texts in Indonesian: the preset "id" is the default, and the only other preset is "en". The parent must then translate each text by hand. GitHub issue #11 asks for a German preset that is the default for a new install.

## What Changes

- New language preset **"de"** with German texts for each child message: the 10 message settings (`msg_*`), the reasons that the child can pick to decline a parent prompt (`prompt_response_presets`), and the names of the App Categories in a message ("Spiele", not "Games").
- **"de" is the default** for a new install. The service writes the preset and the German texts when the database has no preset yet. The first-launch question of the WPF Admin ("Yes = Indonesian, No = English") goes away.
- The session messages that the service sends with fixed text also follow the preset: the logon banner, the titles of the warnings, the countdown text of a remote lock, the "session ended" text, and the messages about an approved or declined request. Today these texts are fixed in Indonesian or in English.
- **A preset switch keeps the texts that the parent edited.** It replaces only a text that is empty or that is equal to a default text of a preset. Today a switch replaces all texts.
- **A missing text gets the default of the current preset for that one key only.** Today one empty text makes the service apply the whole preset again, and that replaces all edited texts.
- An existing install keeps its preset and its texts after the update. The parent can switch to German with a new "Deutsch" button in the WPF Admin settings.
- The Telegram texts for the parent are Indonesian for the preset "id" and English for each other preset. Today "de" would give Indonesian Telegram texts, because the code knows only "en" and "id".
- **BREAKING** (small): the preset "en" gets English texts for the session messages and the prompt reasons that were Indonesian before. A parent with "en" sees English texts there after the update.

## Out of scope

- The language of the admin UIs (WPF Admin and web admin). They stay as they are. Only the child texts change.
- The texts of the Session Agent windows (the widget, the prompt dialog, the full-screen settings) and of the child request page (`RequestPage.cs`). These texts are fixed in Indonesian in XAML and HTML. They need a way to send the preset to the agent and to the page. A follow-up change does this (see design.md).
- German Telegram texts for the parent.
- A preset picker in the web admin. The web admin edits two texts and the prompt reasons. The WPF Admin stays the place to pick a preset.
- An automatic switch of an existing install from "id" or "en" to "de".
- The texts in the event log and in the service log.

## Capabilities

### New Capabilities
- `child-messages`: the language presets of the texts that the child sees from the service, the default preset of a new install, the rules that keep the texts of the parent on an update, a preset switch and a missing text, and the language of the parent's Telegram texts per preset.

### Modified Capabilities
<!-- None. category-limits requires that the suspension message names the category and that the parent can change the text. That stays true: the "de" preset names the category in German. -->

## Impact

- `src/ParentalControl.Domain`: a new folder `Messages` with the preset catalog (all texts of "de", "en" and "id"), the preset switch rule, the category names and the placeholder fill. No WinAPI, no SQLite.
- `src/ParentalControl.Core/Data/SettingsRepository.cs`: the presets move to the Domain catalog. `ApplyLanguagePreset` and `GetMessage` change.
- `src/ParentalControl.Core/Data/DatabaseManager.cs` and `CategoryRepository.cs`: the seed of the preset and its texts.
- `src/ParentalControl.Core/Platform/NotificationManager.cs`, `src/ParentalControl.Core/Services/TelegramBotService.cs`, `src/ParentalControl.Service/Web/WebServerHost.cs`, `src/ParentalControl.Service/CategoryEnforcer.cs`: the session messages with fixed text and the category names.
- `src/ParentalControl.Admin`: the "Deutsch" button, and the first-launch question goes away.
- Tests: new xUnit tests in `tests/ParentalControl.Domain.Tests`. The assert on "selesai game" in `tests/ParentalControl.Tests/Program.cs` changes.
- `GLOSSARY.md`: the new term **Language Preset**.
- No new table and no new column. The preset stays in `app_settings.language_preset`.
