# Design

## Context

See proposal.md for the motivation and specs/ for the required behavior.

Current state that shapes the approach:
- The service is a LocalSystem `BackgroundService`. `UsageMonitorWorker` ticks every 60 seconds and checks the session Allowance Time and the Schedule Window per unlocked session of a Restricted User.
- The service never lists the processes of a session. Only the Session Agent sees the Foreground Window. It samples every 20 seconds and posts `{username, processName, windowTitle, durationSeconds}` to `POST /api/agent/activity`. It reads the full exe path (`QueryFullProcessImageNameW`) but sends only the file name without extension.
- `AppUsageRepository.AddSeconds` keeps an in-memory seconds accumulator per (user, process) and commits whole minutes to `app_usage` and `app_activity_hourly`.
- The schema is one `CREATE TABLE IF NOT EXISTS` block in `DatabaseManager`; migrations are ad hoc `pragma_table_info` checks. Repositories are static classes on raw `Microsoft.Data.Sqlite`.
- All projects target `net8.0-windows`; the tests are a console program that runs on Windows only.

## Goals / Non-Goals

**Goals:**
- Category logic (rule matching, limit of the day, what to suspend and resume) in pure code that runs and is tested on the Mac.
- Suspension that survives a service crash and never leaves a process suspended after the service stops cleanly.
- No change to how the session Allowance Time is counted or enforced.

**Non-Goals:**
- Accurate idle detection. A game in the foreground while the child is away counts, as the session time does today.
- Tamper resistance of the agent channel (stage 3).

## Decisions

### D1. Usage source: the agent's Foreground Window samples
Category Usage counts the foreground exe from the agent samples.
- *Alternative:* the service lists all processes in the session and counts a category while any of its processes runs. It is harder to fake, but a launcher or a chat app in the tray would count all day. Rejected for this stage; stage 3 adds the service process list as a check of the agent data.
- The agent now sends `exePath` (full path) in addition to `processName`. An old agent without `exePath` is resolved by file name only.

