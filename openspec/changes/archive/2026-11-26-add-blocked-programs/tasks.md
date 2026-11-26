# Tasks

PR #20 (`add-german-child-texts`), PR #21 (`require-admin-sign-in`) and PR #22 (`resume-suspended-apps-at-session-end`) are merged. This change is rebased on them (design D14, D15).

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

- [x] 4.1 Add `CategoryDefaults.BlockedBrowsers` with the 15 file names of design D8, each a file rule with the category Unknown and the block on. Verify with an xUnit test that the list has no `launcher.exe`, has no duplicates (letter case ignored), and that each pattern passes `AppRule.NormalizePattern`.
- [x] 4.2 Add a pure seed plan: input the existing rules, output the rules to insert and the rule ids to set blocked (design D8). Verify with xUnit tests: an empty database, an existing `chrome.exe` rule to Video (gets the block, keeps Video and its App Limit), and an existing `CHROME.EXE` rule (letter case ignored).

## 5. Core: data

- [x] 5.1 In `DatabaseManager`, add `app_rules.is_blocked INTEGER NOT NULL DEFAULT 0` and `suspended_processes.reason TEXT NOT NULL DEFAULT 'limit'` with `pragma_table_info` checks, and create the table `blocked_starts` of design D11. Verify on the Mac with `dotnet build -p:EnableWindowsTargeting=true`, and later with an assert in `tests/ParentalControl.Tests` that a database of stage 1 gets both columns and keeps its rows.
- [x] 5.2 Read and write `is_blocked` in `AppRuleRepository` (insert, update, read). An update without a block value keeps the stored value. Verify with an assert in `tests/ParentalControl.Tests`.
- [x] 5.3 Read and write `reason` in `SuspensionRepository`. Verify with an assert in `tests/ParentalControl.Tests` that a row of stage 1 reads as `Limit`.
- [x] 5.4 Add the one-time seed of the blocked browsers with the key `blocked_defaults_seeded` in one transaction, after the seed of `categories_seeded`. Verify with asserts: a new database gets 15 blocked rules; a second start adds nothing; a deleted default stays deleted; an existing `chrome.exe` rule gets the block.
- [x] 5.5 Add `BlockedStartRepository`: upsert of a close or a suspension, read of one day, the flags `child_notified` and `parent_notified`, and the delete of old rows in `RunCleanupIfNeeded`. Verify with asserts for the counts, the first insert result, and the cleanup.
- [x] 5.6 Add `EventType.PROGRAM_BLOCKED` at the end of the enum, and the settings key `notify_blocked_telegram` with the default `1`. Verify that `scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED` for groups 5.1 to 5.6.

## 6. Core: platform calls

- [x] 6.1 Add the P/Invokes `TerminateProcess` (kernel32) and `NtQueryInformationProcess` (ntdll) and the constant `PROCESS_TERMINATE` to `NativeMethods`. Verify that the build passes.
- [x] 6.2 Add `ProcessControl.Terminate(pid, expectedStartUtc, out detail)` with the pid and start time check of `WithVerifiedHandle` (design D5). Verify with an assert in `tests/ParentalControl.Tests`: start `ping -n 30 127.0.0.1`, call `Terminate` with a wrong start time (result `NotFound`, process still runs), then with the right start time (result `Ok`, process ended).
- [x] 6.3 Add `ProcessControl.QueryCommandLine(pid)` with `ProcessCommandLineInformation` (60) and `ProcessControl.QueryParentPid(pid)` with `ProcessBasicInformation` (0). Verify with asserts that the command line of the started `ping` process contains `-n 30` and that its parent pid is the pid of the test process.
- [x] 6.4 Verify with `grep -rn "TerminateProcess\|ProcessControl.Terminate" src/` that `NativeMethods.TerminateProcess` has one caller (`ProcessControl.Terminate`) and that `ProcessControl.Terminate` has one caller (the `Close` path of `BlockEnforcer`, after group 7).

## 7. Service: detection and enforcement

- [x] 7.1 Add the NuGet package `System.Management` (8.0.x) to `ParentalControl.Service.csproj`. Verify that `dotnet build -p:EnableWindowsTargeting=true` and the CI build pass.
- [x] 7.2 Add `ProcessStartWatcher` (WMI `Win32_ProcessStartTrace`, filter on restricted sessions, bounded channel, restart at the next tick after an error) (design D3). Verify by a log line at start and on VM 102 in task 7.7.
  - VM 102 (2026-11-26): each service start logs `Process start watcher started (WMI Win32_ProcessStartTrace)`. After `net stop winmgmt /y` the log shows `Process start watcher stopped ("CallCanceled")`, and the next tick starts the watcher again.
