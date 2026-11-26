# Tasks

PR #20 (`add-german-child-texts`) is on `main`, so group 6 can start. Do not start group 7 before PR #21 (`require-admin-sign-in`) is on `main`: on `main`, the admin routes need no sign-in while TOTP is off (issue #18), and a process of the child could end its own Break. The other groups can start before PR #21.

For each VM step: check that the snapshot `test-base` exists before you install the service on VM 102. Do the lock and unlock steps in the Proxmox console, not over RDP. For a short test, set the Break Rule of `Kid` to N = 10 and M = 5 (directly in `break_rules` before group 7, through the API after it).

## 1. Pure break logic in the Domain

- [ ] 1.1 Add `BreakRule` in `src/ParentalControl.Domain/Breaks` (Enabled, UseMinutes, BreakMinutes, the default 45/10/on, the range check N 10–240 and M 5–120). Verify: xUnit tests for the default and for each range limit (9, 10, 240, 241 and 4, 5, 120, 121) pass with `dotnet test tests/ParentalControl.Domain.Tests`.
- [ ] 1.2 Add `BreakState` and `BreakPlanner` with the inputs and actions of design D13 (count, Rest start, Rest end, service start, tick, rule change, end by the parent). Verify: `dotnet test` passes.
- [ ] 1.3 Add xUnit tests for the count and the Rest: 30 counted minutes give 30; a Rest of M − 1 minutes keeps the value; a Rest of M minutes gives 0; a tick gap of M minutes gives 0; a service start with an unlocked session keeps the value; a service start after a reboot later than the last use, M minutes or more after the last use, gives 0; a lock event of a session that is already locked does not move the time of the last use. Verify: the tests pass.
- [ ] 1.4 Add xUnit tests for the warnings and the start: the 5-minute and the 1-minute warning come one time per cycle; no warning when the Allowance Time or the Schedule Window ends at or before the Break; the Break starts at N; no Break when the flag "Allowance Time or Schedule Window locks in this tick" is set. Verify: the tests pass.
- [ ] 1.5 Add xUnit tests for the end and the changes: the end is `min(start + M, Schedule Window end)`; Continuous Use is 0 after the end; the end by the parent ends only the Break with the same start time; rule off ends the Break; a new N gives `min(value, N − 5)` and resets the warnings; a new M keeps the end of a running Break; Bonus Minutes are not an input. Verify: the tests pass.

## 2. Data

- [ ] 2.1 Add the tables `break_rules` and `break_state` (design D12) to the schema block in `DatabaseManager`. Verify: `dotnet build -p:EnableWindowsTargeting=true` passes, and on the VM the service starts on an earlier database and creates both tables (check with `sqlite3` or a test in `tests/ParentalControl.Tests`).
- [ ] 2.2 Add `BreakRepository` in Core: read the rule (default if no row), write the rule, read and write the state. Verify: an assertion test in `tests/ParentalControl.Tests` writes and reads a rule and a state, and `scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED`.
- [ ] 2.3 Add `BREAK_STARTED` and `BREAK_ENDED` to `EventType`, and the settings key `telegram_notify_break` (default "false") to `SettingsRepository`. Verify: the build passes, and the admin event log shows the new event types after group 5.

## 3. Count Continuous Use and the Rest in the service

- [ ] 3.1 In `SessionTracker`, add the elapsed minutes of each `UsageRepository.AddMinutes` call for a Restricted User (in `TickAllSessions` and `FlushSessionTime`, not the clock-tamper penalty) to Continuous Use through `BreakEnforcer`. During a running Break, add no minutes to either count. Verify: the build passes; on the VM, after 10 minutes of unlocked use as `Kid`, `break_state.continuous_use_minutes` is 10 and `usage.minutes_used` grew by 10.
- [ ] 3.2 Report the Rest start (`OnSessionLock`, `OnUserLogoff`, `OnSleep`, only for a session tracked as unlocked) and the Rest end (an accepted `OnSessionUnlock`, `OnUserLogon`, `OnWake`) to `BreakEnforcer` with the event time. Verify on the VM: with M = 5, a lock of 6 minutes (Win+L in the Proxmox console) sets `continuous_use_minutes` to 0; a lock of 3 minutes keeps the value. Note the result for issue #9 if the poll shows the session as unlocked.
- [ ] 3.3 On the service start, report the service start per Restricted User, with or without an unlocked session, and with the boot time of the PC (design D5). Verify on the VM: restart the service while `Kid` is unlocked with 7 counted minutes; `continuous_use_minutes` is still 7 after the restart.

## 4. Start and end a Break

- [ ] 4.1 Add `BreakEnforcer` in the Service project and call it from `UsageMonitorWorker.ProcessTick` after the checks of the Allowance Time and the Schedule Window, with the flag of design D8, also for a user without a schedule. Check the end of a running Break each tick for each Restricted User. Send the warnings with `NotificationManager.SendMessage`. Verify on the VM: with N = 10, `Kid` gets the 5-minute warning at 5 minutes and the 1-minute warning at 9 minutes, each one time.
- [ ] 4.2 At the start of a Break: send `msg_break_start`, call `SessionManager.LockSession` and `SessionTracker.OnSessionLock`, store the start and the end, write `BREAK_STARTED`. Verify on the VM: with N = 10 and M = 5, the session locks within 60 seconds after 10 minutes of use; the message names the end time; Notepad with unsaved text and a game still run with the same process IDs after the Break.
- [ ] 4.3 At the end time: end the Break, set Continuous Use to 0, write `BREAK_ENDED` with the reason. Verify on the VM: after the end time, an unlock in the Proxmox console works and stays unlocked.
- [ ] 4.4 Clamp the end of a Break to today's Schedule Window end. Verify on the VM: with a Schedule Window that ends 3 minutes after the Break start and M = 5, the Break ends at the window end and an unlock gets the Schedule Window message and the lock.
- [ ] 4.5 When the Allowance Time ends in the same tick as the Break would start, start no Break. Verify on the VM: set the Allowance Time so that it ends at the same minute as N; the child gets only the daily screen time message, and the event log has no `BREAK_STARTED`.

