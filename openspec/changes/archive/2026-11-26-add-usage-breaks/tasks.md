# Tasks

PR #20 (`add-german-child-texts`) and PR #21 (`require-admin-sign-in`) are on `main`. The Admin Session gate of PR #21 protects the routes of group 7.

For each VM step: check that the snapshot `test-base` exists before you install the service on VM 102. Do the lock and unlock steps in the Proxmox console, not over RDP. For a short test, set the Break Rule of `Kid` to N = 10 and M = 5 (directly in `break_rules` before group 7, through the API after it).

## 1. Pure break logic in the Domain

- [x] 1.1 Add `BreakRule` in `src/ParentalControl.Domain/Breaks` (Enabled, UseMinutes, BreakMinutes, the default 45/10/on, the range check N 10–240 and M 5–120). Verify: xUnit tests for the default and for each range limit (9, 10, 240, 241 and 4, 5, 120, 121) pass with `dotnet test tests/ParentalControl.Domain.Tests`.
- [x] 1.2 Add `BreakState` and `BreakPlanner` with the inputs and actions of design D13 (count, Rest start, Rest end, service start, tick, rule change, end by the parent). Verify: `dotnet test` passes.
- [x] 1.3 Add xUnit tests for the count and the Rest: 30 counted minutes give 30; a Rest of M − 1 minutes keeps the value; a Rest of M minutes gives 0; a tick gap of M minutes gives 0; a service start with an unlocked session keeps the value; a service start after a reboot later than the last use, M minutes or more after the last use, gives 0. Verify: the tests pass.
- [x] 1.4 Add xUnit tests for the warnings and the start: the 5-minute and the 1-minute warning come one time per cycle; no warning when the Allowance Time or the Schedule Window ends at or before the Break; the Break starts at N; no Break when the flag "Allowance Time or Schedule Window locks in this tick" is set. Verify: the tests pass.
- [x] 1.5 Add xUnit tests for the end and the changes: the end is `min(start + M, Schedule Window end)`; Continuous Use is 0 after the end; the end by the parent ends only the Break with the same start time; rule off ends the Break; a new N gives `min(value, N − 5)` and resets the warnings; a new M keeps the end of a running Break; Bonus Minutes are not an input. Verify: the tests pass.

## 2. Data