- [x] 7.3 Add `BlockEnforcer`: the consumer of the channel, the block epoch from its start and `CategoryConfigCache.Changed`, the `BlockedStartDecider`, the close through `ProcessControl.Terminate`, `RequestTick` of `CategoryEnforcer` after a failed close, the upsert in `blocked_starts`, the event log entry, the child message and the Telegram notice with their dedupe flags (design D4, D5, D10, D11, D12). Verify that the build passes and in task 7.7.
  - VM 102: verified through the scenarios of 7.7.
- [x] 7.4 In `CategoryEnforcer.Tick`, run the decider for each listed process before the planner, give `Close` results to `BlockEnforcer`, mark them as a pending close for the planner, and set `IsBlocked` in the verdict. A blocked suspension updates `blocked_starts` and sends the child message once. Verify that the build passes and in task 7.7.
  - VM 102: verified through the scenarios of 7.7 (tick suspensions after a service start and after a WMI stop).
- [x] 7.5 Wire the watcher and the enforcer in `Program.cs` / `UsageMonitorWorker`, start them after the database, and stop them before `ResumeAll` at a service stop. Verify on VM 102 that a service stop leaves no blocked process suspended.
  - VM 102, first run: failed. The Edge crash handler (`--type=crashpad-handler`) has 2 threads in an `Executive` wait that never show `Suspended`. The planner suspended it again at each tick (11 suspends in 10 minutes), and after `sc stop` 8 of its 10 threads stayed suspended. Fix `48e051d` (`ThreadSuspension.AllSuspended`, xUnit tests). After the fix: no repeated suspension of the crash handler, and after `sc stop` no thread of any `msedge.exe` was suspended.
