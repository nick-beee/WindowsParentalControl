# Tasks

## 1. Text catalog in ParentalControl.Domain

- [ ] 1.1 Add `LanguagePreset` in `src/ParentalControl.Domain/Messages` (constants `De`, `En`, `Id`, `All`, `Default = De`, `Normalize`, `ParentLanguage`). Verify with xUnit tests in `tests/ParentalControl.Domain.Tests/LanguagePresetTests.cs`: `Normalize(null)`, `Normalize("")`, `Normalize("fr")` give "de"; `Normalize("EN")` gives "en"; `ParentLanguage("de")` gives "en"; `ParentLanguage("id")` gives "id". Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.2 Add `TextTemplate.Fill` and `TextTemplate.Placeholders`. Verify with tests: `Fill("+{minutes} Min.", minutes=5)` gives "+5 Min."; an unknown placeholder stays as it is; `Placeholders` of `msg_curfew_warn` (en) gives `{curfew_end, minutes}`. Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.3 Add `ChildMessageKeys` and `MessageCatalog.Defaults(preset)` with the texts of the "Child message keys" tables in design.md. Copy the "en" and "id" `msg_*` texts letter for letter from `SettingsRepository`. Verify with tests: each preset has each key; each text is not empty; the placeholders of each key are the same in "de", "en" and "id"; the "de" texts equal the design table. Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.4 Add `SessionTexts.For(preset)` with the "Fixed session texts" table, the weekday names and the date formats of design.md. Copy the "id" texts letter for letter from the current code. Verify with tests: each preset has each key, the placeholders match across presets, and there are 7 weekday names per preset. Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.5 Add `CategoryNames.For(category, preset)` with the table of design decision 5. Verify with tests: each `AppCategory` has a name in each preset; `For(Games, "de")` is "Spiele"; `For(Games, "en")` is "Games". Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.6 Add `PresetSwitch.Plan(stored, target)` (design decision 3). Verify with tests: a missing, empty or white-space text is written; an "id" default is replaced by the "de" default; an edited text is kept and is in the kept list; a `prompt_response_presets` value with `\r\n` and an extra trailing line break is still found as the "id" default. Run `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.7 Add the term **Language Preset** to `GLOSSARY.md`: a named set of default child texts ("de", "en", "id"); new installs use "de"; it does not change the admin UI language. Verify that the entry exists and that the specs and the code use the term.

## 2. Settings and seed in ParentalControl.Core

- [ ] 2.1 Change `SettingsRepository`: the `Key*` constants of the child messages become aliases of `ChildMessageKeys`; remove `CategoryMessagesEn`, `CategoryMessagesId` and the texts in `ApplyLanguagePreset`; add `GetLanguagePreset()`; `ApplyLanguagePreset(preset)` uses `PresetSwitch.Plan` and returns the kept keys; `GetMessage` uses the default of its own key only (design decision 4) and `TextTemplate.Fill`; `GetPromptResponsePresets` uses the default of the current preset. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds and that no `Get(KeyLanguagePreset, "id")` is left (`grep -rn 'KeyLanguagePreset, "id"' src` gives no result).
- [ ] 2.2 Add `SeedChildMessages` to `DatabaseManager.Initialize` before `CategoryRepository.SeedIfNeeded` (design decision 2), and remove the category text seed from `CategoryRepository.SeedIfNeeded`. Verify on the Mac that `dotnet build -p:EnableWindowsTargeting=true` succeeds.
- [ ] 2.3 Update `tests/ParentalControl.Tests/Program.cs`: replace the assert on "selesai game" with asserts for a new database: `language_preset` is "de"; `GetMessage(msg_limit_reached)` is the German default; the prompt reasons contain "Hausaufgaben"; after `Set(msg_remote_lock, "Abendessen!")` and `ApplyLanguagePreset("id")`, `msg_remote_lock` is still "Abendessen!" and `msg_limit_reached` is the Indonesian default; an empty `msg_bonus_granted` gives the default of the current preset and does not change `msg_remote_lock`. Verify that CI runs the test project green on `windows-latest` (the run prints `ALL ASSERTIONS PASSED`).

## 3. Session messages in the service

- [ ] 3.1 `NotificationManager`: `SendLogonBanner`, `SendWarning` and `SendCurfewWarning` use `SessionTexts` of the current preset. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds and that `NotificationManager.cs` has no Indonesian text left (`grep -n "jam\|menit\|Anda" src/ParentalControl.Core/Platform/NotificationManager.cs` gives no result).
- [ ] 3.2 `CategoryEnforcer`: put `CategoryNames.For(category, preset)` into `{name}` and `{category}` for a category message. Keep the app name for an App Limit warning. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds.
- [ ] 3.3 `WebServerHost`: the lock countdown and title, the terminate text and the parent message titles use `SessionTexts`. `/api/admin/settings` GET gives the default prompt reasons of the current preset. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds and that `grep -n "Layar akan otomatis\|terminated this session\|Mendesak" src/ParentalControl.Service/Web/WebServerHost.cs` gives no result.
- [ ] 3.4 `TelegramBotService`: the approve and decline popups to the child and the decline reasons use `SessionTexts` (date format and weekday names too). Each `lang` read uses `LanguagePreset.ParentLanguage(GetLanguagePreset())`. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds and that `grep -n '"id")' src/ParentalControl.Core/Services/TelegramBotService.cs` finds no preset read with the fallback "id".

## 4. WPF Admin

- [ ] 4.1 Add the button "🇩🇪 Deutsch Preset" and the command `ApplyPresetDe` to `SettingsView.xaml` and `SettingsViewModel`. The status line of each preset button shows the kept keys. `LoadSettings` uses `GetLanguagePreset()`. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds.
- [ ] 4.2 Remove `MainViewModel.CheckFirstLaunchLanguagePreset` and its call. Verify that `dotnet build -p:EnableWindowsTargeting=true` succeeds and that `grep -rn CheckFirstLaunchLanguagePreset src` gives no result.

## 5. Checks on the test VM (Windows only)

Before the install, check that the snapshot `test-base` exists. Use the Proxmox console, not RDP.

- [ ] 5.1 New install: roll back to `test-base`, install the build, sign in as `Kid`. Expected: the logon banner is German ("Hallo, Kid!"), `language_preset` in the database is "de", and the WPF Admin shows no language question at its first start.
- [ ] 5.2 Warnings and lock: give `Kid` a session limit that ends in 6 minutes. Expected: the German warnings at 5 and 1 minutes with the title "Parental Control — Bildschirmzeit", then "Deine Bildschirmzeit für heute ist um. Der Computer wird jetzt gesperrt. …", and the session locks. No program closes.
- [ ] 5.3 Category limit: set the Games limit so that it ends in 6 minutes and run a game as `Kid`. Expected: the warning names "Spiele" and says "in 5 Min." and "in 1 Min.", then the suspension message names "Spiele". The game stays open and is paused.
- [ ] 5.4 Remote lock and bonus from the web admin: lock with a 15-second countdown, then grant 15 Bonus Minutes. Expected: "Zeit für eine Pause! …" with "(Der Computer wird in 15 Sekunden gesperrt.)", then "Deine Eltern haben dir +15 Min. Bildschirmzeit geschenkt!". The umlauts show correctly.
- [ ] 5.5 Update of an Indonesian install: roll back to `test-base`, install the current `main` build, apply the Indonesian preset, edit `msg_remote_lock` to "Abendessen!", then install this build. Expected: the preset is still "id" and each text is the same. Then click "🇩🇪 Deutsch Preset". Expected: all unedited texts are German, `msg_remote_lock` is still "Abendessen!", and the status line names `msg_remote_lock`.
- [ ] 5.6 Telegram (if a bot is set up on the VM): with "de", sign in as `Kid`. Expected: the parent gets the English sign-in notification.

## Workflow follow-up

- `/opsx:verify`, then `/opsx:archive` after the user approves the VM checks.
- A follow-up change for the texts of the Session Agent and of the child request page (design decision 9).
