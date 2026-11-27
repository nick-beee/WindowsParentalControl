# Design

## Context

The Enforced Workstation Lock is `SessionManager.LockSession` (`WTSDisconnectSession`). In the VM checks of PR #49 (issue #50), a lock 0.3 s after the start of a sign-out came before Notepad showed its save dialog. Windows ended Notepad and signed out 11 s later. A lock that runs after the save dialog shows is not harmful: in VM run 4.1 of the archived change `keep-programs-running-after-refused-hold`, Windows cancelled a blocked sign-out by itself 65 s after the query and locked the session, and Notepad kept its text.

Signals of a session end that the service has today:

| Signal | When | Use |
|---|---|---|
| `POST /api/agent/session-ending` from the Session Agent | At `WM_QUERYENDSESSION` of the agent. The agent asks Windows for the first query (`SetProcessShutdownParameters`) and posts before it answers, so the request comes before the query of the other programs. | Yes. No authentication (#6). |
| Session End Hold or Session End Guard | Only after this request. A refused request without an eligible process starts neither. | No (D1). |
| Forced Logoff of the parent (`/api/admin/force-logoff`) | Before `WTSLogoffSession`. | Yes, trusted. |
| WTS `SessionLogoff` | After the programs of the session ended. | Too late; it confirms the end. |
| `SERVICE_CONTROL_PRESHUTDOWN` | Not handled; only for a shutdown, never for a sign-out. | No. |
| `WM_ENDSESSION` with FALSE (cancel) in the agent | The agent can be gone already, and it has no route to the service. | No (D7). |

The lock paths of the service (each one calls `SessionManager.LockSession` today):

| Path | Code | Retry after a deferral |
|---|---|---|
| End of the Allowance Time | `UsageMonitorWorker.ProcessTick` | The next tick checks it again. |
| End of the Schedule Window | `UsageMonitorWorker.ProcessTick` | The next tick checks it again. |
| Break start | `BreakEnforcer.StartBreakInSessions` | The Break state is stored. The session stays unlocked in the tracker, so the next tick returns `LockAgain`. |
| Lock again during a Break | `BreakEnforcer.LockForRunningBreak` (from `LockAgain`, the unlock check and a sign-in) | `LockAgain` of the next tick, or the unlock check again. |
| Unlock check (limit, Schedule Window, Break) | `SessionTracker.OnSessionUnlock` | The tracker marks the session as unlocked (D4), so the tick paths above apply to it. |
| Clock check | `SessionTracker.TickAllSessions` | The minutes are added. The session stays tracked, so the Allowance Time path of the next tick locks it. |
| Fallback of a suspension | `CategoryEnforcer.Fallback` | The record of a new suspension is deleted before the fallback, so the woken tick starts a new Pending Suspension (10 s). The follow-up tick tries the suspension again and runs the fallback again: the lock comes about 11 s after the end of the deferral, so the spec says 20 s. |
| Lock loop of the Storage Lockdown | `UsageMonitorWorker.StartLockLoop` (`DataFolderLockdown.LockSessions` with the locker) | The loop runs every 2 s, so it locks the session at its next run after the end of the deferral, within 3 s (D10; VM 4.11: 2.08 s). |
| Data Folder Lockdown | `DataFolderLockdown.LockSessions` from `StartupWorker` | Out of scope: no Restricted User is known, so no deferral exists. |
| Delayed lock of the parent | `/api/admin/lock-session` (countdown of 5 to 60 s) | `SessionLocker.LockForParentAsync` waits until 0.5 s after the end of the deferral and tries again (D8). |
| Lock of the parent from Telegram | `TelegramBotService.OnLockSessionRequested` | Out of scope: it locks at once, without a countdown. |

## Goals / Non-Goals

**Goals:**
- No lock path of a limit, a Break, a Schedule Window end or a suspension fallback locks a session in the first 75 s after the Session Agent reported its end.
- A lock that is still due after the deferral runs within 10 s (20 s for the fallback of a suspension).
- A fake request gains at most one delay of about 85 s in 15 minutes for each Restricted User.
- The decision is pure code with xUnit tests on the Mac.

**Non-Goals:**
- Authentication of the agent channel (#6).
- A sign-out without a running Session Agent.
- The lock of the parent from Telegram and the lock of the Data Folder Lockdown.
- A change of the Session End Hold or the Session End Guard.

## Decisions

### D1. The signal: each session-end request, not the hold

The service starts a Lock Deferral for each session-end request of a session of a Restricted User, before it asks for a Session End Hold. It also starts one before a Forced Logoff (trusted). The deferral has its own state and its own limit.

The hold or the guard as the signal was the first idea, but it leaves a gap. The child cancels a sign-out without a Suspended App. Five minutes later the child signs out again, and a Break starts in that first second. The hold is refused (an unconfirmed hold in 15 minutes), and no guard starts (no eligible process). Without its own deferral, the lock runs and #50 comes back. A guard as the signal also has no bound: D3 of the archived change shows that a real sign-out and Cancel start a guard each time.

The orchestrator decided this under the user's delegation.

### D2. The length: 75 seconds

Windows waits about 65 s for a program that blocks a sign-out. Then it cancels the sign-out and locks the session itself (VM run 4.1, 2026-11-27). A deferral of 75 s ends after that, so an owed lock never runs while the blocking screen of Windows shows. VM check 4.6 (2026-11-27) measured 65.1 s from the log line `Lock Deferral started` to the lock of Windows, with Notepad alone, with 2 Notepads and with 2 Notepads and WordPad. The 10 s margin covers a slower PC. Probe 4.9 showed that a parent lock during the blocking screen did not lose the text on the VM, so a lock that lands late is not known to be harmful. The check on the child's PC must confirm the time.

A shorter deferral (30 s) would cover the first seconds, but it would lock while the blocking screen shows. A longer one gives a fake request more time. The orchestrator decided 75 s under the user's delegation.

### D3. The bound

- A request while a deferral of the session runs does not extend it and does not start a second one.
- A deferral is unconfirmed if it ends by its time while the session still exists and it owed a lock. After an unconfirmed deferral of the user, the service refuses each new deferral of that user for 15 minutes from its start. A Forced Logoff is never refused.
- A deferral that ended with the session, or that owed no lock, does not count. So a real sign-out and Cancel while no lock is due costs nothing, and a later real sign-out at the limit is still protected.
- A request while no lock is due gains nothing: there is nothing to delay.

So a fake request delays a due lock by at most 75 s, plus the time of the woken tick and its message (D5), so about 85 s. It works at most once in 15 minutes for each Restricted User: at most about 6 minutes in an hour if the child sends it each 15 minutes at a limit. This is in addition to the Session End Hold, which lets the Suspended Apps run for about 80 s in 15 minutes (category-limits). The orchestrator decided this bound under the user's delegation.

### D4. The check at the moment of the lock

A new service class `SessionLocker` is the only call site of `SessionManager.LockSession` for the paths in the table. Its `Lock(sessionId, reason)` checks the deferral and calls `SessionManager.LockSession` in its own lock, so a session-end request cannot come between the check and the call. It returns false for a deferred lock. The paths that send a message first also call `ShouldDefer(sessionId, reason)` before the message, so a deferred lock shows no message and writes no event. Both calls mark the deferral as owing a lock and log it.

A lock that the deferral stops does not call `SessionTracker.OnSessionLock`: the session is not marked as locked, no Rest starts, and the time counts as used time.

The unlock check and a sign-in during a Break start from a session that the tracker shows as locked. If they find a deferral, Windows shows the session as unlocked, so the tracker marks it as unlocked (`IsLocked = false`; the time counts from now). It writes no event, ends no Rest and raises no `SessionUnlocked`, because the lock is only delayed. The ticks then count the time as used time, and the tick at the end of the deferral locks the session through the paths in the table. If the tracker kept the session as locked, the ticks would skip it, and only the lock flag of Windows (`WTSSessionInfoEx`) could start the unlock check again. A Rest that ran before the unlock does not end, and the owed lock starts a new Rest: the child loses the Rest before the unlock. That is stricter, not weaker, and a fake request gains nothing from it.

`CategoryEnforcer.Fallback` checks the deferral before it writes the `SECURITY_ALERT` event and sends the alert to the parent. Both say that the session is locked, and the daily marker of the alert would hide the alert of the real lock later.

`SessionLocker` has its own lock and never calls into `CategoryEnforcer`, because `CategoryEnforcer.Fallback` runs inside the lock of the enforcer. A session-end request takes the lock of the locker before `ResumeSessionForEnd`, so the deferral starts before the slow part.

### D5. The owed lock runs through a woken tick

At the end of an unconfirmed deferral, `SessionLocker` raises `OwedLockDue`. `UsageMonitorWorker` then runs a tick at once instead of after its 60 s wait. The tick checks the Allowance Time, the Schedule Window, the Breaks and the suspensions again with the current state, and each path locks the session if a lock is still due (the table in Context). A timer of the locker fires 0.5 s after the end of the earliest deferral.

A stored lock that runs at the end was the other option. It would lock after Bonus Minutes or after the parent ended the Break. The tick uses the current state. An extra tick is safe: the minutes count from the last tick time, and the alerts and warnings use their own sent markers.

### D6. Where the logic lives

- New pure class `LockDeferrals` in `ParentalControl.Domain/Sessions` (no WinAPI, xUnit tests). It is not thread-safe; `SessionLocker` holds a lock.
  - `Request(sessionId, userId, now, trusted)`: `Granted`, `AlreadyDeferred` or `Refused`.
  - `TryDefer(sessionId, now, out ends)`: true during an active deferral of the session; marks it as owing a lock.
  - `SessionEnded(sessionId)`, `NextEnd`, `Expire(now, sessionExists)` (returns the ended deferrals with `Owed` and `Confirmed`).
  - `Duration` = 75 s, `UnconfirmedWindow` = 15 min.
- New service class `SessionLocker`, a singleton. `SessionTracker` gives it the test for an existing session and the event of an ended session.
- Dependency injection gives the same `SessionLocker` to `SessionTracker`, `BreakEnforcer`, `CategoryEnforcer`, `UsageMonitorWorker` and `WebServerHost`. The constructors that the Windows test program uses (`SessionTracker(logger)`, `BreakEnforcer(logger)`, the old `CategoryEnforcer` and `WebServerHost` signatures) stay and make a locker of their own.
- `WebServerHost`: the session-end route and the Forced Logoff route call `SessionLocker.OnSessionEnding` before `CategoryEnforcer.ResumeSessionForEnd`.
- WinAPI: no new call. `WTSDisconnectSession` (through `SessionManager.LockSession`) stays the lock. No new table or column.

### D7. Alternatives

- *Defer until the cancel of the sign-out (`WM_ENDSESSION` with FALSE).* Rejected for now: the agent can be gone, and the message comes from an unauthenticated channel. It could only make a deferral shorter, so it is a possible later step.
- *Wait for `SessionLogoff`.* Too late: it comes after the programs ended.
- *Lock only after the save dialog shows.* The service cannot see the dialog of another program.
- *Lock with a delay of a few seconds on every path.* It does not know about the sign-out, and a sign-out can start during the delay.

### D8. The delayed lock of the parent

The admin page locks the session after a countdown of 5 to 60 s and shows the countdown to the child. A child who reads it can start a sign-out to save the work, and then the lock comes in the first seconds of that sign-out. The task of the countdown can try again, so it locks through `SessionLocker.LockForParentAsync`: during a deferral it waits until 0.5 s after the end and tries again, at most 3 times, and it stops if the session does not exist any more. The parent is trusted, but a deferred parent lock owes a lock like each other lock. So the deferral counts against the bound of D3: a script cannot delay the parent lock again with a new request, because the next untrusted request is refused. The lock from Telegram has no countdown and stays as it is.

### D9. Monotonic time

The deferral, its timer and the 15-minute window use `Environment.TickCount64`, given to `LockDeferrals` as a `DateTime` (from year 1), not `DateTime.UtcNow`. With the wall clock, a clock set back by X during a deferral keeps it active for X + 75 s, and the timer goes to the far end again and again: each lock path, also the lock of the clock check, would wait until the session ends. `SessionLocker` takes the clock as a parameter, so a test gives its own clock. The state is in memory only: a restart of the service ends each deferral and resets the 15-minute window. A Restricted User cannot restart the service.

### D10. The Storage Lockdown and the control clock (rebase on #40)

Decided by the orchestrator under the user's delegation, at the rebase of PR #51 on `main` with PR #40 (`harden-clock-and-disk`).

- **The lock loop of the Storage Lockdown locks through `SessionLocker`.** The loop applies the Enforced Workstation Lock every 2 s to each active session of a user who is not an administrator, while the service cannot write its database. It is an enforcement lock like the limit locks: the child's programs must stay open ("each program of the child stays open" in `storage-failure`). A lock in the first seconds of a sign-out would end Notepad, so the loop calls `SessionLocker.Lock` for each session. A session without a deferral locks as before. A deferred session locks at the next run of the loop after the end of the deferral. The loop sleeps 2 s after each run, so the lock comes a little more than 2 s later at most (VM check 4.11: 2.08 s); the spec says 3 s. The bound of D3 applies. The deferral is in memory, so it needs no write to the database. `DataFolderLockdown.LockSessions` gets an overload with the lock function. `StartupWorker` keeps the direct lock: before the start gate, no session-end route runs and no Restricted User is known.
- **The order "lock before write" of #40 stays.** In each lock path the deferral check comes first, then the message, then `SessionLocker.Lock`, then the writes. A deferred lock writes no event (also not `LOGIN_DENIED` of the unlock check, `LIMIT_REACHED` or the `SECURITY_ALERT` of the fallback). The clock check calls the locker before it adds the Allowance Time to the ledger and writes `CLOCK_TAMPER`. It adds the minutes also for a deferred lock, so the tick at the end of the deferral locks the session.
- **Time.** The deferral keeps its own monotonic clock (D9). The tracked state uses the fields of #40: a deferred unlock check sets `IsLocked = false` and, for a session that was tracked as locked, `Stamped()` (`LastTickUtc` and `LastTickMono`), so the ticks count the time from the unlock. The day of the checks comes from the control clock of #40, as in the other paths.

## Risks / Trade-offs

- **The first milliseconds.** A lock that runs between the start of the sign-out and the arrival of the request is not deferred. The agent asks for the first query, so this window is the time of one local HTTP request. The lock of `CategoryEnforcer.Fallback` runs in the lock of the enforcer, and the route waits for that lock only after it started the deferral.
- **No Session Agent.** Without the agent, there is no signal, as before.
- **A second cancelled sign-out within 15 minutes.** After a deferral that delayed a lock and ended without the end of the session, a new sign-out of the same child in 15 minutes gets no deferral. The lock that came at the end of the first deferral makes a second lock in that time less likely: the session is locked.
- **The 65 s of Windows.** VM 102 measured 65.1 s in each run of check 4.6, also with 3 blocking programs. If a PC waits longer than 75 s, an owed lock runs while the blocking screen shows. Probe 4.9 found no lost text for such a lock on the VM. The check on the child's PC measures the time again.
- **Clock check.** The clock path adds the minutes of the Allowance Time from the limit table, and the next tick compares with the effective limit of the day. If the effective limit is higher, the next tick does not lock. This is the same as after a sign-in today.
- **A slow "Sign out anyway".** `Expire` decides "confirmed" from the session list of the tracker. If the child clicks "Sign out anyway" and `SessionLogoff` comes later than 75.5 s after the request, the deferral counts as unconfirmed: the woken tick can lock a session that already logs off, and the user gets no deferral for 15 minutes. The programs end already at that point, so the lock does no harm.
- **The session is tracked as unlocked during the sign-out.** After a deferred unlock check, the tracker shows the session as unlocked. During the sign-out, the service can then send a message to the session or start a Pending Suspension in it. A new suspension during a sign-out is the accepted risk of the archived change `keep-programs-running-after-refused-hold` (design, Risks).
- **A restart of the service.** It ends each deferral and resets the 15-minute window (D9). A Restricted User cannot restart the service.
- **Fake request.** The child sees the desktop for at most about 85 s after a due lock, once in 15 minutes. The log shows each deferral and each refusal.
