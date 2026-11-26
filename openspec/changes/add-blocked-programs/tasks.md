# Tasks

PR #20 (`add-german-child-texts`) is merged (design D14). Do not install a build of this change on the child's PC before PR #21 and PR #22 are merged.

## 1. Domain: the block on an App Rule

- [x] 1.1 Add `bool IsBlocked` (default `false`) to `AppRule` in `src/ParentalControl.Domain/Categories/AppRule.cs`. Verify that `dotnet build -p:EnableWindowsTargeting=true` passes and that the existing Domain tests stay green (`dotnet test tests/ParentalControl.Domain.Tests`).
- [x] 1.2 Add the pure record `ProtectedPrograms` (Windows folder, install folder, `msedgewebview2.exe`) with `IsProtected(path)` (design D7). Verify with xUnit tests: an exe below each folder, `msedgewebview2.exe` in any folder, a similar folder name (`C:\WindowsApps2\x.exe`), and a path with `..` parts.
- [x] 1.3 Extend `AppRule.Validate` with the block rules of design D2 (System, protected folders, protected file names). Keep the error texts in the style of the existing ones. Verify with xUnit tests for each refused case and for a permitted blocked `notepad.exe` file rule.
- [x] 1.4 Add tests for the block through the resolver precedence: a file rule without the block below a blocked folder, the longest folder, and a blocked file rule over an unblocked folder (spec "The block follows the rule precedence"). Verify that `dotnet test tests/ParentalControl.Domain.Tests` passes.
- [x] 1.5 Make `CategoryLimitCalculator` leave out the App Limit of a blocked rule (design D1). Verify with xUnit tests: a blocked rule with an App Limit gives no App Limit status and no due warning; the exe uses its Category Limit; the App Limit applies again without the block.

## 2. Domain: the decision "at its start"

- [x] 2.1 Add `BlockedStartDecider` with the results `Close`, `Suspend` and `Ignore` and the constant `StartWindow = 10 s` (design D4). Verify with xUnit tests: a start after the epoch detected after 1 s (Close), after 11 s (Suspend), a start before the epoch (Suspend), a protected exe (Ignore), an unrestricted session (Ignore), a null path (Ignore), an exe that is not blocked (Ignore), a new process whose parent is a blocked process created before the epoch (Suspend), the same parent pid with a creation time after the child (reused pid: Close), and a new process whose parent is a Blocked Start (Close).
- [x] 2.2 Add the pure classifier for a silent start (`--no-startup-window`, `--type=`, letter case ignored, inside quotes too) (design D9). Verify with xUnit tests for the Edge Startup Boost command line, a Chrome renderer command line, a normal start, an empty command line, and a command line with `--no-startup-window` as part of a URL argument.
- [x] 2.3 Verify by test that the silent classifier cannot change `Close` to another result: the decider has no command line input (spec "Process data for a block is untrusted").

## 3. Domain: blocked suspensions in the planner

- [x] 3.1 Add `SuspensionReason` (`Limit`, `Blocked`) to `SuspensionRecord` and `SuspendAction`, and `IsBlocked` to `ExeVerdict`. Verify that the existing planner tests pass without a change of their expectations.
- [x] 3.2 Change `SuspensionPlanner.Plan`: suspend a blocked process with the reason `Blocked` without the Foreground Window condition; skip a process for which the caller marks a pending close; keep a `Blocked` record while the exe is blocked; when the block ends, change the record to `Limit` or resume it (design D6). Verify with xUnit tests for each case of D6, also "blocked browser in Unknown with time left stays suspended" and "a record after a service start with an empty Foreground history stays suspended".
- [x] 3.3 Verify by test that a protected exe (D7) is never in `ToSuspend` because of a block, also if a blocked rule matches it.

## 4. Domain: default blocked browsers

- [ ] 4.1 Add `CategoryDefaults.BlockedBrowsers` with the 15 file names of design D8, each a file rule with the category Unknown and the block on. Verify with an xUnit test that the list has no `launcher.exe`, has no duplicates (letter case ignored), and that each pattern passes `AppRule.NormalizePattern`.
- [ ] 4.2 Add a pure seed plan: input the existing rules, output the rules to insert and the rule ids to set blocked (design D8). Verify with xUnit tests: an empty database, an existing `chrome.exe` rule to Video (gets the block, keeps Video and its App Limit), and an existing `CHROME.EXE` rule (letter case ignored).

## 5. Core: data