### D2. Pure logic in a new `ParentalControl.Domain` project
New project `src/ParentalControl.Domain` (`net8.0`, no WinAPI, no SQLite). Core references it. It holds:
- `AppCategory` (enum: Games, Video, Social, Study, Creative, System, Unknown) and `CategoryLimit` (weekday minutes, weekend minutes, both nullable).
- `AppRule` (match type, pattern, category, optional App Limit) and `CategoryResolver`: returns the category and the matched rule; canonical path (`Path.GetFullPath` semantics on a Windows-style string, separators `\`, case-insensitive), file-name rules first, then the longest folder prefix on a folder boundary, else Unknown.
- `CategoryLimitCalculator`: limit of the day (weekend = Saturday or Sunday of the given local date) plus Bonus Minutes. Each limit is either a Category Limit or an App Limit; both compare against the same Category Usage of the category. Returns per limit: remaining minutes, whether a 5- or 1-minute warning is due, and whether it is reached. An exe is exhausted when the Category Usage reaches its own limit (App Limit if its rule has one, else Category Limit).
- `SuspensionPlanner`: input = processes in the child's sessions (pid, start time, exe path, session id, whether all threads are suspended), the exhausted check per exe (from the resolver and the calculator), the set of exe paths that were a Foreground Window of the user today, and the current suspension records; output = processes to suspend (new, or recorded but running again), records to resume, records to drop. Never in the suspend list: the System category, the agent exe, `msedgewebview2.exe`, an exe below the Windows folder, processes without a path, and exes that were not a Foreground Window today.
- New test project `tests/ParentalControl.Domain.Tests` (`net8.0`, xUnit). It runs with `dotnet test` on the Mac and in `ci.yml`.
- *Alternative:* put the logic in Core and keep testing it on the VM only. Rejected: Core is `net8.0-windows` and the feedback loop through the VM is slow.

### D3. Data model
New tables in the `DatabaseManager` schema block (created on start, like `app_activity_hourly` in ADR 0003):
- `app_categories (category TEXT PRIMARY KEY, weekday_minutes INTEGER NULL, weekend_minutes INTEGER NULL)`: one row per `AppCategory`. NULL = no limit.
- `app_rules (id INTEGER PRIMARY KEY, match_type TEXT CHECK(match_type IN ('file','folder')), pattern TEXT, category TEXT, weekday_minutes INTEGER NULL, weekend_minutes INTEGER NULL, is_default INTEGER, created_at TEXT, UNIQUE(match_type, pattern COLLATE NOCASE))`. The two minute columns are the App Limit; NULL = the Category Limit applies.
- `category_usage (user_id INTEGER, date TEXT, category TEXT, minutes INTEGER, PRIMARY KEY(user_id, date, category))`.
- `suspended_processes (pid INTEGER, start_time_utc TEXT, session_id INTEGER, user_id INTEGER, category TEXT, exe_path TEXT, suspended_at TEXT, PRIMARY KEY(pid, start_time_utc))`.
- `settings` key `categories_seeded` (existing key/value table): the seed of default limits and default rules runs only while this key is absent. This keeps deleted default rules deleted.
- New settings message key `msg_category_suspended` with a default text, edited like the other messages.
- Retention: `category_usage` rows older than the existing usage retention are deleted in `RunCleanupIfNeeded`.

### D3a. App Limits share the category time
The parent chose that an App Limit shares the time of its category: all Games time counts toward Games, and an App Limit only moves the point where that app stops. So the maximum game time per day is the highest limit in the category (for example Minecraft 180 minutes), not a sum.
- *Alternative:* the app has its own counter that does not count toward the category (maximum = sum of limits). Rejected by the parent.
- An App Limit below the Category Limit stops that app earlier in the shared time: Roblox at 60 means "Roblox only while all games together are below 60 minutes", not "60 minutes of Roblox". The admin page states this next to the field.
- No per-app usage counter is needed; `app_usage` keeps the per-app statistics as before.

### D3b. Suspend only foreground-seen exes
A category covers many background processes that the child never uses directly: tray helpers, updaters, Store app hosts, anti-cheat services. Most are in Unknown. Suspending all of them when Unknown is exhausted would freeze helpers that Study apps need, and one helper that refuses the suspend handle would trigger the D6 session lock on every unlock.
- Decision: only an exe that was a Foreground Window of that user today is a suspend candidate. The service keeps this set in memory per user and day, filled by `POST /api/agent/activity`. A game started after the limit ended becomes a candidate with the first sample (20 s) and is suspended at the next tick.
- Trade-off: after a service restart the set is empty until the next samples, so a program that only plays in the background is not suspended until it comes to the front again.
- `msedgewebview2.exe` is a hard exception (HANDOFF stage 2), like the agent exe.
- An exe below the Windows folder is a hard exception too (user decision, 2026-11-26). If the parent deletes the default System rule for the Windows folder, `explorer.exe` and Task Manager move to Unknown and count there, but the service never suspends them. A file rule that puts a Windows exe (for example `mspaint.exe`) in a limited category also counts the time, but does not suspend the exe.

### D4. Counting
`POST /api/agent/activity` resolves the category of the sample (rules are cached in memory and reloaded when the admin changes them) and calls `CategoryUsageRepository.AddSeconds(userId, category, seconds)`. It uses the same seconds accumulator pattern as `AppUsageRepository` (in memory, commit whole minutes). The endpoint already ignores samples of a locked session; System samples are not counted.

### D5. Enforcement in the 60-second tick
After the existing session checks, `UsageMonitorWorker.ProcessTick` runs a new `CategoryEnforcer.Tick()` (in the Service project):
1. For each Restricted User with an unlocked session: read today's `category_usage`, the category limits and today's Bonus Minutes; ask `CategoryLimitCalculator` which categories are exhausted and which warnings are due. Send due warnings with `NotificationManager.SendMessage` (deduplicated per user, category and day in memory, like `_sentAlerts`).
2. List the processes of the child's sessions: `Process.GetProcesses()` filtered by `SessionId`; for each, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` for the path and `GetProcessTimes` for the start time.
3. `SuspensionPlanner` decides. To suspend: `OpenProcess(PROCESS_SUSPEND_RESUME)` + `NtSuspendProcess` (ntdll), then insert the record. To resume: verify pid + start time, `NtResumeProcess`, delete the record.
4. A process gets a second `NtSuspendProcess` only if one of its threads runs again (`ProcessThread.ThreadState != Wait` or `WaitReason != Suspended`). The child owns the process and can resume it with a tool (for example PowerShell, which is in System). `NtSuspendProcess` raises a suspend count per thread; the check keeps the count at 1, so one `NtResumeProcess` always clears it.
5. The first suspension of a category per user and day sends `msg_category_suspended`.

New P/Invokes in `NativeMethods`: `OpenProcess`, `QueryFullProcessImageNameW`, `GetProcessTimes`, `NtSuspendProcess`, `NtResumeProcess`.

### D6. Suspend failure fallback
If `OpenProcess` or `NtSuspendProcess` fails for a suspend candidate (D3b), the enforcer locks that session with `SessionManager.LockSession` and sends an alert through the existing Telegram Alert Channel (one per exe and day). The existing unlock handler in `SessionTracker` calls the enforcer for that session, so the check repeats on unlock.
- *Alternative:* leave the process running and only alert. Rejected: then a game with anti-cheat has no limit.

### D7. Resume triggers
- New day, Bonus Minutes, raised or removed limit, rule change: covered by the next tick, because the planner compares records with the current exhausted set. A bonus approval and an admin change call the enforcer at once, so the child does not wait for the tick.
- Service stop: the worker overrides `StopAsync` and resumes all records before it returns.
- Service start: the planner reconciles records against live processes (pid + start time); stale records are dropped.

### D8. Mute through the agent
The response of `POST /api/agent/activity` gets a list of the suspended pids of that user. The agent mutes the audio sessions of these pids with the Windows Core Audio API (`IAudioSessionManager2`, `ISimpleAudioVolume`) through the `NAudio.Wasapi` package (MIT) and restores the previous mute state when a pid leaves the list.
- *Alternative:* own COM interop for the audio interfaces. Rejected: more code for the same result.
- The service cannot do this itself: audio sessions belong to the user's session, and the service runs in session 0.

### D9. Web admin
New routes under the existing admin auth: `GET/PUT /api/admin/categories`, `GET/POST/DELETE /api/admin/app-rules`. `AdminPage.cs` gets a Categories section (limits table, rules table) and an "assign category" action in the App Usage table. All exe text is HTML-escaped as in the existing tables.

## Risks / Trade-offs

- [The agent channel has no authentication (problem 2 in HANDOFF): a child can post fake samples, for example a Study exe while a game runs.] → Accepted for this stage. Suspension does not depend on the samples: once Games is exhausted, every Games process is suspended. Stage 3 adds the service process list as a check.
- [Anti-cheat software (for example Vanguard or Easy Anti-Cheat) blocks the suspend handle or flags a suspended game.] → D6 fallback locks the session. Manual check on the VM with a Steam game; the parent can move such a game to a rule with a lower limit.
- [A suspended full-screen game can leave a frozen frame on the screen.] → Manual check on the VM. If it happens, a follow-up change makes the agent minimize the windows of a pid before the service suspends it.
- [A game loses its network session while suspended.] → Accepted: the program is not closed, as the parent asked.
- [Launchers and updaters (for example `steam.exe`) run in the background and are in Unknown by default, so they are suspended when Unknown is exhausted.] → Accepted: a suspended launcher does not harm the computer. The default rules do not cover launchers; the parent assigns each launcher to a category in the web admin.
- [A crash between `NtSuspendProcess` and the insert leaves a suspended process without a record.] → The insert happens before the suspend call; a failed suspend deletes the record. A record without a suspended process is harmless, because `NtResumeProcess` on a running process only fails or does nothing.

## Migration Plan

- The tables and the seed run on the first start of the new version. Rollback: install the previous version; it ignores the new tables. Before a rollback, stop the service so that it resumes all suspended processes.
- No change to existing tables.
