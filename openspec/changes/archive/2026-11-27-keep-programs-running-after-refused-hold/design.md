# Design

## Context

See proposal.md for the motivation and specs/ for the required behavior. The base is the change `resume-suspended-apps-at-session-end` (archived, design D1 to D9): the Session Agent gets `WM_QUERYENDSESSION` first, resumes the Suspended Apps of its last status poll, and sends `POST /api/agent/session-ending`. The service grants a Session End Hold (60 s) at most one time per Restricted User in 15 minutes if the hold does not end with the session (`SessionEndHolds`). On a refusal, `CategoryEnforcer.ResumeSessionForEnd` returns before its own resume, and the planner has no exception for the session.

### Reproduction on VM 102 (2026-11-27, build of `main` at `32e4509`)

Kid signed in on the console. A copy of Notepad in `C:\Games\Pad\` with unsaved text, blocked by a folder rule after its start, so the service suspended it. Sign-out with `shutdown /l` as Kid. Nobody clicked on the screen of Windows.

```
10:02:27.098 Suspended C:\Games\Pad\notepad.exe (pid 9628, "Unknown", "Blocked") of Kid
10:02:37.263 Session end hold started for Kid (session 1, agent logoff, trusted false) for 60 s
10:02:37.266 Resumed C:\Games\Pad\notepad.exe (pid 9628, "Unknown") for the session end: "Ok"
10:03:37.760 Session end hold of Kid (session 1) ended without the end of the session; alert true
10:03:37.787 Pending suspension of C:\Games\Pad\notepad.exe (pid 9628, "Unknown") again: suspend in 10 s
10:03:43.167 Session 1 (Kid) locked            <- Windows cancelled the sign-out by itself, 66 s after the query
10:03:48.265 Suspended ... notepad.exe ... again
--- unlock at 10:09:47, second sign-out ---
10:10:16.652 Session end hold refused for Kid (session 1, agent logoff): an unconfirmed hold in the last 15 min
10:10:16.821 Pending suspension of C:\Games\Pad\notepad.exe (pid 9628, "Unknown") again: suspend in 10 s
10:10:27.310 Suspended ... notepad.exe ... again   <- 11 s into the sign-out, the screen "Closing 1 app" still shows
10:11:21.770 Session 1 (Kid) locked            <- Windows cancelled the sign-out, 65 s after the query
```

Findings:
- **Root cause.** The refusal does not stop the planner. The agent resumed Notepad at the query (it ran again, so the planner made it a Pending Suspension "again" 0.2 s after the refusal), and the service suspended it 10.5 s later, while Windows still waited for Notepad. A frozen program cannot answer Windows or show its save dialog. The only ways out are "Sign out anyway" (Windows ends the program unsaved) or Cancel. The same applies to each program that Windows queries after a blocking program, for example a resumed game or Chrome: if a tick suspends it before Windows queries it, Windows lists it as "preventing you from signing out" and ends it at "Sign out anyway".
- The agent itself still ran and resumed. The local resume is not the problem.
- The blocking screen of Windows stays for 65 to 66 seconds and then cancels by itself. A hold (60 s) plus the Pending Suspension (10 s) covers about 70 s, so the margin is about 5 s. The guard uses the same 60 s and keeps this margin.
- The service starts the agent again during the sign-out (`EnsureAgentRunning` of the usage tick at 10:10:16.814 and 10:11:16.836), because Windows ended the old agent at its query and the session is not locked. This is harmless and does not change.

## Goals / Non-Goals

**Goals:**
- At a second sign-out within 15 minutes, the service does not suspend again a program that the Session Agent resumed for the sign-out, for as long as a hold would protect it.
- A request with `curl` alone gains no play time. A chain of fake requests without a tool cannot keep a game running.
- A real sign-out and Cancel, repeated, gives at most about 70 s of play time for each cycle, and the parent learns about it (D6).
- The new logic is pure code with xUnit tests on the Mac.

**Non-Goals:**
- Authentication of the agent channel (#6).
- Protection without the Session Agent, and protection of a process that the agent cannot open.
- A change of the hold, the 15-minute limit or the alert of an unconfirmed hold.

## Decisions

### D1. A refused request starts a Session End Guard
On a refused request, the enforcer starts a guard of 60 s (`SessionEndGuards.Duration`, the same as `SessionEndHolds.HoldDuration`) for the session. During the guard, the planner does not suspend again a recorded process of the session that the guard covers. A request while a guard of the session runs does not extend it and adds no process. A request without an eligible record starts no guard (review of PR #49, finding 1): an empty guard covers nothing, but it would block a real guard of the same session for 60 s.

The guard covers only an eligible record: a record that the service suspended, or saw suspended while no guard covered it, and did not resume itself since. The spec uses the same definition in "Session End Guard" and "Limit the Session End Guard". The service does not resume anything on a refusal, so a covered process runs only because another party resumed it: the agent at the query, or a tool. A covered record stops being eligible: it can only become eligible again when the service suspends it again. So:
- `curl` alone gains nothing: the request resumes nothing, and a suspended process stays suspended.
- After a granted hold, the service resumed the records itself, so a guard does not cover them, and they are suspended 20 s after the hold as before.
- At the end of a guard, the follow-up tick makes each covered process a Pending Suspension. A new request in these 10 s finds no eligible process, so no guard starts and the suspension happens. This closes the chain of fake requests without a tool (D3).
- After a service restart no record is eligible until the service suspends it again. The service resumes all records at its stop, so this is correct.
- A record also becomes eligible when a tick sees its process suspended (all threads) while no guard covers it. This covers a process that a guard covered while the agent could not resume it: it stayed suspended, so a later real sign-out must protect it again. To use this, the child needs a new resume outside the service, so it does not open the chain.

A guard does not resume a process, does not drop the Pending Suspension of a process that it does not cover, and does not stop a new suspension. It does not count against the 15-minute limit of holds. A first guard gives no event or alert of its own; a repeated guard (D6) does. The refusal keeps its `SECURITY_ALERT` event and log line. The start of a guard writes one log line with the covered pids.

The guard ends when the session ends (`SessionTracker.SessionEnded`, the same event that confirms a hold) or when its time is over. The enforcer includes the end of each guard in the follow-up timer, so a covered process that still runs is suspended again within about 20 s after the guard (0.5 s margin, a Pending Suspension of 10 s, and the next follow-up tick).

### D2. Where the logic lives
- New pure class `SessionEndGuards` in `ParentalControl.Domain/Categories` (no WinAPI, xUnit tests). The key of a process is the pid and the start time, as for the records.
  - `MarkSuspended(key)`: the service suspended the process (`CategoryEnforcer.Suspend`, result Ok). The record becomes eligible.
  - `Observe(recordKeys, suspendedKeys, nowUtc)`: a tick saw recorded processes suspended (all threads). Each one that no active guard covers becomes eligible. This is the observe step of the tick; it is in the Domain so that the xUnit tests use the same code as the enforcer (finding 4).
  - `MarkResumed(key)`: the service resumed the process (`ResumeRecord`, also at the service stop, and the resume of `ResumeSessionForEnd` for a granted or already held hold).
  - `Start(sessionId, userId, sessionKeys, nowUtc)`: starts a guard that covers the eligible keys of the session, and makes them not eligible. The result is `Started` (with the guard), `AlreadyRunning` or `NothingToCover`; only `Started` records a guard.
  - `Expire(nowUtc, sessionExists)`: ends each guard whose time is over. A guard whose session still exists is unconfirmed and counts for D6. A guard that starts after an unconfirmed guard of the user in 15 minutes has `Repeated` true.
  - `Covered(nowUtc)`: the keys of the active guards. `NextEndUtc`, `SessionEnded(sessionId)`, `Retain(liveKeys)` (forget a key whose record is dropped or deleted).
- `SuspensionPlanner`: new input `GuardedProcesses` (a set of pid and start time). A recorded process in this set is not suspended again; its record stays. A process that is not recorded is not affected.
- `CategoryEnforcer`: keeps the state under its lock, like `SessionEndHolds`. Every call of `ProcessControl.Suspend` and `ProcessControl.Resume` in the service is in this class (checked with grep), so the bookkeeping covers each site. The resume of the Session Agent is in another process and is not marked: that is the point of the guard.

### D3. Trade-off: what a request gains
- `curl` alone (no tool): nothing. The guard covers only a process that runs because of a resume outside the service.
- A chain of requests without a tool: nothing more. A request in the Pending Suspension after a guard finds no eligible record, so no guard starts. With a tool the child can suspend the process again during that Pending Suspension; the next tick sees it suspended and makes it eligible, so a request and a resume with a tool give about 60 s more each time. This is in the class of issue #24: a resume with a tool already gives play time without a request.
- With a tool, or a fake `WM_QUERYENDSESSION` to the agent window (also a tool): a resumed process runs at most the guard plus about 20 s, so about 80 s for each resume, as before.
- **A real sign-out and Cancel need no tool** (review of PR #49, finding 3). The Session Agent resumes the Suspended Apps at each real `WM_QUERYENDSESSION`, whatever the service answers, and the service makes a re-suspended process eligible again. Without a bound, the child keeps a program with unsaved text open as the blocker and repeats: sign out, Cancel, play about 70 s, the service suspends the game again. `main` already allows this cycle, with about 10 to 70 s for each cycle. On VM 102 a manual Cancel (2026-11-27) does **not** lock the session: the desktop comes back at once, so a cycle costs no password. Only the automatic cancel of Windows after 65 s locks the session. D6 reports the cycle to the parent but does not stop it.
- The 15-minute limit of the granted hold, and its gain of about 80 s in 15 minutes with `curl`, do not change.

### D4. Alternatives
- *A refused request blocks all suspensions of the session for a short time (the first option of #47).* Rejected: a fake request every N seconds keeps a running game and its Pending Suspension alive with `curl` alone, so the gain has no bound. A limit of one such window in 15 minutes brings the bug back at a third sign-out.
- *The service also resumes the records on a refused request.* Rejected: `curl` alone could then resume every Suspended App every minute. It would help only a process that the agent cannot open.
- *Confirm the real sign-out on the service side (WTS session events, `SERVICE_CONTROL_PRESHUTDOWN`, `WTS_SESSION_LOGOFF`), so that a cancelled sign-out does not use the budget (the second option of #47).* Rejected: Windows sends no session event at the start of a sign-out or at its cancel. `PRESHUTDOWN` does not come for a sign-out and comes about 35 s after the query for a restart (R4 of the earlier change). The only trace of a cancelled sign-out that the service sees is that Windows ended the agent, and the child can end the agent too. A refund based on that trace can be forged, so it breaks the bound.
- *A longer hold or more holds in 15 minutes.* Rejected: it raises the `curl` gain and only moves the bug to a later sign-out.

### D5. No new storage and no new WinAPI
No new table, column, route or WinAPI call. The guard state is in memory: a service restart forgets it, which is safe because no record is eligible after a restart.

### D6. A repeated guard alerts the parent
A guard that ends by its time while its session still exists is unconfirmed: the sign-out did not complete. A guard that starts after an unconfirmed guard of the user in the last 15 minutes (`SessionEndGuards.RepeatWindow`, the same as the hold limit) is repeated:
- It covers the same processes as a first guard, so #47 stays closed at every sign-out.
- The enforcer writes a `SECURITY_ALERT` event and sends one Telegram alert per user and day, like the alert of an unconfirmed hold.
- A guard that ends with its session (a completed sign-out) is confirmed and does not count, and a request without an eligible record starts no guard. So a normal sign-out never gives an alert.

What this bounds: each cycle of sign-out and Cancel gives at most about 70 s (the guard and the Pending Suspension), and each cycle needs a real sign-out and a program that blocks it. The number of cycles in 15 minutes is **not** bounded; the parent sees the alert and the events. The full fix is the authentication of the agent channel (#6), together with a way to tell a cancelled sign-out from one in progress. The xUnit test `Sign_out_and_cancel_cycle_gives_at_most_about_70_seconds_per_cycle_and_is_reported` pins this.

Rejected options:
- *The Enforced Workstation Lock at the start of a repeated guard (tested on VM 102, 2026-11-27).* The guard covered the suspended Pad Notepad (pid 6020, unsaved text), and the enforcer disconnected the session 0.3 s after the query (11:46:55.97). Notepad ran (0 of 4 threads suspended) but had not shown its save dialog yet. Windows ended it about 10 s later, and the session logged off at 11:47:06.99: the unsaved text was lost because of the lock. In a first experiment, a disconnect 1.5 s after the query, when the save dialog of a normal Notepad was already open, cancelled the sign-out and kept the text; a sign-out without a blocking program completed 0.6 s after the disconnect. So a disconnect early in a sign-out lets Windows end programs. This breaks the hard rule, so the option is rejected.
- *The lock at the end of a repeated guard, or a few seconds after the query.* At the end, the child already played for 60 s, and a new sign-out that started just before can get the lock in its first second (the agent often does not run then, so no request marks it). A delay "until the blocking screen shows" depends on the timing of the PC.
- *A cap on guards (no guard after N in 15 minutes).* It brings #47 back at the next sign-out.

## Risks / Trade-offs

- [A new sign-out while a guard runs (review of PR #49, finding 2).] → The request does not extend the guard. Example: second sign-out at t0, Cancel at +5 s, third sign-out at +20 s. The guard ends at +60 s and the service suspends the game again at about +71 s, while the third blocking screen of Windows can stay until about +85 s. The game is frozen during the end of that screen, as on `main`. An extension for the covered processes would let a chain of `curl` requests keep a game running without a bound, so the gap stays. The xUnit test `Gap_sign_out_during_a_running_guard_is_not_extended` pins it.
- [A sign-out in the re-suspension window (finding 2).] → After a guard or a hold ends and before the service suspends the process again (about 11 s), the record is not eligible, so a refused request starts no guard and the suspension goes ahead during the sign-out. To make the record eligible at the end of the guard would open the chain of `curl` requests again. The xUnit test `Gap_sign_out_in_the_re_suspension_window_gets_no_guard` pins it. So the guard protects a later sign-out only if it starts after the service suspended the process again.
- [Sign-out and Cancel again and again (finding 3).] → About 70 s of play time for each cycle, without a limit on the number of cycles. The parent gets an alert (D6). Accepted until #6.
- [A lock (`LockSession`) in the first second of any sign-out.] → The VM run of D6 shows that a disconnect early in a sign-out lets Windows end a program with unsaved data. This change does not add such a lock. Other locks (the start of a Break, the suspend fallback, the limit lock) can in theory come at the same moment; this risk is older than this change.

- [A process becomes due for its first suspension during the sign-out.] → The guard does not stop a new suspension, so it is suspended after its Pending Suspension. Only in a refused case and only for an exe whose time runs out in these seconds. Accepted.
- [The Session Agent does not run or did not poll yet.] → No local resume and no request, as before this change.
- [The agent cannot open a process (for example, a sandboxed Chromium process).] → It stays suspended at a refused sign-out. The guard covers only processes that run. On VM 102 the agent resumed all 8 recorded Chrome processes with "Ok" (task 4.4), so this did not occur for Chrome.
- [The blocking screen of Windows stays longer than about 70 s on another computer.] → The covered process is suspended again before the screen ends, as after a hold. Both durations are constants. The margin is about 5 s on VM 102: the screen stays 65 to 66 s after the query, and the cover lasts about 70.5 s. A slow tick only moves the suspension later. But the 65 s of Windows can start later than the query of the agent if the programs that Windows queries first are slow, and a forward step of the wall clock shortens a guard as it shortens a hold. **Measure the margin in the final test on the child's PC.**
- [A thread that a process creates after its suspension makes the process run again.] → The planner suspends such a process "again" one time (seen in #33). If a guard runs at this moment and the record is eligible, the guard covers it once. The child cannot cause this.

## VM results (2026-11-27)

The details and log lines are in tasks.md, group 4. In short: at a second sign-out within 15 minutes, the guard covered Notepad and Chrome and the service did not suspend them for 60 s (on `main` it suspended Notepad after 11 s). Chrome closed itself, and a sign-out with only a suspended TestGame completed in 5.4 s under a guard. A fake request while Notepad was suspended left it suspended. The granted single sign-out did not change. Windows gives each program that it ends after `WM_ENDSESSION` the exit status 0x40010004 (also the Session Agent), so the exit status cannot tell a frozen program from one that answered; the time to the end of the session and the blocking screen can.

Review of PR #49 (tasks.md 5.7): three sign-outs within 15 minutes, each cancelled by hand, with the suspended Pad Notepad. The hold, the first guard and the repeated guard each kept Notepad running (0 suspended threads) for 60 s, and the service suspended it again 71 s after each query. The repeated guard wrote its alert. A manual Cancel does not lock the session. The unsaved text was still there after the three cycles. A lock at the start of the repeated guard lost the text (D6).

## Migration Plan

No data migration. The installer ships the service. Rollback: install the previous version.

## Open Questions

None.

## Decisions under the delegation of the user

Decided by the orchestrator under the user's delegation (2026-11-27):
1. The guard of D1 (refused request → Session End Guard of 60 s that covers only records that the service suspended and someone else resumed, at most once for each suspension) instead of a general no-suspend window or a service-side confirmation.
2. No resume by the service on a refused request.
3. The proposal is approved without a separate review stop, and the implementation follows in the same PR.
4. Review of PR #49 (2026-11-27): a repeated guard alerts the parent (D6). The lock that the orchestrator suggested first was tested on VM 102 and rejected because Windows ended the unsaved Notepad. A cap was rejected because it brings #47 back. A request without an eligible record starts no guard. The two gaps of finding 2 stay and are documented (Risks).