- [x] 7.6 Add the message key `msg_program_blocked` with the de, en and id texts of design D10 to `ChildMessageKeys` and `MessageCatalog` in `src/ParentalControl.Domain/Messages`, with `{name}`. Verify that the catalog tests (same placeholders in each preset) pass with the new key, and add a test for the German text of the spec scenario "German message".
- [x] 7.7 Manual check on VM 102 (console, not RDP) as `Kid`, after a rollback to `test-base` and an install of the build:
  - Start Edge from the Start menu. Expected: the Edge window closes within 2 seconds; `Kid` gets one German message; `blocked_starts` has one row for `msedge.exe` with `closed_count` 1; the event log has one `PROGRAM_BLOCKED` entry.
  - Start Edge again. Expected: it closes; no second message; `closed_count` 2.
  - Sign out and sign in as `Kid`. Expected: if Startup Boost starts Edge, it closes without a message.
  - Install Firefox. Start it as `Kid`, then stop the service, start Firefox, and start the service. Expected: the Firefox windows are minimized and the running Firefox is suspended within 70 seconds, not closed; its record has the reason `blocked`.
  - Remove the block of `firefox.exe` and start Firefox as `Kid`. Turn on the block through the API, and at once open a new tab in Firefox. Expected: no Firefox process is closed; all Firefox processes are suspended within 70 seconds.
  - Remove the block of `firefox.exe` through the API. Expected: Firefox resumes within 60 seconds.
  - Start a Study app that uses `msedgewebview2.exe` (for example a WebView2 sample or the Widgets of Windows 10). Expected: it keeps running.
  - Stop the WMI service (`net stop winmgmt /y`) and start Chrome. Expected: Chrome is suspended within 70 seconds, not closed; the watcher starts again after `net start winmgmt`.
  - Sign out while a blocked browser is suspended. Expected: the sign-out finishes and no other program of `Kid` is closed.
  - Start the sign-out while a blocked browser is suspended and Notepad has unsaved text, and cancel it. Expected: the browser is not suspended during the Session End Hold, and is suspended again within 20 seconds after the hold; a new Chrome start during the hold is closed (design D15).
  - Results on VM 102 (2026-11-26, console as `Kid`, build `48e051d` on `main` with PR #31). A process logger (WMI start and stop events) recorded each exit; the service closes with exit code 1.
  - Edge start (Win+R `msedge`; Open-Shell takes the Win key, the parent is `explorer.exe` as from the Start menu): closed, one German message, `closed_count` 1, one `PROGRAM_BLOCKED` event. The first `msedge.exe` (7228) exited by itself with code 0; its child 6540 was closed with code 1.
  - Second Edge start: closed, no second message, `closed_count` 2.
  - Close time, measured as Kid from the start of the process to its exit: 25 starts (10 Edge, 15 Chrome). 24 closed in 0.23 to 1.7 s. One Edge start took about 3.2 s: the WMI start event arrived late, at the same time in the service and in the logger. Open question for the spec: "within 2 seconds" holds in 24 of 25 starts.
  - Startup Boost (`--no-startup-window --win-session-start`, parent `explorer.exe`) at the sign-in: closed, marked `(silent)`, no message. It started only after the privacy screens of Windows were answered. An Edge that ran before the deploy was suspended silently after 10 s (reason `blocked`, `child_notified` 0).
  - Firefox: a start with the service running was closed (the launcher exited by itself with code 0, the browser process was closed). Started while the service was stopped: after the service start, 9 processes got a Pending Suspension, the window was minimized (`IsIconic` true) and all were suspended 11 s later with the reason `blocked`. Nothing was closed.
  - Block on a running Firefox, then new content: Ctrl+T made no new process (Firefox reuses its content processes). A local page that loads a new cross-site iframe every 1.5 s made 7 new `firefox.exe` children of the old browser after the block. Each got a Pending Suspension and was suspended; none was closed.
  - Block removed (API and React switch): Firefox resumed 0.02 s after the change.
  - WebView2: the new Outlook (`olk.exe`) started 6 `msedgewebview2.exe` processes; they ran and were never suspended or closed (no service log line for `msedgewebview2` all day).
  - `net stop winmgmt /y`, then a Chrome start: not closed; the next tick (20 s after the start) gave it a Pending Suspension, and its 15 processes were suspended with the reason `blocked`. The restart of the watcher started WMI again, so `net start winmgmt` was not necessary.
  - Sign-out with a suspended blocked program: the hold started, the program was resumed, and the session ended 0.5 s later. Windows ended the other programs of the session with its logoff code `0x40010004`; the service closed nothing.
  - Cancelled sign-out (D15) with a blocked copy of Notepad (`C:\Games\npblock.exe`) with unsaved text, because Edge and Firefox end themselves at a sign-out request: no suspension during the hold; a Chrome start during the hold was closed in 0.5 s; after the hold, npblock got a Pending Suspension at once and was suspended 10.5 s after the end of the hold.
  - Hard rule: the service log has 134 closes (64 `msedge.exe`, 69 `chrome.exe`, 1 `firefox.exe`). Each one was a browser process, closed in the same second as its start event. 61 closes of sandboxed Chromium processes failed with "access denied"; each fell back to suspension, and the process ended by itself after its parent. No lock and no parent alert followed.
  - Observed (PR #22, not this change): after a hold that the session did not confirm, the service refuses the next hold for 15 minutes. In that time, Windows ended the suspended npblock (with its unsaved text) and a suspended Chrome at a sign-out.

## 8. Web admin API

- [x] 8.1 Add `blocked` to `GET`, `POST` and `PUT /api/admin/app-rules` (missing on `PUT` keeps the stored value) with the validation of design D2 (status 400). Verify with an assert in `tests/ParentalControl.Tests` or a `curl` call on VM 102: a blocked System rule returns 400; a `PUT` without `blocked` keeps the block.
  - VM 102: a block on the System rule `C:\Windows\` (`PUT`) and on a new System rule (`POST`) gives 400 "A rule for System cannot have a block."; `msedgewebview2.exe` gives 400; a folder rule below `C:\Windows\` or the install folder gives 400. A `PUT` of `chrome.exe` to Video without `blocked` kept the block.
- [x] 8.2 Add `GET /api/admin/blocked-starts?date=` (design D11). Verify with `curl` on VM 102 after task 7.7: the list has `msedge.exe` with the counts.
  - VM 102: `msedge.exe` closed 2 / suspended 2 and `chrome.exe` closed 4 at that time; another day gives an empty list; `date=bad` gives 400.
- [x] 8.3 Add `notifyBlockedTelegram` to `GET` and `POST /api/admin/settings`. Verify with `curl` on VM 102 that the value survives a service restart.
  - VM 102: `notifyBlockedTelegram` true by default; set to false, still false after the service restart; set back to true.
- [x] 8.4 Verify with `curl` on VM 102 that each new or changed route returns 401 without an admin session. The console gate test (task 11.3) already checks `GET /api/admin/blocked-starts` and a `PUT` of a block in CI.
  - VM 102, from the Mac without a session: `GET` and `POST /api/admin/app-rules`, `PUT /api/admin/app-rules/19`, `GET /api/admin/blocked-starts`, `GET` and `POST /api/admin/settings` give 401; the block of rule 19 stayed on.

## 9. Admin UI (React admin of PR #35)

- [x] 9.1 In the React admin, add a block switch to each row of the App Rules table and to the form for a new rule. The switch saves at once with the stored values of the rule; show the error text of a refused block, and the switch stays as stored. Verify with a Vitest component test.
- [x] 9.1a Verify on VM 102 that the switch blocks and unblocks a rule, and that a block of a System rule shows the error text of the server.
  - VM 102, headless browser on the Mac: the `firefox.exe` switch removed and set the block (the database and the service log follow); the `C:\Windows\` switch shows "Sperre nicht geändert: A rule for System cannot have a block." and stays off. The "Gesperrte Starts" panel shows firefox, msedge and chrome with the counts, local times and paths. The PDF hint and the "Standard-Browser" badge show.
- [x] 9.2 Add a "Blocked starts" list (user, program, counts, first and last time, path as text) from `/api/admin/blocked-starts`, for the today of the service or a selected day. Verify with a Vitest component test that a path with `<script>` shows as text.
- [x] 9.3 Add the switch for the Telegram notice and the text `msgProgramBlocked` to the settings view. Verify with a Vitest component test.
- [x] 9.4 Add the field `defaultBrowser` to `GET /api/admin/app-rules` (`CategoryDefaults.IsBlockedBrowser`), and show a badge for the 15 default browser rules. Verify with an xUnit test and a Vitest component test.
- [x] 9.5 Show a hint below the App Rules: a blocked Edge opens no PDF files, so the parent installs another PDF reader (design D9). Verify with a Vitest component test.

## 10. Documentation

- [x] 10.1 Add the terms Blocked Program and Blocked Start to `GLOSSARY.md`, and widen Suspended App: a process that the service suspends because its exe has no time left, or because its exe is a Blocked Program and it did not start after the block took effect. Verify that the terms match `specs/blocked-programs/spec.md`.
- [x] 10.2 Add the parent notes to `README.md`: the default blocked browsers, the PDF reader hint for Edge (design D9), and that a renamed browser is not blocked. Verify that the notes name each of the 15 default file names.
- [x] 10.3 Update `docs/ROADMAP.md` (stage 2): blocked programs done, the allow-list mode and the renamed-browser detection as follow-ups. Verify that the roadmap links this change.

## 11. Rebase on PR #21 and PR #22

- [x] 11.1 Rebase on `main` with PR #21 and PR #22. Keep both behaviours in `SuspensionPlanner` (`HeldSessions` together with `PendingClose` and `ProtectedPrograms`), `CategoryEnforcer` (the hold expiry and the Pending Suspension together with the block enforcer), the schema in `DatabaseManager`, and `GLOSSARY.md`. The route `GET /api/admin/blocked-starts` uses the Admin Session gate instead of `CheckAuth` (design D13, D15). Verify that `dotnet build -p:EnableWindowsTargeting=true` has 0 errors and that `dotnet test tests/ParentalControl.Domain.Tests` passes.
- [x] 11.2 Add xUnit tests: during a Session End Hold, the planner suspends no blocked process and keeps the blocked record; after the hold, it suspends the blocked processes again with the reason `Blocked`. Add `/api/admin/blocked-starts` and `/api/admin/app-rules` to the route policy test. Verify that `dotnet test tests/ParentalControl.Domain.Tests` passes.
- [x] 11.3 In the console gate test, check that the routes of the block are mapped and classified as `Admin`, that `GET /api/admin/blocked-starts` without a session gives 401, and that a `PUT` of a block without a session gives 401 and keeps the block. Verify with the CI run of the PR.

## Workflow follow-up

- Run `/opsx:verify` after the VM checks of task 7.7 and group 8. Done on VM 102 on 2026-11-26 (notes in 7.7 and group 8).
- Still open: the Telegram notice of design D12 (no bot on the VM, issue #13) and the final check on the child's PC.
- Before the archive: if `openspec/specs/category-limits/spec.md` changed after this rebase (for example by PR #31), start each MODIFIED block of this change again from the current text (design D14).
- Archive the change with `/opsx:archive` after the final check on the child's PC.
- Open follow-up issues: the allow-list mode ("allowed programs"), the detection of a renamed or portable browser.