- [x] 2.1 Add the tables `break_rules` and `break_state` (design D12) to the schema block in `DatabaseManager`. Verify: `dotnet build -p:EnableWindowsTargeting=true` passes, and on the VM the service starts on an earlier database and creates both tables (check with `sqlite3` or a test in `tests/ParentalControl.Tests`).
  - Result: VM 102, 2026-11-26: the database of `main` (#35) had no break tables. After the deploy of this branch, `sqlite_master` lists `break_rules` and `break_state`, and the first tick wrote the row of `Kid`.
- [x] 2.2 Add `BreakRepository` in Core: read the rule (default if no row), write the rule, read and write the state. Verify: an assertion test in `tests/ParentalControl.Tests` writes and reads a rule and a state, and `scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED`.
  - Result: `scripts/vm-test.sh` printed `ALL ASSERTIONS PASSED` on VM 102 (2026-11-26), and CI passes.
- [x] 2.3 Add `BREAK_STARTED` and `BREAK_ENDED` to `EventType`, and the settings key `telegram_notify_break` (default "false") to `SettingsRepository`. Verify: the build passes, and the admin event log shows the new event types after group 5.
  - Result: The web admin has no event log view, so the check read the `events` table directly. On VM 102 it has `BREAK_STARTED` ("Until 20:44 (5 min)") and `BREAK_ENDED` with the reasons `elapsed`, `parent` and `schedule end`. `telegram_notify_break` is "false" by default.

## 3. Count Continuous Use and the Rest in the service

- [x] 3.1 In `SessionTracker`, add the elapsed minutes of each `UsageRepository.AddMinutes` call for a Restricted User (in `TickAllSessions` and `FlushSessionTime`, not the clock-tamper penalty) to Continuous Use through `BreakEnforcer`. During a running Break, add no minutes to either count. Verify: the build passes; on the VM, after 10 minutes of unlocked use as `Kid`, `break_state.continuous_use_minutes` is 10 and `usage.minutes_used` grew by 10.
  - Result: From 20:28:52 to 20:39:31, `continuous_use_minutes` went from 0 to 10 and `usage.minutes_used` from 32 to 42. The two counts moved by the same minutes in each tick.
- [x] 3.2 Report the Rest start (`OnSessionLock`, `OnUserLogoff`, `OnSleep`, only for a session tracked as unlocked) and the Rest end (an accepted `OnSessionUnlock`, `OnUserLogon`, `OnWake`) to `BreakEnforcer` with the event time. Verify on the VM: with M = 5, a lock of 6 minutes (Win+L in the Proxmox console) sets `continuous_use_minutes` to 0; a lock of 3 minutes keeps the value. Note the result for issue #9 if the poll shows the session as unlocked.
  - Result: Win+L in the console with M = 5. A lock of 8.5 minutes (20:48:24–20:57:00) set the count from 7 to 0. A lock of about 3 minutes (20:57:17–21:00:25) kept 7. The poll showed the correct lock state (no #9 effect).
- [x] 3.3 On the service start, report the service start per Restricted User, with or without an unlocked session, and with the boot time of the PC (design D5). Verify on the VM: restart the service while `Kid` is unlocked with 7 counted minutes; `continuous_use_minutes` is still 7 after the restart.
  - Result: A service restart at 21:00 with 7 counted minutes and `Kid` unlocked: the log shows `ContinuousUseMinutes = 7` at the start, and the table keeps 7.

## 4. Start and end a Break

- [x] 4.1 Add `BreakEnforcer` in the Service project and call it from `UsageMonitorWorker.ProcessTick` after the checks of the Allowance Time and the Schedule Window, with the flag of design D8, also for a user without a schedule. Check the end of a running Break each tick for each Restricted User. Send the warnings with `NotificationManager.SendMessage`. Verify on the VM: with N = 10, `Kid` gets the 5-minute warning at 5 minutes and the 1-minute warning at 9 minutes, each one time.
  - Result: With N = 10: the 5-minute warning at count 5 (20:34:31) and the 1-minute warning at count 9 (20:38:31), one time each (log and screenshots).
- [x] 4.2 At the start of a Break: send `msg_break_start`, call `SessionManager.LockSession` and `SessionTracker.OnSessionLock`, store the start and the end, write `BREAK_STARTED`. Verify on the VM: with N = 10 and M = 5, the session locks within 60 seconds after 10 minutes of use; the message names the end time; Notepad with unsaved text and a game still run with the same process IDs after the Break.
  - Result: The Break started at 20:39:31 and the session locked at 20:39:36. The message said "… für 5 Minuten gesperrt, bis 20:44 Uhr …". Notepad (pid 1412, unsaved text) and TestGame (pid 10348) had the same PIDs after the Break; the clock of TestGame did not stop, and `state.ps1` shows 0 suspended threads. In the 4.4 run, the lock came 35 s after the start, because the 5-minute message of the Schedule Window came in the same tick.
- [x] 4.3 At the end time: end the Break, set Continuous Use to 0, write `BREAK_ENDED` with the reason. Verify on the VM: after the end time, an unlock in the Proxmox console works and stays unlocked.
  - Result: The Break ended at 20:45:28 (`elapsed`, one tick after the end time 20:44:31). A reconnect at 20:45:53 stayed unlocked for more than 60 s.
- [x] 4.4 Clamp the end of a Break to today's Schedule Window end. Verify on the VM: with a Schedule Window that ends 3 minutes after the Break start and M = 5, the Break ends at the window end and an unlock gets the Schedule Window message and the lock.
  - Result: The Schedule Window end was set to 21:06. The Break started at 21:03:50 with the end 21:06 (3 min, not 5). It ended at 21:06:25 (`schedule end`). An unlock at 21:06:33 got "Anmelden geht gerade nicht: Jetzt ist keine Computerzeit." and the lock.
- [x] 4.5 When the Allowance Time ends in the same tick as the Break would start, start no Break. Verify on the VM: set the Allowance Time so that it ends at the same minute as N; the child gets only the daily screen time message, and the event log has no `BREAK_STARTED`.
  - Result: The daily limit was set so that the Allowance Time ends in the tick of count 10. At 21:01:45 the log has `LIMIT_REACHED` and "Daily limit reached - workstation locked" only. The events have no `BREAK_STARTED`.

## 5. Keep the session locked during a Break

- [x] 5.1 In `SessionTracker.OnSessionUnlock`, lock again during a Break and send `msg_break_running` at most one time in 10 seconds. Verify on the VM (Proxmox console): an unlock during a Break shows the message and locks again; measure the time to the lock and note it in issue #14.
  - Result: A reconnect (tscon as SYSTEM) at 20:39:58.24 during the Break showed "Die Pause dauert noch bis 20:44 Uhr (5 Minuten). …" and locked again at 20:40:03.68 (5.4 s). For #14: the re-lock takes about 5 s, because the message waits before the lock.
- [x] 5.2 In `SessionTracker.OnUserLogon`, lock a new session of the user during a Break with `LockSession`, not `ForceLogoff`. Verify on the VM: sign out during a Break and sign in again; the session locks after the sign-in and is not signed out.
  - Result: During a Break, `logoff` of `Kid` at 21:08:42, then a sign-in in the console. The log has "Sign-in during a Break … locking the session" at 21:09:03, and session 2 was locked at 21:09:11. `Kid` was not signed out.
- [x] 5.3 In the tick, lock a session of the user that shows as unlocked during a Break. Verify on the VM: the session is locked again within 60 seconds also if the unlock event is lost (for example after a service restart during the Break); `LockSession` on a session that is already disconnected causes no error in the log.
  - Result: The service was stopped during the Break, the session was connected again while it was down (the unlock event was lost), and the service was started. At 20:40:23 the log has "shows as unlocked during a Break; locking again", and the lock came at 20:40:28. The log has no error or warning since the deploy. A separate check at 22:19 disconnected the session of `Kid` with `tsdiscon` and then called `POST /api/admin/lock-session` (the same `SessionManager.LockSession`). The log has the lock and no error or warning.
- [x] 5.4 After a service restart during a Break, continue the Break until its stored end. Verify on the VM: restart the service during a Break; an unlock before the end is locked again; an unlock after the end works.
  - Result: After the restart at 20:40:23, the stored Break (end 20:44:31) continued: the tick locked the session again, and the unlock after the end (4.3) worked.
- [x] 5.5 Confirm that a Break uses no Allowance Time. Verify on the VM: `usage.minutes_used` and `category_usage` do not grow during a Break of 5 minutes.
  - Result: During the Break from 20:39:31 to 20:45:28, `usage.minutes_used` stayed at 42 and the sum of `category_usage` stayed at 10.

## 6. Child texts

- [x] 6.1 Add `msg_break_warn`, `msg_break_start` and `msg_break_running` to `ChildMessageKeys` (also to `All`) and to `MessageCatalog` with the "de", "en" and "id" texts of design D15, and `break_title` to `SessionTexts`. Verify: the existing catalog tests (same placeholders in each preset) pass for the new keys with `dotnet test`.
- [x] 6.2 Add xUnit tests for the three German scenario strings of the spec, with `{break_end}` as `HH:mm` and the remaining minutes rounded up. Verify: the tests pass.
- [x] 6.3 Use the catalog texts in `BreakEnforcer` and `SessionTracker`. Verify on the VM with the preset "de": the warning, the start and the unlock message show the German texts with the correct word form for 1 minute.
  - Result: Preset "de": the screenshots show "In 5 Minuten ist Pause. …", "In 1 Minute ist Pause. …", "Zeit für eine Pause! Der Computer wird jetzt für 5 Minuten gesperrt, bis 20:44 Uhr. …" and "Die Pause dauert noch bis 20:44 Uhr (5 Minuten). …", each with the title "Parental Control — Pause".

## 7. Admin API (after PR #21)

- [x] 7.1 Add `GET /api/admin/breaks`, `PUT /api/admin/breaks/{userId}` and `POST /api/admin/breaks/{userId}/end` with the shapes and status codes of design D14. Verify: an xUnit test of `AdminRoutePolicy` classifies the three paths as admin routes, and the build passes.
- [x] 7.2 Put the range check of `BreakRule` in the PUT route and the rule change of design D11 in `BreakEnforcer`. Verify: xUnit tests from group 1 cover the logic; on the VM, `PUT` with `useMinutes` 5 gives 400 and no change, and a change of N from 60 to 30 with 40 counted minutes gives 25.
  - Result: From the Mac with an Admin Session: `PUT` with `useMinutes` 5 gave 400, and `GET` showed the old rule. N from 60 to 30 with 40 counted minutes gave 25.
- [x] 7.3 Add `telegram_notify_break` and the three break texts (`msg_break_warn`, `msg_break_start`, `msg_break_running`) to `GET` and `POST /api/admin/settings`, as for the category texts. Verify on the VM: the key changes through the API with an Admin Session.
  - Result: `POST /api/admin/settings` with `telegramNotifyBreak` true, then false, changed `telegram_notify_break` in `app_settings`. Without a cookie, the same POST gave 401.
- [x] 7.5 Add the card "Pausen" to the React admin (`web/admin`, design D16): the Break Rule per Restricted User (on/off, N, M with the ranges of D3), the Continuous Use or the end of a running Break, a save button and an end button with a confirmation, and `telegramNotifyBreak` and the three break texts in the settings dialog. German texts in `src/i18n/de.ts`. Verify: Vitest tests for the card and the settings dialog pass with `npm test`, lint and the type check pass, and on the VM the page reads, changes and ends a Break through a headless browser.
  - Result: `npm test` (80 tests), lint and the type check pass. On VM 102, the page showed "3 / 10 Min. am Stück", then "⏸️ Pause bis 20:44 Uhr" with the end button. The confirmation said that the PC stays locked, and after the end the row showed "0 / 10 Min. am Stück".
- [x] 7.4 Verify the gate on the VM: as `Kid`, `curl -X POST http://127.0.0.1:5050/api/admin/breaks/{userId}/end` without a cookie gives 401 and the Break continues; with an Admin Session it gives 200 and the Break ends; a second call gives 409.
  - Result: As `Kid` (scheduled task in the session of `Kid`), `curl.exe -X POST http://127.0.0.1:5050/api/admin/breaks/1/end` gave 401, and the Break continued. The end button of the React page (headless Chromium from the Mac, Admin Session) ended the next Break (`BREAK_ENDED parent`), and the session stayed locked. A second `POST` gave 409.

## 8. Telegram

- [x] 8.1 Add the command `/break` to `TelegramBotService`: one line per Restricted User with a Break Rule, and a button `brk_end:<userId>:<breakStartUnixSeconds>` for a running Break. Texts in English and Indonesian, as in PR #20. Verify: an xUnit test for the format and the parse of the callback data (at most 64 bytes) passes.
- [ ] 8.2 Handle the callback: end the Break only if the start in the button equals the stored start; else answer that this Break already ended. Verify on the VM with a test bot: the button ends the running Break; a button of an earlier Break does not end the current one.
  - Open: VM 102 has no Telegram bot token or chat ID (`telegramConfigured` false), so the button was not tested. The xUnit tests of 8.1 cover the format and the parse of the callback data.
- [ ] 8.3 Send the notice at a Break start when `telegram_notify_break` is on, with the same button. Verify on the VM: with the setting off (default), no notice comes; with the setting on, the notice names `Kid` and the end time.
  - Partly checked: `telegram_notify_break` is "false" by default and the API changes it (7.3). The notice itself needs a test bot; VM 102 has none.
- [ ] 8.4 Update the `/help` text with `/break`. Verify: `/help` in the test chat lists `/break`.
  - Open: needs a test bot. The `/help` text in `TelegramBotService` lists `/break` (code review).

## 9. Documentation

- [x] 9.1 Add to `GLOSSARY.md`: Break Rule, Break, Continuous Use and Rest, with the definitions of the spec. Verify: each term in `specs/usage-breaks/spec.md` that is not in `GLOSSARY.md` or `CONTEXT.md` before has an entry.
- [x] 9.2 Update `docs/ROADMAP.md` (stage 2: breaks done, per-category breaks as a follow-up) and the release notes of the version: breaks are on after the update (design D2). Verify: the diff of the two files names both points.
  - Result: `docs/ROADMAP.md` names both points. `release.yml` builds the notes from commit subjects, and `main` uses squash merges with the PR title as the subject. The title of PR #31 now ends with "breaks are on after the update", so the release notes name it.

## 10. Integration check on the VM

- [x] 10.1 Run `scripts/vm-test.sh`. Verify: it prints `ALL ASSERTIONS PASSED`, and the xUnit tests of the Domain pass.
  - Result: VM 102, 2026-11-26: `ALL ASSERTIONS PASSED`, and the Domain tests passed (725).
- [ ] 10.2 Run one full cycle as `Kid` with the default rule (45/10) in the Proxmox console, with a Steam game and Notepad open. Verify: warnings at 40 and 44 minutes, lock at 45 minutes, re-lock on an unlock during the Break, unlock after 10 minutes works, both programs still run, and `usage.minutes_used` grew by 45 minutes only.
  - Partly checked (VM 102 has no Steam): one full 45/10 cycle with TestGame (pid 5816) and Notepad with unsaved text (pid 9640). The warnings came at 40 (21:57:31) and 44 (22:02:31), the Break started at 45 (22:03:31) and the lock came at 22:03:36. An unlock at 22:03:54 was locked again at 22:03:59. The Break ended at 22:13:36, and the unlock after it worked. Both programs kept their PIDs, TestGame has 0 suspended threads, and `usage.minutes_used` grew from 46 to 91 (45 minutes). `category_usage` did not grow in this run, because the foreground window was `WWAHost.exe` ("Microsoft account", the first sign-in screen of Windows), which is System and is not counted. A control check at 22:17 closed that window and brought TestGame to the front, and Unknown went from 16 to 18. Do the run with a Steam game on the child's PC.

## Workflow follow-up

- Run `/opsx:verify`, then `/opsx:archive` after the VM checks pass.
- Do the final test on the child's PC, not only on the VM.
- Follow-up change: breaks per App Category.
