# Tasks

## 1. Domain project and test setup

- [x] 1.1 Create `src/ParentalControl.Domain` (`net8.0`) and `tests/ParentalControl.Domain.Tests` (`net8.0`, xUnit, `<RollForward>Major</RollForward>` because the Mac has only the .NET 11 runtime), add both to `ParentalControl.slnx`, reference Domain from Core; verify `dotnet test tests/ParentalControl.Domain.Tests` passes on the Mac with one placeholder test.
- [x] 1.2 Add a `dotnet test tests/ParentalControl.Domain.Tests` step to `.github/workflows/ci.yml` and a line about it in CLAUDE.md "Build and test"; verify the CI run on the PR shows the step green.
- [x] 1.3 Add the terms App Category, App Rule, App Limit, Category Usage, Category Limit and Suspended App to `GLOSSARY.md`; verify each term matches its use in the specs.

## 2. Category resolution (pure logic)

- [x] 2.1 Implement `AppCategory`, `AppRule` and `CategoryResolver` (canonical path, file-name rules first, longest folder prefix on a folder boundary, else Unknown); verify xUnit tests for every scenario of the requirements "App Rules map an exe to a category", "Rule precedence" and "Folder match on whole folder names" pass on the Mac.
- [x] 2.2 Define the default rules list and default limits in Domain; verify a test asserts Games/Video/Social/Unknown = 120/180, Study/Creative/System = no limit, and the default rules resolve a Steam `steamapps\common` exe to Games and `WINWORD.EXE` to Study.

## 3. Limit calculation and suspension planning (pure logic)

- [x] 3.1 Implement `CategoryLimitCalculator` (weekday/weekend limit + Bonus Minutes, Category Limits and App Limits against the shared Category Usage, remaining minutes, 5- and 1-minute warnings per limit, exhausted per exe); verify xUnit tests for the "Saturday", "Bonus Minutes", "Five minutes left", "Game with a longer App Limit", "Longer App Limit reached" and "App Limit shorter than the category" scenarios pass.
- [x] 3.2 Implement `SuspensionPlanner` (suspend, re-suspend a recorded process that runs again, resume, drop stale records; only foreground-seen exes; never System, the agent exe, `msedgewebview2.exe`, an exe below the Windows folder or a process without a path); verify xUnit tests cover: exhausted category, background helper not seen in front, WebView2, Windows folder without the System rule, recorded process running again, time back after bonus, new day, rule moved to another category, pid reused with another start time, stale record, admin session excluded.

## 4. Storage

- [x] 4.1 Add the tables `app_categories`, `app_rules`, `category_usage`, `suspended_processes` to `DatabaseManager`, and the one-time seed guarded by the setting `categories_seeded`; verify a new section in `tests/ParentalControl.Tests/Program.cs` (run with `scripts/vm-test.sh`) checks the seed, that a second `Initialize()` keeps a changed limit, and that a deleted default rule stays deleted.
- [x] 4.2 Add the repositories for categories, rules, category usage (with a seconds accumulator) and suspension records, plus retention of `category_usage` in `RunCleanupIfNeeded`; verify the same test section covers CRUD, the accumulator (3 × 20 s = 1 minute) and retention.

## 5. Counting from the Session Agent

- [x] 5.1 Make `ActivityTracker` send `exePath` (full path) next to `processName`, and for a Store app window (`ApplicationFrameHost.exe`) the exe of the hosted app; verify on the VM with `ssh winvm` that the service log or `app_usage` shows samples of the Kid session and that the request body has the path.
- [x] 5.2 In `POST /api/agent/activity`, record the exe in the per-user set of foreground exes of today, resolve the category (with a rule cache that the admin routes reset) and add the seconds to Category Usage, never for System or a locked session; verify on the VM: 3 minutes with a Games exe in front give Games usage 3 (±1) and Explorer in front gives no usage.

## 6. Enforcement on Windows