- [ ] 5.1 In `DatabaseManager`, add `app_rules.is_blocked INTEGER NOT NULL DEFAULT 0` and `suspended_processes.reason TEXT NOT NULL DEFAULT 'limit'` with `pragma_table_info` checks, and create the table `blocked_starts` of design D11. Verify on the Mac with `dotnet build -p:EnableWindowsTargeting=true`, and later with an assert in `tests/ParentalControl.Tests` that a database of stage 1 gets both columns and keeps its rows.
- [ ] 5.2 Read and write `is_blocked` in `AppRuleRepository` (insert, update, read). An update without a block value keeps the stored value. Verify with an assert in `tests/ParentalControl.Tests`.
- [ ] 5.3 Read and write `reason` in `SuspensionRepository`. Verify with an assert in `tests/ParentalControl.Tests` that a row of stage 1 reads as `Limit`.
- [ ] 5.4 Add the one-time seed of the blocked browsers with the key `blocked_defaults_seeded` in one transaction, after the seed of `categories_seeded`. Verify with asserts: a new database gets 15 blocked rules; a second start adds nothing; a deleted default stays deleted; an existing `chrome.exe` rule gets the block.
- [ ] 5.5 Add `BlockedStartRepository`: upsert of a close or a suspension, read of one day, the flags `child_notified` and `parent_notified`, and the delete of old rows in `RunCleanupIfNeeded`. Verify with asserts for the counts, the first insert result, and the cleanup.
- [ ] 5.6 Add `EventType.PROGRAM_BLOCKED` at the end of the enum, and the settings key `notify_blocked_telegram` with the default `1`. Verify that `scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED` for groups 5.1 to 5.6.

## 6. Core: platform calls

- [ ] 6.1 Add the P/Invokes `TerminateProcess` (kernel32) and `NtQueryInformationProcess` (ntdll) and the constant `PROCESS_TERMINATE` to `NativeMethods`. Verify that the build passes.
- [ ] 6.2 Add `ProcessControl.Terminate(pid, expectedStartUtc, out detail)` with the pid and start time check of `WithVerifiedHandle` (design D5). Verify with an assert in `tests/ParentalControl.Tests`: start `ping -n 30 127.0.0.1`, call `Terminate` with a wrong start time (result `NotFound`, process still runs), then with the right start time (result `Ok`, process ended).
- [ ] 6.3 Add `ProcessControl.QueryCommandLine(pid)` with `ProcessCommandLineInformation` (60) and `ProcessControl.QueryParentPid(pid)` with `ProcessBasicInformation` (0). Verify with asserts that the command line of the started `ping` process contains `-n 30` and that its parent pid is the pid of the test process.
- [ ] 6.4 Verify with `grep -rn "TerminateProcess\|ProcessControl.Terminate" src/` that `NativeMethods.TerminateProcess` has one caller (`ProcessControl.Terminate`) and that `ProcessControl.Terminate` has one caller (the `Close` path of `BlockEnforcer`, after group 7).

## 7. Service: detection and enforcement

