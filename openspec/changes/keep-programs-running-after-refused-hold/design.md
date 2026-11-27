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
- A request with `curl` alone gains no play time. A chain of fake requests cannot keep a game running.
- The new logic is pure code with xUnit tests on the Mac.

**Non-Goals:**
- Authentication of the agent channel (#6).
- Protection without the Session Agent, and protection of a process that the agent cannot open.
- A change of the hold, the 15-minute limit or the alert.

## Decisions

### D1. A refused request starts a Session End Guard
On a refused request, the enforcer starts a guard of 60 s (`SessionEndGuards.Duration`, the same as `SessionEndHolds.HoldDuration`) for the session. During the guard, the planner does not suspend again a recorded process of the session that the guard covers. A request while a guard of the session runs does not extend it and adds no process.

The guard covers only a record that the service suspended itself and did not resume since (an "eligible" record). The service does not resume anything on a refusal, so a covered process runs only because another party resumed it: the agent at the query, or a tool. A covered record stops being eligible: it can only become eligible again when the service suspends it again. So:
- `curl` alone gains nothing: the request resumes nothing, and a suspended process stays suspended.
- After a granted hold, the service resumed the records itself, so a guard does not cover them, and they are suspended 20 s after the hold as before.
- At the end of a guard, the follow-up tick makes each covered process a Pending Suspension. A new request in these 10 s starts a new guard, but this guard cannot cover the process (not eligible), so the suspension happens. This closes the chain of fake requests.
- After a service restart no record is eligible until the service suspends it again. The service resumes all records at its stop, so this is correct.
- A record also becomes eligible when a tick sees its process suspended (all threads) while no guard covers it. This covers a process that a guard covered while the agent could not resume it: it stayed suspended, so a later real sign-out must protect it again. To use this, the child needs a new resume outside the service, so it does not open the chain.

A guard does not resume a process, does not drop the Pending Suspension of a process that it does not cover, and does not stop a new suspension. It does not count against the 15-minute limit and gives no event or alert of its own. The refusal keeps its `SECURITY_ALERT` event and log line. The start of a guard writes one log line with the covered pids.

The guard ends when the session ends (`SessionTracker.SessionEnded`, the same event that confirms a hold) or when its time is over. The enforcer includes the end of each guard in the follow-up timer, so a covered process that still runs is suspended again within about 20 s after the guard (0.5 s margin, a Pending Suspension of 10 s, and the next follow-up tick).

### D2. Where the logic lives
- New pure class `SessionEndGuards` in `ParentalControl.Domain/Categories` (no WinAPI, xUnit tests). The key of a process is the pid and the start time, as for the records.
  - `MarkSuspended(key)`: the service suspended the process (`CategoryEnforcer.Suspend`, result Ok), or a tick saw the recorded process suspended outside a guard. The record becomes eligible.
  - `MarkResumed(key)`: the service resumed the process (`ResumeRecord`, also at the service stop, and the resume of `ResumeSessionForEnd` for a granted or already held hold).
  - `Start(sessionId, sessionKeys, nowUtc)`: starts a guard that covers the eligible keys of the session, and makes them not eligible. Returns null if a guard of the session runs.
  - `Covered(nowUtc)`: the keys of the active guards. `NextEndUtc`, `Expire(nowUtc)`, `SessionEnded(sessionId)`, `Retain(liveKeys)` (forget a key whose record is dropped or deleted).
- `SuspensionPlanner`: new input `GuardedProcesses` (a set of pid and start time). A recorded process in this set is not suspended again; its record stays. A process that is not recorded is not affected.
- `CategoryEnforcer`: keeps the state under its lock, like `SessionEndHolds`. Every call of `ProcessControl.Suspend` and `ProcessControl.Resume` in the service is in this class (checked with grep), so the bookkeeping covers each site. The resume of the Session Agent is in another process and is not marked: that is the point of the guard.

### D3. Trade-off: what a fake request gains
- `curl` alone (no tool): nothing. The guard covers only a process that runs because of a resume outside the service.
- With a tool, or a fake `WM_QUERYENDSESSION` to the agent window (also a tool): a resumed process runs at most the guard plus about 20 s, so about 80 s for each resume. Today, without the guard, it runs at most 70 s for each resume (D4 of `resume-suspended-apps-at-session-end`). A guard needs a new resume after a real suspension each time. So the gain stays in the class of issue #24 (a repeated resume with a tool), which the spec accepts.
- The 15-minute limit of the granted hold, and its gain of about 80 s in 15 minutes with `curl`, do not change.

### D4. Alternatives
- *A refused request blocks all suspensions of the session for a short time (the first option of #47).* Rejected: a fake request every N seconds keeps a running game and its Pending Suspension alive with `curl` alone, so the gain has no bound. A limit of one such window in 15 minutes brings the bug back at a third sign-out.
- *The service also resumes the records on a refused request.* Rejected: `curl` alone could then resume every Suspended App every minute. It would help only a process that the agent cannot open.
- *Confirm the real sign-out on the service side (WTS session events, `SERVICE_CONTROL_PRESHUTDOWN`, `WTS_SESSION_LOGOFF`), so that a cancelled sign-out does not use the budget (the second option of #47).* Rejected: Windows sends no session event at the start of a sign-out or at its cancel. `PRESHUTDOWN` does not come for a sign-out and comes about 35 s after the query for a restart (R4 of the earlier change). The only trace of a cancelled sign-out that the service sees is that Windows ended the agent, and the child can end the agent too. A refund based on that trace can be forged, so it breaks the bound.
- *A longer hold or more holds in 15 minutes.* Rejected: it raises the `curl` gain and only moves the bug to a later sign-out.

### D5. No new storage and no new WinAPI
No new table, column, route or WinAPI call. The guard state is in memory: a service restart forgets it, which is safe because no record is eligible after a restart.

## Risks / Trade-offs

- [A process becomes due for its first suspension during the sign-out.] → The guard does not stop a new suspension, so it is suspended after its Pending Suspension. Only in a refused case and only for an exe whose time runs out in these seconds. Accepted.
- [The Session Agent does not run or did not poll yet.] → No local resume and no request, as before this change.
- [The agent cannot open a process (for example, a sandboxed Chromium process).] → It stays suspended at a refused sign-out. The guard covers only processes that run. Task 4.4 checks Chrome on the VM.
- [The blocking screen of Windows stays longer than about 70 s on another computer.] → The covered process is suspended again before the screen ends, as after a hold. Both durations are constants.
- [A thread that a process creates after its suspension makes the process run again.] → The planner suspends such a process "again" one time (seen in #33). If a guard runs at this moment and the record is eligible, the guard covers it once. The child cannot cause this.

## Migration Plan

No data migration. The installer ships the service. Rollback: install the previous version.

## Open Questions

None.

## Decisions under the delegation of the user

Decided by the orchestrator under the user's delegation (2026-11-27):
1. The guard of D1 (refused request → Session End Guard of 60 s that covers only records that the service suspended and someone else resumed, at most once for each suspension) instead of a general no-suspend window or a service-side confirmation.
2. No resume by the service on a refused request.
3. The proposal is approved without a separate review stop, and the implementation follows in the same PR.