## 5. Keep the session locked during a Break

- [ ] 5.1 In `SessionTracker.OnSessionUnlock`, lock again during a Break and send `msg_break_running` at most one time in 10 seconds. Verify on the VM (Proxmox console): an unlock during a Break shows the message and locks again; measure the time to the lock and note it in issue #14.
- [ ] 5.2 In `SessionTracker.OnUserLogon`, lock a new session of the user during a Break with `LockSession`, not `ForceLogoff`. Verify on the VM: sign out during a Break and sign in again; the session locks after the sign-in and is not signed out.
- [ ] 5.3 In the tick, lock a session of the user that shows as unlocked during a Break. Verify on the VM: the session is locked again within 60 seconds also if the unlock event is lost (for example after a service restart during the Break); `LockSession` on a session that is already disconnected causes no error in the log.
- [ ] 5.4 After a service restart during a Break, continue the Break until its stored end. Verify on the VM: restart the service during a Break; an unlock before the end is locked again; an unlock after the end works.
- [ ] 5.5 Confirm that a Break uses no Allowance Time. Verify on the VM: `usage.minutes_used` and `category_usage` do not grow during a Break of 5 minutes.

## 6. Child texts

- [ ] 6.1 Add `msg_break_warn`, `msg_break_start` and `msg_break_running` to `ChildMessageKeys` (also to `All`) and to `MessageCatalog` with the "de", "en" and "id" texts of design D15, and `break_title` to `SessionTexts`. Verify: the existing catalog tests (same placeholders in each preset) pass for the new keys with `dotnet test`.
- [ ] 6.2 Add xUnit tests for the three German scenario strings of the spec, with `{break_end}` as `HH:mm` and the remaining minutes rounded up. Verify: the tests pass.
- [ ] 6.3 Use the catalog texts in `BreakEnforcer` and `SessionTracker`. Verify on the VM with the preset "de": the warning, the start and the unlock message show the German texts with the correct word form for 1 minute.

## 7. Admin API (after PR #21)

- [ ] 7.1 Add `GET /api/admin/breaks`, `PUT /api/admin/breaks/{userId}` and `POST /api/admin/breaks/{userId}/end` with the shapes and status codes of design D14. Verify: an xUnit test of `AdminRoutePolicy` classifies the three paths as admin routes, and the build passes.
- [ ] 7.2 Put the range check of `BreakRule` in the PUT route and the rule change of design D11 in `BreakEnforcer`. Verify: xUnit tests from group 1 cover the logic; on the VM, `PUT` with `useMinutes` 5 gives 400 and no change, and a change of N from 60 to 30 with 40 counted minutes gives 25.
- [ ] 7.3 Add `telegram_notify_break` to `GET` and `POST /api/admin/settings`. Verify on the VM: the key changes through the API with an Admin Session.
- [ ] 7.4 Verify the gate on the VM: as `Kid`, `curl -X POST http://127.0.0.1:5050/api/admin/breaks/{userId}/end` without a cookie gives 401 and the Break continues; with an Admin Session it gives 200 and the Break ends; a second call gives 409.

## 8. Telegram

- [ ] 8.1 Add the command `/break` to `TelegramBotService`: one line per Restricted User with a Break Rule, and a button `brk_end:<userId>:<breakStartUnixSeconds>` for a running Break. Texts in English and Indonesian, as in PR #20. Verify: an xUnit test for the format and the parse of the callback data (at most 64 bytes) passes.
- [ ] 8.2 Handle the callback: end the Break only if the start in the button equals the stored start; else answer that this Break already ended. Verify on the VM with a test bot: the button ends the running Break; a button of an earlier Break does not end the current one.
- [ ] 8.3 Send the notice at a Break start when `telegram_notify_break` is on, with the same button. Verify on the VM: with the setting off (default), no notice comes; with the setting on, the notice names `Kid` and the end time.
- [ ] 8.4 Update the `/help` text with `/break`. Verify: `/help` in the test chat lists `/break`.

## 9. Documentation

- [ ] 9.1 Add to `GLOSSARY.md`: Break Rule, Break, Continuous Use and Rest, with the definitions of the spec. Verify: each term in `specs/usage-breaks/spec.md` that is not in `GLOSSARY.md` or `CONTEXT.md` before has an entry.
- [ ] 9.2 Update `docs/ROADMAP.md` (stage 2: breaks done, per-category breaks as a follow-up) and the release notes of the version: breaks are on after the update (design D2). Verify: the diff of the two files names both points.

## 10. Integration check on the VM

- [ ] 10.1 Run `scripts/vm-test.sh`. Verify: it prints `ALL ASSERTIONS PASSED`, and the xUnit tests of the Domain pass.
- [ ] 10.2 Run one full cycle as `Kid` with the default rule (45/10) in the Proxmox console, with a Steam game and Notepad open. Verify: warnings at 40 and 44 minutes, lock at 45 minutes, re-lock on an unlock during the Break, unlock after 10 minutes works, both programs still run, and `usage.minutes_used` grew by 45 minutes only.

## Workflow follow-up

- The page for the Break Rule in the React admin (`GET`/`PUT /api/admin/breaks`, end button, `telegram_notify_break`) is a part of issue #26. It does not block the archive of this change.
- Run `/opsx:verify`, then `/opsx:archive` after the VM checks pass.
- Do the final test on the child's PC, not only on the VM.
- Follow-up change: breaks per App Category.