- [x] 6.1 Add `OpenProcess`, `QueryFullProcessImageNameW`, `GetProcessTimes`, `NtSuspendProcess` and `NtResumeProcess` to `NativeMethods`, and a small process service that lists the processes of a session with path and start time; verify a VM test section lists the test process itself with the correct path.
- [x] 6.2 Implement `CategoryEnforcer.Tick()` in the Service and call it from `UsageMonitorWorker.ProcessTick`: warnings, suspension with record-before-suspend, first-suspension message (`msg_category_suspended`), resume; verify on the VM as Kid with Games limit 2 minutes: a warning appears, the game is suspended within 60 s after 2 minutes, it still exists in Task Manager (status Suspended), a Study program still runs, and the session stays unlocked.
- [x] 6.3 Re-suspend check: list the thread states of recorded processes each tick; verify on the VM: as Kid, resume the suspended game with PowerShell (`[Diagnostics.Process]` + a small `NtResumeProcess` call) and see it suspended again within 60 s.
- [x] 6.4 Add the fallback for a failed suspend of a suspend candidate (lock the session + one Telegram alert per exe and day) and the re-check on unlock; verify on the VM with a process that the service cannot open (simulate with a test hook that forces the failure): the session locks and the log shows the alert.
- [x] 6.5 Resume on service stop (`StopAsync`) and reconcile records on start; verify on the VM: with a game suspended, `sc stop ParentalControlService` resumes the game; after `taskkill /f` of the service and a restart on the same day, the game stays suspended and the record remains.
- [x] 6.6 Call the enforcer at once after a bonus approval (web and Telegram) and after an admin change of a limit or rule; verify on the VM: approving 15 bonus minutes resumes the suspended game within a few seconds.
- [x] 6.7 Check on the VM that a suspended full-screen or borderless game leaves the desktop usable (Alt+Tab, Start menu); record the result in the PR. Expected: the desktop is usable. If not, write a follow-up issue for minimizing before suspend.

## 7. Mute through the agent

- [x] 7.1 Return the suspended pids of the user in the response of `POST /api/agent/activity`; add `NAudio.Wasapi` to the Agent and mute/restore the audio sessions of those pids; verify on the VM: a video player with sound goes silent on suspension and has sound again after resume, and an agent kill does not stop the suspension.

## 8. Web admin

- [x] 8.1 Add `GET/PUT /api/admin/categories` and `GET/POST/PUT/DELETE /api/admin/app-rules` (the rule carries the optional App Limit) behind the admin auth, with validation (0–1440 or no limit, no limit on System); verify with curl on the VM: no session gives 401, 2000 minutes gives 400, System limit gives 400, a valid change persists.
- [x] 8.2 Add the Categories section (category limits, rules with App Limit fields and a note that an App Limit shares the category time) and the "assign category" action in the App Usage table to `AdminPage.cs`, with HTML escaping of all exe text; verify in a browser against the VM: change a limit, add, change and delete a rule with an App Limit, assign an app from App Usage, and a window title with `<script>` shows as text.
- [x] 8.3 Add the default text of `msg_category_suspended` to the editable messages in the settings; verify the text can be changed in the web admin and the changed text appears on the next suspension on the VM.

## 9. Integration check

- [x] 9.1 Full run on the VM from the snapshot `clean-before-parental`: install the build, Kid logs on, a whole day flow with a Games limit of 3 minutes, an App Limit of 5 minutes on one game, and a session Allowance Time of 30 minutes (warning, suspension of the other games at 3 minutes, the App Limit game at 5 minutes, bonus, resume, Allowance Time lock still works); verify each step and record the results in the PR.
- [ ] 9.2 Update `docs/HANDOFF.md` (stage 1 done, problems found) and README "Key Features"; verify the text matches the merged behavior.

## Workflow follow-up

- Archive the change with `/opsx:archive` after the PR is merged.
