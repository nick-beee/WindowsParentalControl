# Tasks

All VM checks run on test VM 102 as `Kid` in the Proxmox console (noVNC or SPICE), not over RDP. Before you install the service, check that the snapshot `test-base` exists (`docs/vm.md`). Use a test exe in Games with a Games limit of 2 minutes, as in `add-app-categories` task 6.2.

## 1. VM spike: Windows behaviour that the design assumes

- [ ] 1.1 Build a small test program for the VM (outside the product, in `scripts/vm/`): it calls `SetProcessShutdownParameters(0x3FF, SHUTDOWN_NORETRY)`, logs each `WM_QUERYENDSESSION` with time and `lParam`, and resumes a given suspended pid in the handler. Verify on the VM: with a suspended test game, a sign-out logs the query of the test program before the game is queried, and Windows does not show "This app is preventing you from signing out". Also run it without `SetProcessShutdownParameters` and record whether the blocking screen closes without a click after the late resume. Record both results in the PR.
  - Result 2026-11-26 (design.md, R1 and R2): the order holds and no game blocked the sign-out. Windows ends each program right after its query, so the agent gets no `WM_ENDSESSION(FALSE)` after a cancel. Not run: a completed sign-out and the fallback without the shutdown level (needs a `Kid` logon).
- [ ] 1.2 With the same program, call `ShowWindowAsync(hwnd, SW_FORCEMINIMIZE)` on the windows of a suspended process: a normal window, a borderless full-screen window, and a window that Windows shows as "Not Responding". Verify on the VM: each window minimizes and the desktop is usable. If one does not minimize, stop and run `/opsx:update` for the two-phase suspension of design D7 before group 4.
  - Result 2026-11-26 (design.md, R3): failed. No window minimized. A short resume with `SW_MINIMIZE` worked. The parent must choose an option before group 2.
- [x] 1.3 Log the time of `SERVICE_CONTROL_PRESHUTDOWN` in a test service build and the time of the `WM_QUERYENDSESSION` from 1.1 during a shutdown. Verify on the VM: the query comes first, as design D1 assumes. Record the result in the PR.
  - Result 2026-11-26 (design.md, R4): passed. The query came 35 s before `PRESHUTDOWN`.

## 2. Session End Hold (pure logic)

- [ ] 2.1 Implement `SessionEndHolds` in `src/ParentalControl.Domain/Categories` (Request with `trusted`, Cancel, SessionEnded, IsHeld, expiry, unconfirmed holds per user in `UnconfirmedWindow`); verify xUnit tests in `tests/ParentalControl.Domain.Tests` pass with `dotnet test tests/ParentalControl.Domain.Tests` for: hold granted, hold expires after 60 s, second untrusted request within 15 min after an unconfirmed hold refused, request after a confirmed hold granted, request after 15 min granted, trusted request always granted, cancel ends the hold as unconfirmed, the first unconfirmed hold of a day asks for an alert and a second one on the same day does not, and an unconfirmed hold on the next day asks for an alert again.
- [ ] 2.2 Add `HeldSessions` to `PlannerInput` and skip each process of a held session in `SuspensionPlanner` (new and recorded; the record stays; a resume because time is back still happens); verify new xUnit tests for the spec scenarios "Tick during the sign-out", "Limit ends during a sign-out" and "Recorded game during a hold" pass, and the existing planner tests still pass.

## 3. Service

