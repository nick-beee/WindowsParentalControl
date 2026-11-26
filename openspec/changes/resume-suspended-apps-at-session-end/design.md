# Design

## Context

See proposal.md for the motivation and specs/ for the required behavior.

Current state that shapes the approach:
- The service is a LocalSystem service in session 0. `CategoryEnforcer.Tick()` runs every 60 seconds (and on `RequestTick()`). It suspends a process with `NtSuspendProcess`, records it in `suspended_processes` (pid + start time), and suspends a recorded process again if one of its threads runs again (D5 of `add-app-categories`).
- `ParentalControlServiceLifetime.OnSessionChange` gets `SessionLogoff` only after the session ended. Windows has no session change event at the start of a sign-out.
- The Session Agent runs in the child's session with the child's token (WPF, `ShutdownMode.OnExplicitShutdown`). It references Core. It polls `GET /api/agent/status` every 5 seconds and posts `POST /api/agent/activity` every 20 seconds. The activity response carries `suspendedPids` for the audio mute.
- The child owns the processes of the session. The child (and so the agent) can open them with `PROCESS_SUSPEND_RESUME`. The child can already resume a Suspended App with a tool, and the service suspends it again within 60 seconds. The spec accepts this.
- The agent channel `127.0.0.1:5050` has no authentication (issue #6). Each request that the agent can send, the child can send too.

Facts from the VM run (Windows 10 22H2, task 6.7 of `add-app-categories`):
- A suspended process does not answer `WM_QUERYENDSESSION`. Windows shows "This app is preventing you from signing out". If nobody clicks, Windows cancels the sign-out, but it already closed the other programs of the child.
- "Sign out anyway" also ends the suspended game.
- The window of a suspended process ignores the mouse and does not minimize. Alt+Tab, Start and other windows work.

## Goals / Non-Goals

**Goals:**
- A Suspended App never makes Windows close the other programs of the child at a sign-out, a restart or a shutdown.
- After a cancelled sign-out, each game that is still there is suspended again.
- A frozen window does not cover the screen: the agent minimizes it before the suspension.
- The new unauthenticated request gains the child at most a short, bounded play time.
- The hold logic is pure code with xUnit tests on the Mac.

**Non-Goals:**
- Authentication of the agent channel (issue #6).
- A reliable end-of-session path without the Session Agent.
- Detection of a repeated resume with a tool.

## Decisions

### D1. Detect the end of the session in the Session Agent
The agent creates a hidden top-level window (WPF `HwndSource`, style `WS_POPUP`, never shown) and hooks its window procedure. Windows sends `WM_QUERYENDSESSION` and `WM_ENDSESSION` to every top-level window of the session, also to a hidden one.
- `WM_QUERYENDSESSION` without `ENDSESSION_CLOSEAPP` in `lParam`: start the session-end resume (D3). Then return TRUE. This covers `lParam` = `0` (shutdown), `ENDSESSION_LOGOFF` (0x80000000, sign-out) and `ENDSESSION_CRITICAL` (0x40000000, the restart of R4). The agent never blocks or cancels the end of the session.
- `lParam` with `ENDSESSION_CLOSEAPP` (0x1): the Restart Manager closes only this program, for example the installer during an upgrade. The session does not end, so the agent does nothing. The service stops during an upgrade and resumes all processes anyway.
- The agent does not handle `WM_ENDSESSION`. Windows sends `WM_ENDSESSION(TRUE)` to each program right after it answers, so a cancel never reaches the agent (R2).
- *Alternative:* `SERVICE_CONTROL_PRESHUTDOWN` in the service. Rejected: Windows does not send it for a sign-out. For a shutdown, Windows first ends the user sessions and queries their programs, and only then sends the service controls. Task 1.3 checks this order on the VM.
- *Alternative:* the `SessionLogoff` session change event. Rejected: it arrives after the session ended.
- *Alternative:* SENS logoff events or the Winlogon event log. Rejected: Windows does not document that they arrive before the programs are queried.
- *Alternative:* WPF `Application.SessionEnding` or `SystemEvents.SessionEnding`. Rejected: both map `lParam` to "logoff" or "shutdown" only. They cannot tell `ENDSESSION_CLOSEAPP` apart, so an agent upgrade would resume the Suspended Apps.

### D2. The agent gets the query before the game
At start, the agent calls `SetProcessShutdownParameters(0x3FF, SHUTDOWN_NORETRY)` (kernel32). 0x3FF is the highest level for applications, and Windows queries a higher level first. A game has the default level 0x280. So the agent resumes the game before Windows asks the game. `SHUTDOWN_NORETRY` makes sure that the agent itself never shows in the "This app is preventing" list.
- If the order does not hold on a computer, the game is resumed some seconds later anyway. Windows then sees the game answer, and the blocking screen closes. Task 1.1 checks both cases on the VM.

### D3. Two resume paths: in the agent and in the service
The `WM_QUERYENDSESSION` handler runs on the UI thread and must return well within the hung-app timeout of Windows (`HungAppTimeout`, 5 seconds by default). It does two things, in this order:
1. **Local resume.** For each Suspended App of the last status poll (pid and start time, at most 5 seconds old, D6), the agent calls `ProcessControl.Resume(pid, startTimeUtc)` from Core (`OpenProcess` with `PROCESS_SUSPEND_RESUME`, `GetProcessTimes`, `NtResumeProcess`). This takes milliseconds and needs no service. It gives the child no new power, because the child owns these processes.
2. **Service request.** The agent sends `POST /api/agent/session-ending` with `{ sessionId, reason }` (`reason` = `logoff` or `shutdown`) as a synchronous `HttpClient.Send` with a timeout of 2 seconds. The service checks that the session id is a session of a Restricted User, asks the hold policy (D4), and if it grants the hold, resumes every record of that session with `ProcessControl.Resume`. The service path also covers a process that the service suspended in the last 5 seconds, and a process whose handle the agent cannot get (for example, anti-cheat software that strips user handles but accepts LocalSystem).
- The service does not delete the records. After a completed sign-out, the processes are gone, and the planner drops the records (pid + start time). After a cancelled sign-out, the records make the processes suspend again.
- *Alternative:* the service path only. Rejected: an HTTP call in the critical path. If the service is busy in a tick (the enforcer lock), the game is still frozen when Windows queries it.
- *Alternative:* the local path only (no new endpoint). Rejected: then nothing stops the 60-second tick. The tick can suspend the game again between the resume and the moment Windows queries the game. A sign-out with other programs that save can take more than 30 seconds, so this race is likely.

### D4. Session End Hold with a rate limit (the security trade-off)
New pure class `SessionEndHolds` in `ParentalControl.Domain/Categories` (no WinAPI, xUnit tests):
- `Request(sessionId, userId, nowUtc, trusted)` returns Granted, AlreadyHeld or Refused. A hold lasts `HoldDuration` (default 60 seconds). A request for a session that has an active hold does not extend it (AlreadyHeld).
- A request that is not `trusted` (from the agent) is refused if the user has a hold that started in the last `UnconfirmedWindow` (default 15 minutes) and that is not confirmed. This includes an active hold of another session, and a hold that timed out but that `Expire` did not process yet. A hold that ended with the end of the session is confirmed and does not count. So "sign out, sign in, sign out again" works.
- `SessionEnded(sessionId)` ends the hold as confirmed. `HeldSessions(nowUtc)` tells the planner.
- `Expire(nowUtc, today, sessionExists)` ends each hold whose time is over. If the session still exists, the hold is unconfirmed, and the result tells whether the parent gets a Telegram alert: yes for the first unconfirmed hold of the user on a local date, no for each later one on the same date. If the session does not exist any more, the hold is confirmed. This covers a Forced Logoff, because the route removes the session from the tracker at once.
- A trusted hold (Forced Logoff, D5) is never refused. If it ends unconfirmed (the logoff failed), it counts against the limit like every other hold, and the parent gets the alert. That case shows a fault, so the alert is correct.
- The hold state is in memory in `CategoryEnforcer`. No new table and no new column.

`SuspensionPlanner` gets a new input `HeldSessions`. It does not suspend a process in a held session, new or recorded. A recorded process in a held session keeps its record. A resume because time is available again still happens.

The end of a hold:
- Timeout: the enforcer schedules a follow-up tick shortly after the end of the hold. If the session still exists, the tick makes each process whose exe has no time left a Pending Suspension (D9), and the next follow-up tick suspends it 10 seconds later. So a process is suspended again within about 20 seconds after the end of the hold.
- After a timeout while the session still exists (an unconfirmed hold), the enforcer writes an event to the `events` table. If `SessionEndHolds` says so, it also sends a Telegram alert that names the user ("a sign-out did not complete"). So the parent gets at most one such alert per Restricted User per day.
- Session end: `SessionTracker.OnUserLogoff` and `SynchronizeSessions` (a stale session) raise a new event `SessionEnded(sessionId)`. `OnUserLogoff` raises it also if the tracker does not know the session any more (Forced Logoff removes it first). The enforcer ends the hold as confirmed.
- There is no cancel request. A cancelled sign-out ends the hold by its timeout (decision 2 below).

**Trade-off.** The session-ending request is unauthenticated. The child can send it with `curl`, or can send a fake `WM_QUERYENDSESSION` to the agent window. A granted fake request gains the child at most `HoldDuration` plus about 20 seconds (a follow-up tick and the Pending Suspension), so about 80 seconds per 15 minutes, about 9% of the time. A refused request gains nothing through the service. Through the agent, it gains at most 70 seconds until the tick suspends again. This equals what a tool already gives the child (the spec accepts it, "Keep a suspended process suspended"). Each request, refusal and unconfirmed hold goes to the log and to the event table, and an unconfirmed hold alerts the parent.
- *Alternative:* verify the end of the session in the service. Rejected: no service-side signal comes before Windows queries the programs (D1). The service can only confirm afterwards (`SessionLogoff`). The rate limit uses exactly this confirmation.
- *Alternative:* re-suspend a recorded process only after the service saw it run for 60 seconds, for every process (no new request). Rejected: it doubles the time that a tool resume gains (up to 120 seconds) all day, and it does not stop a new suspension during a sign-out.
- *Alternative:* check the caller of the request (the owner pid of the TCP connection with `GetExtendedTcpTable`, then the exe path and the session of that pid). Not in this change: a fake `WM_QUERYENDSESSION` to the real agent passes this check, so it adds little. Issue #6 adds a protected channel.

### D5. Forced Logoff resumes first
`POST /api/admin/force-logoff` (admin auth) calls `CategoryEnforcer.ResumeSessionForEnd(sessionId, trusted: true)` before `SessionManager.ForceLogoff` (`WTSLogoffSession`). The call starts a trusted hold without the rate limit. The logon denial in `SessionTracker.OnUserLogon` also calls `ForceLogoff`, but a new session has no Suspended App, so it needs no change.

### D6. The status poll carries the Suspended Apps and the Pending Suspensions
`GET /api/agent/status` adds two lists for a Restricted User:
- `suspendedApps: [{ pid, startTimeUtc }]` from `SuspensionRepository.GetForUser`. The agent keeps the last list for the local resume of D3.
- `pendingApps: [{ pid, startTimeUtc }]`: the Pending Suspensions of the user (D9). The agent minimizes their windows (D7).

The status poll runs every 5 seconds, so at least one poll falls into the 10 seconds of a Pending Suspension. The activity response comes every 20 seconds only, so it cannot carry the pending list. The `suspendedPids` of the activity response stay for the audio mute. The lists only name processes of the child, so they give the child no new information.

### D7. Minimize the windows before the suspension (Pending Suspension)
The window of a suspended process cannot be minimized: Windows queues `SW_FORCEMINIMIZE` until the process runs again (R3). So the agent minimizes the window *before* the suspension:
1. The service makes a process that is due for suspension a Pending Suspension (D9) and lists it in `pendingApps` of the status response.
2. On each status poll, for each pending pid, the agent checks that the process still has the listed start time (`ProcessControl.QueryPathAndStart`). Then it finds the top-level windows of the pid (`EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`) and calls `ShowWindowAsync(hwnd, SW_MINIMIZE)` for each visible window that is not minimized. The process still runs, so the window handles the request at once.
3. The service suspends the process 10 seconds after the start of the Pending Suspension, also if the agent does not run or does not answer.

`EnumWindows` only lists the windows of the desktop of the agent, so the agent never touches a window of another session.

The agent does not minimize a window again after the suspension. That cannot work (R3). After Alt+Tab or a click on the taskbar button, Windows queues the restore of a suspended window, so the window does not come to the front, and the desktop stays usable (R3).

**Security note.** A minimized window is a request to the window, and the child can restore the window before the suspension. The security rule holds because the suspension never depends on the agent: the service suspends at the end of the 10 seconds without an answer of the agent.

- *Alternative (rejected):* `SW_FORCEMINIMIZE` on the suspended window. R3 shows that Windows queues it until the process runs again.
- *Alternative (rejected):* a short resume-minimize-suspend cycle in the agent (R3, `minimize-resume`). It races with the enforcer: if the service resumes or suspends the process during the cycle, the suspend count of Windows goes wrong, and a game can stay frozen without a record. Nothing then resumes it.
- *Alternative (rejected):* no minimization. The frozen window then covers the screen, as before this change.
- *Alternative (rejected):* the service minimizes the windows. The service runs in session 0, and a window of the child's session is not reachable from session 0 (session isolation).

### D8. New WinAPI and no new storage
- New P/Invokes: `SetProcessShutdownParameters` (kernel32), `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`, `ShowWindowAsync` with `SW_MINIMIZE` (user32). Message: `WM_QUERYENDSESSION` (0x11), with `ENDSESSION_CLOSEAPP`.
- Reused from Core: `OpenProcess`, `GetProcessTimes`, `NtResumeProcess` (in `ProcessControl.Resume`), `WTSLogoffSession`.
- No new table and no new column. A new event text in the existing `events` table.

### D9. Pending Suspension in a pure class
New pure class `PendingSuspensions` in `ParentalControl.Domain/Categories` (no WinAPI, xUnit tests). The key is the pid and the start time.
- `Step(due, nowUtc)` gets the `ToSuspend` list of the planner. A process that is new in the list becomes pending with the time `nowUtc`. A process that is pending for at least `Delay` (10 seconds) is due now: `Step` returns it as Ready and removes it. Each pending process that is not in the list any more is dropped: its session is held, its exe has time left again, or it ended.
- `Pending` lists the pending processes for the status response. `NextDueUtc` is the time of the next Ready step.
- The enforcer suspends only the Ready actions. It keeps the state in memory and writes no record for a pending process. A record makes the planner suspend the process at once, and it stops the Category Usage of the exe in `/api/agent/activity`.
- Every suspension goes through this step: a new process, a recorded process that runs again, and a process after the end of a hold. So every bound of the spec is the old bound plus 10 seconds.

**Follow-up tick.** The regular tick runs every 60 seconds. Without more ticks, a Pending Suspension waits until the next regular tick (up to 120 seconds). So the enforcer keeps one timer. After each tick and after each granted hold, it sets the timer to the earliest of `NextDueUtc` and the end of each hold, plus 0.5 seconds. The margin makes sure that the follow-up tick does not come a few milliseconds before the due time. The timer calls `RequestTick()`.

## Risks / Trade-offs

- [The Session Agent does not run, or the child killed it.] → Windows behaves as before this change. The service starts the agent again at the next tick in an unlocked session. If the child kills the agent on purpose, the child causes the loss of work.
- [The order from D2 does not hold.] → The resume still happens within about 2 seconds, and the game answers late. Task 1.1 checks on the VM that the blocking screen then closes without a click.
- [The sign-out takes longer than the hold, for example because Notepad waits for the child to save for more than 60 seconds.] → The service suspends the game again, and the game can block the rest of the sign-out. The child sees the blocking screen in that case only. `HoldDuration` is a constant that is easy to change.
- [The child cancels a sign-out and signs out again within 15 minutes.] → The service refuses the second hold. The local resume still happens, so the game is only blocked if a tick runs during the few seconds of the sign-out.
- [A cancelled sign-out.] → Windows already closed each program that answered before the program that cancelled, also the agent (R2). The service starts the agent again at the next tick in an unlocked session. The hold ends by its timeout, it is unconfirmed, and the parent gets the alert.
- [The Pending Suspension gives the child 10 seconds more per suspension.] → Accepted (decision 1 below). The bound becomes 70 seconds.
- [A fake request] → bounded, see D4.
- [The service restarts during a hold.] → The hold is lost and the next tick suspends the game again. A service restart during a sign-out is rare.
- [Fast Startup: a Windows 10 shutdown ends the user sessions and hibernates session 0, so the service does not stop.] → The session end confirms the hold. After the boot, the planner drops the old records because the processes are gone. Task 3.4 checks this on the VM.
- [A process that the service suspended less than 5 seconds before the sign-out is not in the agent's list.] → The service path (D3, step 2) resumes it.
- [The agent does not run or the child restores the window in the 10 seconds.] → The service suspends the process anyway. The frozen window then stays on the screen, as before this change.

## Migration Plan

- The installer ships the service and the agent together. No data migration.
- An old agent with the new service sends no session-end request: the behavior stays as before. A new agent with an old service gets 404 for the new routes: the local resume still works, without a hold.
- Rollback: install the previous version. Stop the service before, so that it resumes all Suspended Apps.

## Decisions of the parent (review of the proposal)

1. `HoldDuration` = 60 seconds and at most one unconfirmed hold per Restricted User in 15 minutes (`UnconfirmedWindow`): approved. Both are constants.
2. An unconfirmed hold writes an event to the event log and sends a Telegram alert, at most one alert per Restricted User per day (D4).
3. When the child signs out, Windows closes the resumed game like every other program. This is acceptable: it is the action of the child, not of a limit.
4. A repeated resume of a Suspended App with a tool stays out of scope. GitHub issue #24 tracks it.

## Decisions after the VM gate (2026-11-26)

The orchestrator made these decisions under the delegation of the user, after the VM results of group 1.

1. Minimize: two-phase suspension. When a process becomes due for suspension, the service first tells the Session Agent (through the existing agent channel, for example the activity or status response) to minimize the windows of that pid and start time while the process still runs, and the service suspends the process 10 s later. If the agent does not run or does not answer, the service still suspends after the 10 s. The bound in the spec changes from 60 s to 70 s where it applies. Rejected: a short resume-minimize-suspend cycle (race with the enforcer, frozen game without a record), no minimization.
2. Cancel path: remove the `session-end-cancelled` signal. A Session End Hold ends by its timeout (60 s) or by the confirmed logoff event; after the timeout the tick suspends again each process that is still there.

Note: a minimized window is a request to the window; the security rule holds because the suspension never depends on the agent.

Details of the implementation:
- The channel is the status response (`pendingApps`, D6), because only the 5-second status poll reaches the agent within the 10 seconds.
- The two-phase step applies to every suspension (D9). The bounds of the spec change: suspend within 70 seconds, suspend again within 70 seconds, suspend again within 20 seconds after the end of a hold, and a fake request gains at most the hold plus 20 seconds.

## Open Questions

- None. The two decisions above close the questions of group 1.

## VM results of group 1 (2026-11-26)

Test VM 102, Windows 10 22H2 (19045), Fast Startup on. The test program is `scripts/vm/tools/EndSessionSpike`. It runs as `Kid` in the console session. The product service was stopped during the tests. The games are WinForms windows: `SpikeGame` (normal window), `SpikeFull` (borderless full-screen window) and `TestGame`. Each one was suspended with `NtSuspendProcess` as `Kid`. All times are UTC.

### R1. Order of the end-session query (task 1.1): passed

The agent ran with `SetProcessShutdownParameters(0x3FF, SHUTDOWN_NORETRY)` and a hidden `WS_POPUP` window. Three games were suspended, `Other.exe` and Edge ran, and Notepad had unsaved text. `Kid` signed out with `shutdown /l`.

```
14:19:05.418 agent WM_QUERYENDSESSION wParam=0x0 lParam=0x80000000
14:19:05.420 NtResumeProcess status=0x0   (SpikeFull, SpikeGame, TestGame, done at .423)
14:19:05.423 agent WM_QUERYENDSESSION return TRUE
14:19:05.448 agent WM_ENDSESSION wParam=0x1 lParam=0x80000000
14:19:05.470 game (SpikeFull) WM_QUERYENDSESSION ... return 0x1
14:19:05.472 game (SpikeGame) WM_QUERYENDSESSION ... return 0x1
14:19:05.567 game (SpikeGame) WM_ENDSESSION wParam=0x1, exit
14:19:05.584 game (SpikeFull) WM_ENDSESSION wParam=0x1, exit
```

- The hidden window gets the query. The agent gets it about 50 ms before the games.
- The local resume takes 5 ms for three processes.
- Windows then showed "Closing 1 app and signing out" with Notepad only. No game was in the list.
- Not run: a sign-out that completes (without Notepad), and the run without `SetProcessShutdownParameters` or with a late resume. Both need a new `Kid` logon (see "Not run").

### R2. Windows ends each program before it queries the next one: the cancel path of D1 fails

Windows sends `WM_ENDSESSION(TRUE)` to a program right after that program answers the query (see 14:19:05.448 above). It does not wait until all programs answered. So when Notepad blocks the sign-out, Windows already ended every program that it queried before Notepad.

- After the sign-out was cancelled, the session had Notepad and the shell programs only. The three games, `Other.exe`, Edge, OneDrive and the agent itself were closed.
- The agent got no `WM_ENDSESSION(FALSE)`. The cancel request of D4 is thus never sent.
- Nobody clicked on the screen of Notepad. After about 60 s, Windows cancelled the sign-out and locked the session (`SESSIONCHANGE` lock at 14:20:10.598).
- This is the normal sign-out order of Windows and is not caused by the suspension. With this change, the resumed games close like the other programs. This agrees with decision 3 of the parent.

### R3. SW_FORCEMINIMIZE on a suspended window (task 1.2): failed

`ShowWindowAsync(hwnd, SW_FORCEMINIMIZE)` returned TRUE for each window, but no window was minimized:

| Window | After 1.5 s | Screen |
|---|---|---|
| `SpikeFull`, full screen, foreground | `iconic=False` | not changed |
| `SpikeGame`, normal | `iconic=False` | not changed |
| `TestGame`, normal | `iconic=False` | not changed |

- `IsHungAppWindow` stayed FALSE for a suspended window, also after 5 minutes, a mouse click, a key and a posted `WM_NULL`. Windows showed no "Not Responding" ghost window. So the case "a window that Windows shows as Not Responding" does not occur. A second `SW_FORCEMINIMIZE` after a posted `WM_NULL` and 6 s also failed.
- Windows queues the request. The window minimized as soon as the process ran again.
- After Alt+Tab, Windows marked the two minimized normal windows as hung. Alt+Tab did not restore a minimized suspended window, and the desktop stayed usable.

Alternative that worked (`minimize-resume` mode of the test program): resume the process, call `ShowWindowAsync(hwnd, SW_MINIMIZE)`, wait until `IsIconic`, wait a settle time, and suspend again.

| Window | Iconic after | Run time | Result |
|---|---|---|---|
| `SpikeFull`, full screen | 28 ms | 341 ms (settle 300 ms) | minimized, desktop usable |
| `SpikeGame`, normal | 4 ms (*) | 310 ms (settle 300 ms) | minimized |
| `TestGame`, normal | 5 ms (*) | 315 ms (settle 300 ms) | minimized |
| `SpikeFull`, full screen | 4 ms (*) | 4 ms (no settle) | `iconic=True`, but the screen kept the frozen image until the next resume |

After each run, all threads were suspended again (12/12, 13/13, 8/8).

(*) Not a clean measurement. The queued `SW_FORCEMINIMIZE` of the earlier test ran within some milliseconds after the resume, before the `SW_MINIMIZE` call. Only the first row (after a full resume) measures `SW_MINIMIZE` alone. If the parent chooses option 2, group 4 must measure the run time again.

### R4. Service controls during a restart and a shutdown (task 1.3): passed

Test service `EndSessionSpike` (LocalSystem, accepts PRESHUTDOWN, SHUTDOWN and session changes) and the agent in the locked `Kid` session.

```
Restart (shutdown /r /t 0 from SSH):
14:21:18.665 agent WM_QUERYENDSESSION wParam=0x0 lParam=0x40000000
14:21:18.666 agent WM_ENDSESSION wParam=0x1 lParam=0x40000000
14:21:24.310 service SESSIONCHANGE event=6 (logoff) session=1
14:21:53.822 service PRESHUTDOWN
Shutdown with Fast Startup (shutdown /s /hybrid /t 0, no user session):
14:23:14.007 service PRESHUTDOWN
```

- The query comes 35 s before `PRESHUTDOWN`. D1 is correct: the service control is too late.
- For this restart, `lParam` was `ENDSESSION_CRITICAL` (0x40000000), not 0. The handler must treat each query without `ENDSESSION_CLOSEAPP` as an end of the session.
- The shutdown with `/hybrid` also sent `PRESHUTDOWN`. The next boot was a cold boot (Kernel-Boot event 27, boot type 0x0), so the VM did not use Fast Startup, although `HiberbootEnabled` is 1. The test service stopped itself on `PRESHUTDOWN`. So this run does not show what a hybrid shutdown does. The check of Fast Startup (task 3.4) must run on the child's PC.

### Not run

These checks need a new `Kid` logon in the console. The `Kid` session ended with the restart of R4, and the `Kid` password is not available to the agent that ran the tests.

- A sign-out with a suspended game that completes (task 1.1, the main case).
- A sign-out without `SetProcessShutdownParameters`, and with a resume 8 s after the query (task 1.1, the fallback of D2).

### Options for the parent

D7 (minimize):
1. **Two-phase suspension** (the fallback of D7). The service marks a process as pending, the agent minimizes it while it runs, and the service suspends it 10 s later. The bound "suspend within 60 seconds" becomes 70 seconds. Spec change.
2. **Short resume in the agent** (R3). The agent resumes a Suspended App for about 0.3 s, minimizes it, and suspends it again. The 60-second bound stays. The spec must permit that the agent runs a Suspended App for at most 1 s to minimize it. The agent must not resume a process that the service did not suspend.
   - Risk: Windows counts each suspend and each resume. If the service resumes the game (time is available again) during the 0.3 s, the suspend of the agent freezes it with no record, and nothing resumes it. The child can then only close it, which is near a break of the hard rule. If the service suspends the game during the 0.3 s, the count becomes 2, and one later resume does not unfreeze it. So option 2 needs a lock between the agent and the service, for example: the service does the short resume itself on a request of the agent, inside the enforcer lock.
3. **No minimization.** Remove the requirement "Minimize the windows of a Suspended App". The frozen window stays on the screen, as today. Alt+Tab, Start and the other windows work.

Cancel path (R2):
- Remove `POST /api/agent/session-end-cancelled` and the agent report of a cancel. A cancelled sign-out ends the hold by its timeout. The tick at the end of the hold then suspends again each process that still exists. Change the scenario "Sign-out cancelled by the child": Windows closes the resumed game before the cancel, and a process that is still there is suspended again within 10 s after the end of the hold.
- A cancelled sign-out then always gives an unconfirmed hold and the daily alert. This is correct: the sign-out did not complete.

The chosen options are in "Decisions after the VM gate": option 1 for D7, and the removal of the cancel path. With the Pending Suspension, the bound after the end of a hold is 20 s, not 10 s.