- [ ] 7.1 Add the NuGet package `System.Management` (8.0.x) to `ParentalControl.Service.csproj`. Verify that `dotnet build -p:EnableWindowsTargeting=true` and the CI build pass.
- [ ] 7.2 Add `ProcessStartWatcher` (WMI `Win32_ProcessStartTrace`, filter on restricted sessions, bounded channel, restart at the next tick after an error) (design D3). Verify by a log line at start and on VM 102 in task 7.7.
- [ ] 7.3 Add `BlockEnforcer`: the consumer of the channel, the block epoch from its start and `CategoryConfigCache.Changed`, the `BlockedStartDecider`, the close through `ProcessControl.Terminate`, `RequestTick` of `CategoryEnforcer` after a failed close, the upsert in `blocked_starts`, the event log entry, the child message and the Telegram notice with their dedupe flags (design D4, D5, D10, D11, D12). Verify that the build passes and in task 7.7.
- [ ] 7.4 In `CategoryEnforcer.Tick`, run the decider for each listed process before the planner, give `Close` results to `BlockEnforcer`, mark them as a pending close for the planner, and set `IsBlocked` in the verdict. A blocked suspension updates `blocked_starts` and sends the child message once. Verify that the build passes and in task 7.7.
- [ ] 7.5 Wire the watcher and the enforcer in `Program.cs` / `UsageMonitorWorker`, start them after the database, and stop them before `ResumeAll` at a service stop. Verify on VM 102 that a service stop leaves no blocked process suspended.
- [ ] 7.6 Add the message key `msg_program_blocked` with the de, en and id texts of design D10 to `ChildMessageKeys` and `MessageCatalog` in `src/ParentalControl.Domain/Messages`, with `{name}`. Verify that the catalog tests (same placeholders in each preset) pass with the new key, and add a test for the German text of the spec scenario "German message".
- [ ] 7.7 Manual check on VM 102 (console, not RDP) as `Kid`, after a rollback to `test-base` and an install of the build:
  - Start Edge from the Start menu. Expected: the Edge window closes within 2 seconds; `Kid` gets one German message; `blocked_starts` has one row for `msedge.exe` with `closed_count` 1; the event log has one `PROGRAM_BLOCKED` entry.
  - Start Edge again. Expected: it closes; no second message; `closed_count` 2.
  - Sign out and sign in as `Kid`. Expected: if Startup Boost starts Edge, it closes without a message.
  - Install Firefox. Start it as `Kid`, then stop the service, start Firefox, and start the service. Expected: the running Firefox is suspended within 60 seconds, not closed; its record has the reason `blocked`.
  - Remove the block of `firefox.exe` and start Firefox as `Kid`. Turn on the block through the API, and at once open a new tab in Firefox. Expected: no Firefox process is closed; all Firefox processes are suspended within 60 seconds.
  - Remove the block of `firefox.exe` through the API. Expected: Firefox resumes within 60 seconds.
  - Start a Study app that uses `msedgewebview2.exe` (for example a WebView2 sample or the Widgets of Windows 10). Expected: it keeps running.
  - Stop the WMI service (`net stop winmgmt /y`) and start Chrome. Expected: Chrome is suspended within 60 seconds, not closed; the watcher starts again after `net start winmgmt`.
  - Sign out while a blocked browser is suspended (needs PR #22). Expected: the sign-out finishes and no other program of `Kid` is closed.

## 8. Web admin API

- [ ] 8.1 Add `blocked` to `GET`, `POST` and `PUT /api/admin/app-rules` (missing on `PUT` keeps the stored value) with the validation of design D2 (status 400). Verify with an assert in `tests/ParentalControl.Tests` or a `curl` call on VM 102: a blocked System rule returns 400; a `PUT` without `blocked` keeps the block.
- [ ] 8.2 Add `GET /api/admin/blocked-starts?date=` (design D11). Verify with `curl` on VM 102 after task 7.7: the list has `msedge.exe` with the counts.
- [ ] 8.3 Add `notifyBlockedTelegram` to `GET` and `POST /api/admin/settings`. Verify with `curl` on VM 102 that the value survives a service restart.
- [ ] 8.4 Verify with `curl` on VM 102 that each new or changed route returns 401 without an admin session.

## 9. Admin UI (depends on issue #26)

- [ ] 9.1 In the React admin, add a block switch to each row of the App Rules table and to the form for a new rule; show the error text of a refused block. Verify with a Vitest component test and on VM 102.
- [ ] 9.2 Add a "Blocked today" list (user, program, count, first and last time, path as text) from `/api/admin/blocked-starts`. Verify with a Vitest component test that a path with `<script>` shows as text.
- [ ] 9.3 Add the switch for the Telegram notice to the settings view. Verify with a Vitest component test.

## 10. Documentation

- [ ] 10.1 Add the terms Blocked Program and Blocked Start to `GLOSSARY.md`, and widen Suspended App: a process that the service suspends because its exe has no time left, or because its exe is a Blocked Program and it did not start after the block took effect. Verify that the terms match `specs/blocked-programs/spec.md`.
- [ ] 10.2 Add the parent notes to `README.md`: the default blocked browsers, the PDF reader hint for Edge (design D9), and that a renamed browser is not blocked. Verify that the notes name each of the 15 default file names.
- [ ] 10.3 Update `docs/ROADMAP.md` (stage 2): blocked programs done, the allow-list mode and the renamed-browser detection as follow-ups. Verify that the roadmap links this change.

## Workflow follow-up

- Run `/opsx:verify` after the VM checks of task 7.7 and group 8.
- Before the archive: if PR #22 is archived first, merge its text of "Resume suspended programs" into the MODIFIED block of this change (design D14).
- Archive the change with `/opsx:archive` after the final check on the child's PC.
- Open follow-up issues: the allow-list mode ("allowed programs"), the detection of a renamed or portable browser.
