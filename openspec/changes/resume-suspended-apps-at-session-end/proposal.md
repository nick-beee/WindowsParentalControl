# Proposal

## Why

A Suspended App blocks the end of a session (GitHub issue #4). A process that the service suspended with `NtSuspendProcess` cannot answer the end-session query of Windows. Windows then shows "This app is preventing you from signing out" (or "shutting down"). Before that screen appears, Windows already closed the other programs of the child. If nobody clicks, Windows cancels the sign-out or the shutdown, and the unsaved work of the child is lost. A limit thus causes Windows to close programs of the child, and this breaks the hard rule. Also, the window of a suspended process stays on the screen: it ignores the mouse and the child cannot minimize it.

## What Changes

- The Session Agent asks Windows to send it the end-session query before the other programs of the child (`SetProcessShutdownParameters`).
- When the agent gets the end-session query for a sign-out, a restart or a shutdown, it resumes the Suspended Apps of its session. Then each program answers the query as if no limit applied. The agent also tells the service that the session ends.
- The service resumes the Suspended Apps of that session from its records. Then it starts a **Session End Hold**: for a short time (default 60 seconds) it does not suspend a process in that session. Without the hold, the next 60-second tick can suspend the game again before Windows closes it.
- The agent request has no authentication (issue #6), so the child can also send it. The service therefore limits the hold: at most one hold per Restricted User in 15 minutes that does not end with a sign-out. A hold ends early if the agent reports that the sign-out was cancelled. After the hold, the service suspends each process again whose exe still has no time left.
- If the sign-out is cancelled (the child clicks Cancel because another program asks to save), the session stays consistent: the agent reports the cancel, the hold ends, and the service suspends the games again within 10 seconds.
- A hold that does not end with a sign-out (an unconfirmed hold) writes an event to the event log. It also sends a Telegram alert to the parent, at most one alert per Restricted User per day.
- Before the service logs off a session itself (Forced Logoff from the admin page), it resumes the Suspended Apps of that session and starts a hold without the rate limit.
- The Session Agent minimizes the windows of each Suspended App of its session, so that a frozen window does not cover the screen.
- The agent gets the list of the Suspended Apps (process ID and start time) every 5 seconds from the status poll, not only every 20 seconds from the activity response.

## Out of scope

- Authentication of the agent channel (issue #6, stage 3). This change limits what an unauthenticated request can gain. It does not add authentication.
- A sign-out or shutdown when the Session Agent does not run, or when the agent cannot resume a process (for example, anti-cheat software blocks the handle). Windows then behaves as before this change.
- A program that the child closes on purpose. When the child signs out, Windows closes the resumed game like every other program. That is the action of the child, not of a limit. The parent accepted this.
- Detection of a repeated resume of a Suspended App with a tool, and escalation to the Enforced Workstation Lock. That is a separate hardening change (GitHub issue #24).
- Window minimization for a Suspended App in another session (fast user switch to another account).
- A change of how the Allowance Time, the Schedule Window or the Enforced Workstation Lock works.

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `category-limits`: a Suspended App is resumed when its session ends (sign-out, restart, shutdown, Forced Logoff); a bounded Session End Hold stops new suspensions during the end of the session; "Keep a suspended process suspended" gets the hold as an exception; the windows of a Suspended App are minimized.

## Impact

- `src/ParentalControl.Agent/Program.cs`, `AgentController.cs`: shutdown level at start; a hidden top-level window that handles `WM_QUERYENDSESSION` and `WM_ENDSESSION`.
- New agent class for the session-end resume and for the window minimization. It reuses `ProcessControl.Resume` from Core.
- `src/ParentalControl.Service/Web/WebServerHost.cs`: new routes `POST /api/agent/session-ending` and `POST /api/agent/session-end-cancelled`; `GET /api/agent/status` adds the list of Suspended Apps; `POST /api/admin/force-logoff` resumes first.
- `src/ParentalControl.Service/CategoryEnforcer.cs`: resume of a session, the hold, and the end of a hold.
- `src/ParentalControl.Service/ParentalControlServiceLifetime.cs` / `SessionTracker.cs`: a sign-out (`SessionLogoff`) ends the hold of that session.
- `src/ParentalControl.Domain/Categories/`: a new pure class for the hold and its rate limit; `SuspensionPlanner` skips the processes of a held session. New xUnit tests.
- `src/ParentalControl.Core/Platform/NativeMethods.cs` or the agent: `SetProcessShutdownParameters`, `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`, `ShowWindowAsync`.
- No new table and no new column. The hold state is in memory.
- `GLOSSARY.md`: new term Session End Hold; the term Suspended App also names the resume at the end of a session.
