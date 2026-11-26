# Design

## Context

See proposal.md for the motivation and `specs/` for the required behaviour.

The current code has these properties:

- `CategoryResolver` (Domain) selects one App Rule per exe: a file-name rule first, then the longest folder on a folder boundary. `CategoryConfigCache` (Core) holds the resolver and resets it on each rule write. It raises `Changed`.
- `CategoryEnforcer` (Service) runs in the 60-second tick of `UsageMonitorWorker`. It lists the processes of the restricted sessions with `ProcessControl.ListProcesses` (`Process.GetProcesses`, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW`, `GetProcessTimes`). `SuspensionPlanner` (Domain) decides what to suspend, resume and drop. Then the enforcer calls `NtSuspendProcess` and `NtResumeProcess`.
- `SuspensionPlanner` resumes each record whose exe has time left. It suspends only an exe that was a Foreground Window today (`ForegroundHistory`, in memory, empty after a service start). It never suspends the Session Agent exe, `msedgewebview2.exe` or an exe below the Windows folder.
- `suspended_processes` has no reason column. `ProcessControl` has no close call. Nothing in the code closes a program of the child.
- `app_rules` has `UNIQUE(match_type, pattern COLLATE NOCASE)`. The one-time seed of the default rules runs while the settings key `categories_seeded` is absent. On the child's PC, stage 1 already set this key.
- The service runs as LocalSystem in session 0. The Session Agent channel has no authentication (#6).
- `EventRepository` stores `EventType` as text.
- PR #20 (`add-german-child-texts`) is merged. The child texts are in the catalog `ChildMessageKeys` / `MessageCatalog` in `ParentalControl.Domain/Messages` (spec `child-messages`). At each start, `SettingsRepository.SeedChildMessages` writes the default text of the current preset for a key that has no stored text. A new key thus reaches an existing install without a migration.
- PR #28 sends an App Limit warning only while a process of the app is in the process list of the service (`CategoryEnforcer.RunningExes`, `DayLimits.DueWarnings`). A suspended process is still in that list.
- The draft PR #21 (`require-admin-sign-in`) puts one Admin Session gate in front of each `/api/admin/*` route. The draft PR #22 (`resume-suspended-apps-at-session-end`) resumes each record of `suspended_processes` when the session ends.

Each decision below was decided by the orchestrator under the user's delegation (2026-11-26).

## Goals / Non-Goals

**Goals:**
- One pure decision, in Domain, that says "close" for a process. `TerminateProcess` has one call site, and only this decision leads to it. A review can check the hard rule in one place.
- The block uses the existing resolver, precedence and caches. There is no second rule engine.
- The detection does not depend on data from the Session Agent.
- A blocked process that is not at its start is suspended with the existing suspension machinery. It thus gets the existing resume on a service stop, the re-suspension, the fallback, and the resume at session end of PR #22.

**Non-Goals:**
- Protection against a renamed or a portable browser (see Risks).
- Blocks for Administrators or for other users.
- A UI in the current `AdminPage.cs`.

## Decisions

### D1. The block is a property of an App Rule
Decided by the orchestrator under the user's delegation (2026-11-26).

New column `app_rules.is_blocked INTEGER NOT NULL DEFAULT 0`. `AppRule` gets `bool IsBlocked`. `CategoryResolution` already returns the selected rule, so `resolution.Rule?.IsBlocked == true` is the block of the exe. The precedence of `app-categories` thus also decides the block: a file rule without the block makes an exception below a blocked folder.

The rule keeps its category and its App Limit while the block is on. They have no effect while the block is on: a Blocked Start closes, and a blocked suspension does not depend on time. When the parent removes the block, they apply again. The parent can thus switch a block on and off without a loss of settings.

"No effect" for the App Limit: `CategoryLimitCalculator` leaves out the App Limit of a blocked rule, so the exe falls back to its Category Limit. Without this, a suspended blocked browser with an App Limit stays in the process list, and the App Limit warning of PR #28 tells the child that the blocked program ends soon. The exe still counts in its category, like each other exe of the category.

Rejected alternatives:
- *A new category "Blocked"*: the spec fixes the set of seven categories. The rule would also lose its real category, so a switch back would need a second edit.
- *A separate block list table*: a second matcher with its own precedence. A file rule in one table and a folder rule in the other table would have no clear winner.
- *Refuse an App Limit on a blocked rule*: a switch of the block would then delete a setting of the parent.

### D2. Validation of a block
Decided by the orchestrator under the user's delegation (2026-11-26).

`AppRule.Validate` gets the block and a `ProtectedPrograms` value (D7). It refuses:
- a block on a rule with the category System
- a blocked folder rule whose folder is the Windows folder, the install folder, or a folder below one of them
- a blocked file rule for `msedgewebview2.exe`, `ParentalControl.Service.exe`, `ParentalControl.Agent.exe` or `ParentalControl.Admin.exe`

A blocked file rule for a name like `notepad.exe` is permitted, because the name can also exist outside the Windows folder. D7 still protects the copy below the Windows folder.

Rejected alternative: *accept each block and let D7 ignore it in silence*. The parent would see a block that never works.

### D3. Detection: a WMI start event as a trigger, and the tick as a fallback
Decided by the orchestrator under the user's delegation (2026-11-26).

A new `ProcessStartWatcher` (Service) uses `System.Management.ManagementEventWatcher` with the query `SELECT ProcessID, SessionID FROM Win32_ProcessStartTrace`. This WMI class needs administrator rights; LocalSystem has them. The service gets a new NuGet package `System.Management` (Microsoft, MIT, version 8.0.x like the other Microsoft packages).

- The event is a trigger only. The service uses the `ProcessID` and the `SessionID`. It does not use `ProcessName`, because the kernel source of this event can cut the name, and the name is untrusted.
- The watcher drops an event at once if the `SessionID` is not a session of a Restricted User (from `SessionTracker.ActiveSessions` and `UserRepository`). At sign-in, a start event can arrive before `SessionTracker` knows the new session. Such a process is not closed. The tick finds it and suspends it (see Risks).
- The watcher also keeps the `ParentProcessID` of the event for D4.
- The watcher writes `(pid, sessionId, receivedUtc)` into a bounded `System.Threading.Channels` channel (capacity 1024, oldest dropped). One consumer task in the `BlockEnforcer` reads it. The WMI thread thus never waits for a database or a WinAPI call.
- The consumer reads the path and the creation time from the process: `ProcessControl.QueryPathAndStart` (`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW`, `GetProcessTimes`). A process that ended already is ignored.
- If the watcher stops with an error (the `Stopped` event, or a `ManagementException` at start), the service logs a warning and starts the watcher again at the next tick. While the watcher does not run, the tick fallback applies.
- Fallback: `CategoryEnforcer.Tick` already lists the processes of the restricted sessions each 60 seconds. It gives each blocked process to the decision of D4. A process that the watcher missed is thus older than 10 seconds when the tick sees it in most cases, and D4 suspends it.

Rejected alternatives:
- *ETW with the `Microsoft-Windows-Kernel-Process` provider (TraceEvent package)*: faster, but a larger dependency, more code, and a kernel trace session is a limited system resource.
- *WMI `__InstanceCreationEvent WITHIN n` on `Win32_Process`*: WMI polls internally. It costs more CPU and adds up to n seconds of delay.
- *Data from the Session Agent*: not authenticated (#6). The security rule of `CLAUDE.md` forbids a limit that depends on it.
- *Image File Execution Options (`Debugger` value), AppLocker, Software Restriction Policies*: they act for all users, also for the parent. AppLocker needs Windows Enterprise or Education. Each one also changes system settings outside the service.
- *A shorter tick (for example 2 seconds) instead of the event*: lists all processes 30 times more often for the same result.

### D4. "At its start": one pure decision
Decided by the orchestrator under the user's delegation (2026-11-26).

New pure class `BlockedStartDecider` in `ParentalControl.Domain/Categories`. Input: the resolution of the exe, the protected programs (D7), whether the session belongs to a Restricted User, the creation time of the process (UTC), the detection time (UTC), the **block epoch**, and the parent process (its pid, creation time and whether its exe is a Blocked Program). Output: `Close`, `Suspend` or `Ignore`.

- `Ignore`: the exe is not a Blocked Program, or D7 protects it, or the session is not restricted, or the path is not readable.
- `Close`: the creation time is later than the block epoch, the detection time minus the creation time is 10 seconds or less, and the parent is not an **old blocked parent**.
- `Suspend`: each other blocked process.

An old blocked parent is a live process of a Blocked Program whose creation time is at or before the block epoch and before the creation time of the new process. The second time check stops a reused pid from a match. Reason: a Chromium browser makes a new renderer, GPU or utility process (`--type=...`) for a new tab, and Firefox makes a new `-contentproc` process. Without this input, a browser that ran before the block gets its new tabs closed in the gap before its suspension: after a rule change, after a service start, after the child resumes it with a tool, and inside one tick, because the tick runs D4 before the planner. A closed tab can lose text that the child typed.

The parent pid comes from the `ParentProcessID` of the WMI event, and in the tick from `NtQueryInformationProcess` with the class `ProcessBasicInformation` (0, field `InheritedFromUniqueProcessId`). The service reads the creation time and the path of the parent with `ProcessControl.QueryPathAndStart`.

The block epoch is the later of two times: the start of the `BlockEnforcer` in this service run, and the last change of any App Rule in this service run. It is in memory only. After a service start, it is the start time, so each process that ran before is suspended, not closed.

Implementation (decided during `/opsx:apply`): the "last change" is the build time of the rule snapshot that resolved the exe (`CategoryConfigCache.Snapshot.BuiltUtc`, set after the read of the rules), not the time of the `Changed` event. `Invalidate()` clears the cache before it raises `Changed`, so another thread can build a snapshot with the new block before an event handler sets the epoch. A snapshot that contains a change is always built after the commit of that change, so the epoch is never too early. A snapshot also changes after a Category Limit change; the epoch then moves too, which only turns a possible close into a suspension.

Each rule change sets the epoch, not only a change of the block. Example: a folder rule blocks `C:\Tools\` since Monday, and a file rule allows `C:\Tools\calc2.exe`. The child starts `calc2.exe` on Tuesday. Then the parent deletes the file rule. `calc2.exe` is now blocked, but it started before the change, so it is suspended, not closed.

The limit of 10 seconds: the WMI event arrives within 1 second in normal operation. At sign-in the PC can be slow for some seconds. In 10 seconds the child cannot do work in the program that a close can lose.

The `BlockEnforcer` calls `ProcessControl.Terminate` only for `Close`, and no other code calls it.

Rejected alternatives:
- *A `blocked_since` column per rule*: it fails in the example above, because the folder rule did not change.
- *Close each blocked process that the service finds, at any age*: a browser that the tick finds after 59 seconds can hold text that the child typed. That is not "at its start".
- *Decide by the detection path (an event means close, the tick means suspend)*: the age is the fact that matters, not the path. The decision would also be harder to test.
- *No parent input; trust the tick that `RequestTick` starts after a rule change to suspend the old browser first*: the tick and the start event run on different threads, so a new tab can still arrive first. The parent check has no race.

### D5. The close
Decided by the orchestrator under the user's delegation (2026-11-26).

New method `ProcessControl.Terminate(int pid, DateTime expectedStartUtc, out string detail)`. It uses the pattern of `WithVerifiedHandle`: `OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION)`, a check of the creation time with `GetProcessTimes` (the pid can belong to a new process), then `TerminateProcess(handle, 1)`. New P/Invoke in `NativeMethods`: `TerminateProcess`, and the constant `PROCESS_TERMINATE = 0x0001`.

A Chromium or Firefox browser starts more processes of the same exe. Each one is its own start, and D4 closes each one. A helper of another exe (for example `crashpad_handler`) ends by itself when its parent ends.

If `Terminate` fails with "access denied" or another error, the `BlockEnforcer` asks the `CategoryEnforcer` for a tick at once (`RequestTick`). The planner then suspends the process (D6). If the suspension also fails, the existing fallback of `category-limits` locks the session and alerts the parent. The `BlockEnforcer` keeps the pid and start time of a failed close for 2 minutes and gives `Suspend` for it, so the tick does not try the close again and again. The environment variable `PARENTAL_CONTROL_TEST_TERMINATE_FAIL` (an exe file name) forces a failed close on a test machine, like `PARENTAL_CONTROL_TEST_SUSPEND_FAIL` of stage 1.

### D6. A blocked process that already runs is suspended
Decided by the orchestrator under the user's delegation (2026-11-26).

- `ExeVerdict` gets `bool IsBlocked`. The verdict function of `CategoryEnforcer` sets it from the resolution and D7.
- New column `suspended_processes.reason TEXT NOT NULL DEFAULT 'limit'`, with the values `limit` and `blocked`. `SuspensionRecord` and `SuspendAction` get a `SuspensionReason`.
- The planner suspends a blocked process with the reason `blocked`. It does not require that the exe was a Foreground Window. The parent named the program, so its background processes stop too. A browser in background mode has no window.
- The planner skips a blocked process that `Close` (D4) applies to, so that the tick does not suspend a process that the `BlockEnforcer` closes in the same second. The tick runs D4 before the planner.
- A record with the reason `blocked` stays while the exe is blocked. When the block ends: if the exe has no time left and was a Foreground Window today, the record changes to `limit`; else the planner resumes the process.
- A record with the reason `limit` whose exe becomes a Blocked Program changes to `blocked` (decided during `/opsx:apply`). Else the limit reset of the next day resumes a blocked browser. The plan has a list `ToRelabel` for these changes.
- The re-suspension after a resume by a tool, the resume at a service stop (`ResumeAll`), and the D6 fallback of stage 1 apply without a change, because they use the same records.
- PR #22 resumes each record when the session ends. A blocked suspension thus does not bring back issue #4.

Rejected alternatives:
- *Close it anyway*: breaks the hard rule.
- *Leave it open until it ends*: the child keeps the browser for as long as the PC runs.
- *A separate table for blocked suspensions*: the resume at a service stop and at session end would need a second path.

### D7. One list of programs that are never blocked
Decided by the orchestrator under the user's delegation (2026-11-26).

New pure record `ProtectedPrograms` in Domain: the Windows folder, the install folder (`AppContext.BaseDirectory` of the service), and the file name `msedgewebview2.exe`. `IsProtected(canonicalPath)` is true for an exe below one of the folders or with that file name. D4 and the block path of the planner use it, and D2 uses it for validation.

The install folder covers the service, the Session Agent, the admin app and the updater. The default System rule for the install folder is not enough, because the parent can delete it. The limit path of the planner keeps its stage-1 exclusions without a change.

Consequence: `cmd.exe`, `powershell.exe`, `mspaint.exe` and other programs below the Windows folder cannot be blocked. Issue #24 covers the tools that the child can use against a Suspended App.

Rejected alternative: *only the System rule protects these folders*. One delete by the parent would let the service close Explorer or itself.

### D8. Default blocked browsers and the seed
Decided by the orchestrator under the user's delegation (2026-11-26).

New list `CategoryDefaults.BlockedBrowsers`: `chrome.exe`, `firefox.exe`, `msedge.exe`, `opera.exe`, `brave.exe`, `vivaldi.exe`, `browser.exe`, `iexplore.exe`, `chromium.exe`, `waterfox.exe`, `librewolf.exe`, `floorp.exe`, `palemoon.exe`, `thorium.exe`, `DuckDuckGo.exe`. Each is a file rule with the category Unknown and the block on.

- New settings key `blocked_defaults_seeded`. The seed runs while the key is absent, on a new and on an existing database, after the seed of `categories_seeded`. The new key is necessary because `categories_seeded` is already set on the child's PC.
- If a rule with the same match type and pattern exists, the seed sets `is_blocked = 1` on it and keeps its category and App Limit. The user asked to block all browsers, and the parent can remove the block with one switch.
- The seed and the key write run in one transaction.
- An existing rule with the same pattern and the category System keeps no block, because D2 refuses a block on System (decided during `/opsx:apply`). The seed adds no second rule for it.
- Category Unknown: when the parent removes the block, the browser counts as if it had no rule.
- Tor Browser runs `Tor Browser\Browser\firefox.exe`, so the `firefox.exe` rule covers it in each folder. A folder rule for Tor Browser adds nothing, because the file rule wins, and the folder can be anywhere.
- Opera GX runs `opera.exe` from its own folder. Its `launcher.exe` starts `opera.exe`, which is then closed.

Rejected alternatives:
- *Keep the existing rule unchanged*: on the child's PC the requirement "browsers blocked" would then fail without a sign.
- *A rule for `launcher.exe`*: many game launchers have this file name.
- *`zen.exe`, `arc.exe`*: short, generic names with a high risk of a wrong match. The parent can add them.

### D9. msedge.exe
Decided by the orchestrator under the user's delegation (2026-11-26).

Facts that the decision uses:
- Edge starts `msedge.exe --no-startup-window --win-session-start` at sign-in (Startup Boost and "start on sign-in"). Edge also keeps processes in background mode after the last window closes. Both have no window.
- Windows opens `microsoft-edge:` links in `msedge.exe`: web results of the Windows search, links in "News and interests" or Widgets, help links in Settings, and `iexplore.exe`, which redirects to Edge.
- Edge is the default viewer for PDF files and for `.html` files on a new Windows install.
- The Widgets board, Teams, the new Outlook, Office add-ins and many installers use `msedgewebview2.exe`, not `msedge.exe`. The search box uses `SearchApp.exe` / `SearchHost.exe` below the Windows folder. `MicrosoftEdgeUpdate.exe` and `elevation_service.exe` run as SYSTEM in session 0.

Decision: `msedge.exe` is a default blocked rule. A Blocked Program process whose command line contains `--no-startup-window` (a background start) or `--type=` (a sub-process of a Chromium browser) is closed or suspended silently: no child message and no Telegram notice. The suspension is silent too, because a background Edge at sign-in can miss the 10-second window (D3). The record and the event log entry stay. The service reads the command line with `NtQueryInformationProcess` and the class `ProcessCommandLineInformation` (60), with the `PROCESS_QUERY_LIMITED_INFORMATION` handle (Windows 8.1 and later). New P/Invoke in `NativeMethods`: `NtQueryInformationProcess` (ntdll). The command line is untrusted. It only selects the silent path; it never changes `Close` to another result (spec: "Process data for a block is untrusted").

The parent documentation says: install another PDF reader and make it the default, because a PDF does not open in a blocked Edge.

Rejected alternatives:
- *Allow `msedge.exe`*: the child can browse with Edge.
- *Write the Edge policies `StartupBoostEnabled = 0` and `BackgroundModeEnabled = 0` in `HKLM\SOFTWARE\Policies\Microsoft\Edge`*: they apply also to the parent's account, Edge then shows "managed by your organization", and it is a system change outside the service data. The silent close gives the same result for the child.
- *A message for each background start*: the child would get a message about Edge at each sign-in without an action of its own.

### D10. The child message
Decided by the orchestrator under the user's delegation (2026-11-26).

New child message key `msg_program_blocked` with the placeholder `{name}` in the catalog (`ChildMessageKeys`, `MessageCatalog` in `ParentalControl.Domain/Messages`). The parent can edit it like the other `msg_*` keys. `SeedChildMessages` writes the text of the current preset on an existing install, and a preset switch changes it like the other keys (spec `child-messages`).

| Preset | Text |
|---|---|
| de | `{name} ist auf diesem Computer gesperrt. Frag deine Eltern, wenn du das Programm brauchst.` |
| en | `{name} is blocked on this computer. Ask your parents if you need this program.` |
| id | `{name} diblokir di komputer ini. Tanya orang tuamu jika kamu membutuhkan program ini.` |

- `{name}` is the exe file name without `.exe` (for example `chrome`), like `AppName` in `CategoryEnforcer`. It is the same text for a close and for a suspension.
- The service sends it with `NotificationManager.SendMessage` (`WTSSendMessageW`, no wait for an answer). Not through the Session Agent, because the agent can be absent.
- One message per Restricted User, program (file name, letter case ignored) and day. The flag `child_notified` in `blocked_starts` (D11) keeps this also after a service restart.

Rejected alternatives:
- *A message for each start*: the child gets many messages if it clicks many times.
- *A friendly program name (for example "Google Chrome") in the rule*: a new column and a new field for each rule. The file name is enough for now.

### D11. The parent report
Decided by the orchestrator under the user's delegation (2026-11-26).

New table:

```
blocked_starts (
  user_id INTEGER NOT NULL,
  date TEXT NOT NULL,                -- local date yyyy-MM-dd
  file_name TEXT NOT NULL COLLATE NOCASE,
  exe_path TEXT NOT NULL,            -- last path seen
  closed_count INTEGER NOT NULL DEFAULT 0,
  suspended_count INTEGER NOT NULL DEFAULT 0,
  first_at TEXT NOT NULL,
  last_at TEXT NOT NULL,
  child_notified INTEGER NOT NULL DEFAULT 0,
  parent_notified INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, date, file_name)
)
```

- Each close or blocked suspension does one upsert. The first insert of a row writes one event log entry with the new `EventType.PROGRAM_BLOCKED`, added at the end of the enum, for example "Blocked program chrome.exe closed at start". A background start (`--no-startup-window`, D9) counts in `closed_count` too.
- A sub-process of a browser (a token `--type=...` of Chromium, or `-contentproc` of Firefox) does not count in `closed_count` or `suspended_count`; it only updates the path and `last_at` (decided during `/opsx:apply`). One Chrome start makes several `--type=` processes within milliseconds, and the watcher closes each one. Without this rule, one start gives a count above 1, and the spec scenario "Three starts of Chrome" (count 3) fails. A Firefox `-contentproc` process is not silent (the spec lists only `--no-startup-window` and `--type=`), but the daily dedupe of the message applies.
- `RunCleanupIfNeeded` deletes rows older than the usage retention.
- New route `GET /api/admin/blocked-starts?date=yyyy-MM-dd` (default today): a list of user name, file name, path, counts and times. The path and the file name are untrusted text; the React admin shows them as text.

Rejected alternative: *only the event log, one entry per start*: a child that clicks 50 times makes 50 entries, and the counts and dedupe flags would need a scan of the log.

### D12. The Telegram notice
Decided by the orchestrator under the user's delegation (2026-11-26).

New settings key `notify_blocked_telegram`, default `1`. At the first blocked start of a program, user and day that is not silent, and if a Telegram chat ID is set, the service sends one notice and sets `parent_notified`. German text, because PR #21 decided that new parent texts are German:

`Parental Control: {user} wollte {file} starten. Das Programm ist gesperrt und wurde geschlossen.` For a suspension the last sentence is `Das Programm ist gesperrt und wurde pausiert.` The path follows on a new line. The service escapes the Markdown characters as `CategoryEnforcer.SendParentAlert` does.

Rejected alternatives:
- *Off by default*: the parent asked to know about tries, and one notice per program and day limits the noise.
- *A notice for each start*: too many messages.
- *Use the language rule of `child-messages` for parent texts*: PR #21 is the newer decision for new parent texts.

### D13. The admin API
Decided by the orchestrator under the user's delegation (2026-11-26).

- `GET /api/admin/app-rules` returns `blocked` for each rule.
- `POST /api/admin/app-rules` reads the optional boolean `blocked` (missing = `false`).
- `PUT /api/admin/app-rules/{id}` reads `blocked`. A missing field keeps the stored value. The current `AdminPage.cs` sends no `blocked`, so an edit of the category there does not remove a block.
- `GET /api/admin/blocked-starts` (D11).
- `GET /api/admin/settings` and `POST /api/admin/settings` get `notifyBlockedTelegram`, and `msgProgramBlocked` like the other editable child texts (D10).
- The validation of D2 returns status 400 with an error text.
- All routes are below `/api/admin/`, so the Admin Session gate of PR #21 covers them without a change. Until PR #21 merges, they use the current `CheckAuth`.
- The UI is a task for the React admin (issue #26): a block switch in the rules table, a "Blocked today" list, and the notice setting. Until #26, the default rules work without a UI, and the parent can call the API.

Rejected alternative: *separate routes for blocked rules*: two ways to change one rule.

### D14. Order of the work and the dependencies
Decided by the orchestrator under the user's delegation (2026-11-26).

- PR #20 (the text catalog) is merged. D10 adds a key to that catalog.
- PR #22 merges before this change reaches the child's PC. Without it, a suspended blocked browser has the sign-out problem of issue #4.
- PR #21 is not a code dependency (D13), but the child's PC needs it, because without it the child can remove a block through the API (#18).
- Issue #26 is a dependency of the UI tasks only.
- PR #22 also modifies the requirement "Resume suspended programs" of `category-limits`. The change that is archived second must merge both texts in its MODIFIED block before its archive.

## Risks / Trade-offs

- [A renamed or a portable browser (for example `Downloads\game.exe`) is not blocked.] → It is in Unknown and counts against the Unknown limit. Follow-up: match the version resource (`OriginalFilename`) or the signature. The parent sees new Unknown programs in the App Usage table.
- [`browser.exe` is a generic name. Another program with this name is closed at its start.] → Accepted. The parent can remove the block of that rule.
- [Edge is the default PDF and HTML viewer. These files do not open.] → The parent documentation tells the parent to install another PDF reader.
- [A game launcher that opens a web page for sign-in or for news fails to open it.] → Accepted for now. The parent sees the try in the report. The launcher itself keeps running.
- [The browser window shows for up to about 1 second before the close.] → Accepted.
- [The WMI service (`winmgmt`) stops or fails.] → The tick fallback finds the browser within 60 seconds and suspends it. The watcher starts again at the next tick.
- [A Blocked Start in the first seconds after sign-in can take longer than 10 seconds to detect on a slow PC, or its event can arrive before `SessionTracker` knows the session.] → Then the process is suspended, not closed. The suspension of a browser holds no risk for the child's data. Check the timing on VM 102.
- [The child resumes a suspended blocked browser with a tool again and again.] → The same re-suspension as for limits, within 60 seconds. Issue #24 handles the repeated case.
- [A process that an Administrator starts with "Run as administrator" in the child's session is treated as the child's process.] → The same session rule as `category-limits`. Accepted.
- [A browser inside another program (Steam overlay, Electron apps) still gives web access.] → Out of scope.
- [The admin API without PR #21 lets the child remove a block (#18).] → Do not install this change on the child's PC before PR #21.

## Migration Plan

1. On the first start of the new version, `DatabaseManager` adds `app_rules.is_blocked` and `suspended_processes.reason` with `pragma_table_info` checks (`ALTER TABLE ... ADD COLUMN`), and creates `blocked_starts` with `CREATE TABLE IF NOT EXISTS`.
2. The seed of D8 runs one time and sets `blocked_defaults_seeded`.
3. The `ProcessStartWatcher` starts after the database is ready. Running browsers of the child are suspended at the first tick, not closed.
4. Rollback: stop the service first, so that it resumes each suspended process. The older version ignores the new columns and the new table. It reads each record of `suspended_processes` as a limit record and resumes it if the exe has time left.

## Open Questions

- The limit of 10 seconds for "at its start" can move after the timing check on VM 102 and on the child's PC. A different value changes no requirement structure, only the number in the spec and in one constant.