- [ ] 3.1 Add `suspendedApps` (pid, start time UTC) to `GET /api/agent/status` for a Restricted User; verify on the VM with a suspended test game that `curl "http://127.0.0.1:5050/api/agent/status?user=Kid"` lists its pid and start time.
- [ ] 3.2 Add `CategoryEnforcer.ResumeSessionForEnd(sessionId, trusted)` (hold policy, then resume every record of the session, records stay) and the route `POST /api/agent/session-ending` (session id must belong to a Restricted User; log and event for each request and refusal); verify on the VM with `curl` from the Kid session: the first request resumes the suspended game, the game is suspended again about 60 to 70 seconds later, and a second request 5 minutes later is refused (log line) and the game stays suspended.
- [ ] 3.3 Add the route `POST /api/agent/session-end-cancelled` (ends the hold, `RequestTick()`), the tick at the end of a hold, and for an unconfirmed hold an event in the `events` table and a Telegram alert (at most one per Restricted User per day, from `SessionEndHolds`; text escaped as in `SendParentAlert`); verify on the VM: after a session-ending request, a cancel request makes the game suspended again within 10 seconds, the service log shows the event and the alert, and a second unconfirmed hold on the same day shows the event but no second alert.
- [ ] 3.4 Raise `SessionEnded(sessionId)` from `SessionTracker.OnUserLogoff` and from the stale-session path of `SynchronizeSessions`, and end the hold as confirmed in the enforcer; verify by an xUnit test of the confirmed path (2.1) and on the VM: after a sign-out with a hold, the log shows the confirmed hold, and after a shutdown with Fast Startup and a new sign-in, `suspended_processes` has no record of the old session after the first tick.
- [ ] 3.5 In `POST /api/admin/force-logoff`, call `ResumeSessionForEnd(sessionId, trusted: true)` before `SessionManager.ForceLogoff`; verify on the VM: with a suspended test game, Forced Logoff from the admin page logs off the Kid session without the blocking screen.

## 4. Session Agent

- [ ] 4.1 Call `SetProcessShutdownParameters(0x3FF, SHUTDOWN_NORETRY)` at the start of `Program.Main` and log a failure; verify on the VM that the agent log shows no error at start.
- [ ] 4.2 Add the hidden top-level window (`HwndSource`, `WS_POPUP`) with a hook for `WM_QUERYENDSESSION` and `WM_ENDSESSION`: ignore `ENDSESSION_CLOSEAPP`; on a sign-out, restart or shutdown resume the Suspended Apps of the last status poll with `ProcessControl.Resume`, then send `POST /api/agent/session-ending` synchronously with a 2-second timeout, then return TRUE; on `WM_ENDSESSION` with FALSE send `POST /api/agent/session-end-cancelled`; verify on the VM: Kid with a suspended test game and an unsaved Notepad signs out, Notepad asks to save, the game is not listed as blocking, and the sign-out completes after "Don't save".
- [ ] 4.3 Keep the `suspendedApps` list from the status poll and minimize the visible top-level windows of each listed pid with `SW_FORCEMINIMIZE` on each poll; verify on the VM: a full-screen test game is minimized within 10 seconds after its suspension, and after a click on its taskbar button it is minimized again within 10 seconds.

## 5. Documentation

- [ ] 5.1 Add the term Session End Hold to `GLOSSARY.md`, and add the resume at the end of a session to the term Suspended App; verify both terms match their use in the spec delta.

## 6. End-to-end check on the VM

- [ ] 6.1 Sign-out: Kid has a suspended test game and a running `Other.exe`; sign out in the Proxmox console. Expected: no "This app is preventing you from signing out", the sign-out completes, and the next sign-in has no hold and no stale record.
- [ ] 6.2 Restart and shutdown: repeat 6.1 with Restart and with Shut down (Fast Startup on, the Windows 10 default). Expected: no blocking screen, the computer restarts or turns off, and after the next start the service log shows no error about old records.
- [ ] 6.3 Cancelled sign-out: Kid has a suspended test game and an unsaved Notepad; sign out and click Cancel in the save dialog of Notepad. Expected: the session stays open, Notepad keeps the text, and the game is suspended again within 10 seconds after the cancel (service log).
- [ ] 6.4 Agent upgrade: install the same build again with the installer while the Kid session has a suspended game. Expected: the agent ignores `ENDSESSION_CLOSEAPP`, the service stop resumes the game, and the new service suspends it again within 60 seconds after the agent reports it as the Foreground Window.
- [ ] 6.5 Run `scripts/vm-test.sh` and `dotnet test tests/ParentalControl.Domain.Tests`; verify `ALL ASSERTIONS PASSED`, all xUnit tests pass, and `openspec validate resume-suspended-apps-at-session-end --strict` passes. Then roll the VM back to `test-base`.

## Workflow follow-up

- `/opsx:verify` with the VM results of group 6.
- `/opsx:archive` after the PR is merged.
- The final check of the sign-out on the child's PC.
