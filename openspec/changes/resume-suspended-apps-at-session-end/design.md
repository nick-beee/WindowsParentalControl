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
- A cancelled sign-out leaves the same state as before the sign-out: the games are suspended again.
- The new unauthenticated request gains the child at most a short, bounded play time.
- The hold logic is pure code with xUnit tests on the Mac.

**Non-Goals:**
- Authentication of the agent channel (issue #6).
- A reliable end-of-session path without the Session Agent.
- Detection of a repeated resume with a tool.

## Decisions

### D1. Detect the end of the session in the Session Agent
The agent creates a hidden top-level window (WPF `HwndSource`, style `WS_POPUP`, never shown) and hooks its window procedure. Windows sends `WM_QUERYENDSESSION` and `WM_ENDSESSION` to every top-level window of the session, also to a hidden one.
- `WM_QUERYENDSESSION` with `lParam` = `0` (shutdown or restart) or with `ENDSESSION_LOGOFF` (0x80000000, sign-out): start the session-end resume (D3). Then return TRUE. The agent never blocks or cancels the end of the session.
- `lParam` with `ENDSESSION_CLOSEAPP` (0x1): the Restart Manager closes only this program, for example the installer during an upgrade. The session does not end, so the agent does nothing. The service stops during an upgrade and resumes all processes anyway.
- `WM_ENDSESSION` with `wParam` = FALSE: the end of the session was cancelled. The agent sends `POST /api/agent/session-end-cancelled` (D4).
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
- `Request(sessionId, userId, now, trusted)` returns Granted or Refused. A hold lasts `HoldDuration` (default 60 seconds).
- A request that is not `trusted` (from the agent) is refused if the user has an *unconfirmed* hold in the last `UnconfirmedWindow` (default 15 minutes). A hold is unconfirmed if it ended by a cancel or by its timeout while the session still existed. A hold that ended with the end of the session is confirmed and does not count. So "sign out, sign in, sign out again" works.
- `Cancel(sessionId, now)` ends the hold as unconfirmed. `SessionEnded(sessionId)` ends it as confirmed. `IsHeld(sessionId, now)` tells the planner.
- The hold state is in memory in `CategoryEnforcer`. No new table and no new column.

`SuspensionPlanner` gets a new input `HeldSessions`. It does not suspend a process in a held session, new or recorded. A recorded process in a held session keeps its record. A resume because time is available again still happens.

The end of a hold:
- Timeout: the enforcer schedules `RequestTick()` at the end of the hold. If the session still exists, the tick suspends again each process whose exe has no time left. A Telegram alert names the user ("a sign-out did not complete"), at most one per hold.
- Cancel: `POST /api/agent/session-end-cancelled` ends the hold and calls `RequestTick()` at once.
- Session end: `SessionTracker.OnUserLogoff` and `SynchronizeSessions` (a stale session) raise a new event `SessionEnded(sessionId)`. The enforcer ends the hold as confirmed.

**Trade-off.** The session-ending request is unauthenticated. The child can send it with `curl`, or can send a fake `WM_QUERYENDSESSION` to the agent window. A granted fake request gains the child at most `HoldDuration` plus one requested tick (about 70 seconds) per 15 minutes, about 8% of the time. A refused request gains nothing through the service. Through the agent, it gains at most the existing 60 seconds until the tick suspends again. This equals what a tool already gives the child today (the spec accepts it, "Keep a suspended process suspended"). Each request, refusal and unconfirmed hold goes to the log and to the event table, and an unconfirmed hold alerts the parent. The cancel request can only make enforcement stricter, so the service accepts it without a limit. Only the request that makes enforcement looser has a limit.
- *Alternative:* verify the end of the session in the service. Rejected: no service-side signal comes before Windows queries the programs (D1). The service can only confirm afterwards (`SessionLogoff`). The rate limit uses exactly this confirmation.
- *Alternative:* re-suspend a recorded process only after the service saw it run for 60 seconds, for every process (no new request). Rejected: it doubles the time that a tool resume gains (up to 120 seconds) all day, and it does not stop a new suspension during a sign-out.
- *Alternative:* check the caller of the request (the owner pid of the TCP connection with `GetExtendedTcpTable`, then the exe path and the session of that pid). Not in this change: a fake `WM_QUERYENDSESSION` to the real agent passes this check, so it adds little. Issue #6 adds a protected channel.

### D5. Forced Logoff resumes first
`POST /api/admin/force-logoff` (admin auth) calls `CategoryEnforcer.ResumeSessionForEnd(sessionId, trusted: true)` before `SessionManager.ForceLogoff` (`WTSLogoffSession`). The call starts a trusted hold without the rate limit. The logon denial in `SessionTracker.OnUserLogon` also calls `ForceLogoff`, but a new session has no Suspended App, so it needs no change.

### D6. The status poll carries the Suspended Apps
`GET /api/agent/status` adds `suspendedApps: [{ pid, startTimeUtc }]` for the user (from `SuspensionRepository.GetForUser`). The agent keeps the last list for D3 and D7. The `suspendedPids` of the activity response stay for the audio mute. The list only names processes of the child, so it gives the child no new information.

### D7. Minimize the windows of a Suspended App in the agent
On each status poll (5 seconds), for each Suspended App of the list in the agent's session, the agent finds the top-level windows of the pid (`EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`). For each visible window that is not minimized, it calls `ShowWindowAsync(hwnd, SW_FORCEMINIMIZE)`. Windows documents `SW_FORCEMINIMIZE` for a window whose thread does not respond, and it is meant for calls from another thread. So it can work after the suspension.
- *Decision gate:* task 1.2 checks on the VM that `SW_FORCEMINIMIZE` minimizes the window of a suspended process, also a borderless full-screen game and a window that Windows shows as "Not Responding". If it does not work, the change uses a two-phase suspension: the service marks the process as pending in the status response, the agent minimizes it with `ShowWindowAsync(hwnd, SW_MINIMIZE)` while it still runs, and the service suspends it 10 seconds later. That changes the 60-second bound of the suspension to 70 seconds, so it needs `/opsx:update` of the spec before the implementation.
- *Alternative:* the service minimizes the windows. Rejected: the service runs in session 0, and a window of the child's session is not reachable from session 0 (session isolation).
- *Alternative:* the service starts a helper process in the child's session (`CreateProcessAsUserW`) for each suspension. Rejected: the agent already runs there.

### D8. New WinAPI and no new storage
- New P/Invokes: `SetProcessShutdownParameters` (kernel32), `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`, `ShowWindowAsync` (user32). Messages: `WM_QUERYENDSESSION` (0x11), `WM_ENDSESSION` (0x16), with `ENDSESSION_CLOSEAPP` and `ENDSESSION_LOGOFF`.
- Reused from Core: `OpenProcess`, `GetProcessTimes`, `NtResumeProcess` (in `ProcessControl.Resume`), `WTSLogoffSession`.
- No new table and no new column. A new event text in the existing `events` table.

## Risks / Trade-offs

- [The Session Agent does not run, or the child killed it.] → Windows behaves as before this change. The service starts the agent again at the next tick in an unlocked session. If the child kills the agent on purpose, the child causes the loss of work.
- [The order from D2 does not hold.] → The resume still happens within about 2 seconds, and the game answers late. Task 1.1 checks on the VM that the blocking screen then closes without a click.
- [The sign-out takes longer than the hold, for example because Notepad waits for the child to save for more than 60 seconds.] → The service suspends the game again, and the game can block the rest of the sign-out. The child sees the blocking screen in that case only. `HoldDuration` is a constant that is easy to change.
- [The child cancels a sign-out and signs out again within 15 minutes.] → The service refuses the second hold. The local resume still happens, so the game is only blocked if a tick runs during the few seconds of the sign-out.
- [A fake request] → bounded, see D4.
- [The service restarts during a hold.] → The hold is lost and the next tick suspends the game again. A service restart during a sign-out is rare.
- [Fast Startup: a Windows 10 shutdown ends the user sessions and hibernates session 0, so the service does not stop.] → The session end confirms the hold. After the boot, the planner drops the old records because the processes are gone. Task 3.4 checks this on the VM.
- [A process that the service suspended less than 5 seconds before the sign-out is not in the agent's list.] → The service path (D3, step 2) resumes it.
- [The child restores a minimized frozen window from the taskbar.] → The next poll minimizes it again within 5 seconds.

## Migration Plan

- The installer ships the service and the agent together. No data migration.
- An old agent with the new service sends no session-end request: the behavior stays as before. A new agent with an old service gets 404 for the new routes: the local resume still works, without a hold.
- Rollback: install the previous version. Stop the service before, so that it resumes all Suspended Apps.

## Open Questions

- The default values `HoldDuration` = 60 seconds and `UnconfirmedWindow` = 15 minutes. They are constants; the parent can choose other values without a change of the approach.
- Whether the parent wants the Telegram alert for an unconfirmed hold (D4), or only the event log.
