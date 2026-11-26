# Proposal

## Why

The parent wants to block some programs fully. For now, this includes all web browsers. The internet itself must stay available, because games and Steam need it. Today the service can only count and pause programs by category. It cannot stop the start of a program. The hard rule of `CLAUDE.md` permits exactly one close: a blocked program, at its start. This change adds that close and nothing more.

## What Changes

- An App Rule gets a block switch. A rule with the block makes each exe that it resolves a **Blocked Program**. The block uses the same match types (file name or folder) and the same rule precedence as the categories. A file rule without the block thus makes an exception to a blocked folder.
- The service detects process starts in the sessions of a Restricted User. It uses a Windows process start event, and the 60-second tick lists the processes as a fallback. It does not use data from the Session Agent.
- **Blocked Start**: when a process of a Blocked Program starts after the block took effect, the service closes it at once. This is the only close of a program of the child. A process counts as "at its start" only if the service detects it within 10 seconds after its creation.
- A process of a Blocked Program that ran before the block took effect, or that the service detects later than 10 seconds after its creation, is **not** closed. The service suspends it as a Suspended App. It stays suspended until it ends, until the parent removes the block, until the session ends, or until the service stops.
- Default blocked rules for the common browsers: `chrome.exe`, `firefox.exe` (also Tor Browser), `msedge.exe`, `opera.exe` (also Opera GX), `brave.exe`, `vivaldi.exe`, `browser.exe` (Yandex), `iexplore.exe`, `chromium.exe`, `waterfox.exe`, `librewolf.exe`, `floorp.exe`, `palemoon.exe`, `thorium.exe` and `DuckDuckGo.exe`. An existing install gets them one time. A rule with the same pattern that the parent made keeps its category and gets the block.
- `msedge.exe` is blocked. A start in the background (Startup Boost, background mode) is closed silently: the child gets no message for it.
- The service never blocks: an exe below the Windows folder, an exe below the Parental Control install folder (the service, the Session Agent, the admin app), `msedgewebview2.exe`, a process of another user or session, and a process whose path it cannot read.
- The child gets one session message per program and day. The texts are German by default, with "en" and "id" presets.
- The parent sees each blocked program of the day in the event log and through a new admin API route. An optional Telegram notice comes at most one time per program and day. It is on by default.
- The web admin API gets the block field on the existing App Rule routes and a new read route for the blocked starts of today. The UI for it goes into the new React admin (issue #26).

## Out of scope

- **Allowed programs** (an allow-list mode, where only listed programs can start). The Unknown category limit already limits each program without a rule. This is a follow-up change.
- A renamed browser or a portable browser in another folder (for example `Downloads\game.exe`). The service matches the file name and the folder only. Such a program is in Unknown and counts against the Unknown limit. A follow-up can match the version resource of the exe or a signature.
- Web filters, DNS filters, firewall rules or proxy settings. The internet stays open for all programs that are not blocked.
- Browsers inside other programs: `steamwebhelper.exe` (the Steam overlay browser), `msedgewebview2.exe` and Electron apps stay open.
- Programs below the Windows folder, for example `cmd.exe`, `powershell.exe` and `mspaint.exe`. The service cannot block them. Issue #24 covers the tools that the child can use against a Suspended App.
- AppLocker, Software Restriction Policies and Image File Execution Options. They act for all users of the PC and need other Windows editions or settings.
- A change of the Edge policies in the registry. The service does not write `StartupBoostEnabled` or `BackgroundModeEnabled`.
- The UI in the current server-rendered admin page (`AdminPage.cs`). ADR 0009 moves the admin UI to React.
- Authentication of the Session Agent channel (#6). This change does not read agent data for a block.
- The hard rule does not change for limits, breaks and schedules: they never close a program.

## Capabilities

### New Capabilities
- `blocked-programs`: the block on an App Rule, the default blocked browsers, the detection of process starts, the close of a Blocked Start, the suspension of a Blocked Program that already runs, the never-block list, the child message, and the parent report.

### Modified Capabilities
- `category-limits`: "Resume suspended programs" and "Suspensions survive a service restart" now apply to a process that the service suspended for a limit. A process that the service suspended because of a block follows `blocked-programs`.

## Impact

- `src/ParentalControl.Domain/Categories`: `AppRule` gets the block; `CategoryLimitCalculator` ignores the App Limit of a blocked rule; a new pure decision for a Blocked Start; one never-block check that the suspension planner and the block decision both use; the planner keeps a blocked suspension; the default blocked rules. New xUnit tests.
- `src/ParentalControl.Core`: `app_rules.is_blocked`, `suspended_processes.reason`, a new table `blocked_starts`, new settings keys, a new `EventType` value, `ProcessControl.Terminate` and `ProcessControl.QueryCommandLine`, new P/Invokes.
- `src/ParentalControl.Service`: a new process start watcher (WMI), a block enforcer, the tick fallback, the routes in `WebServerHost.cs`.
- New NuGet dependency `System.Management` (Microsoft, MIT) in the Service project.
- The child text catalog (`src/ParentalControl.Domain/Messages`, spec `child-messages`) gets one new child message key.
- `GLOSSARY.md`: new terms Blocked Program and Blocked Start; a wider definition of Suspended App.
- Dependencies: PR #21 (Admin Session), PR #22 (resume at session end), issue #26 (React admin UI). PR #20 (text catalog) is merged.
