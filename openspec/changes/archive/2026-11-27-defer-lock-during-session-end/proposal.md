# Proposal

## Why

Issue #50: in the VM checks of PR #49, the service called `LockSession` 0.3 s after a sign-out started. The lock came before Notepad showed its save dialog. Windows then ended Notepad, and the session signed out 11 s later. The unsaved text was lost.

Each path that applies the Enforced Workstation Lock can hit this if it runs in the first seconds of a sign-out, a restart or a shutdown: the end of the Allowance Time, the end of the Schedule Window, a Break start, the lock again during a Break, the unlock check, the clock check, the fallback of a suspension and the lock loop of the Storage Lockdown. The behaviour is older than #49. A lock of a limit, a Break or a Schedule Window end must never cause a program of the child to close (the hard rule).

## What Changes

- New concept **Lock Deferral**: when the Session Agent reports that the session ends (`WM_QUERYENDSESSION`), the service does not apply the Enforced Workstation Lock to that session for 75 seconds. Windows waits about 65 seconds for a program that blocks a sign-out and then cancels the sign-out and locks the session itself (VM 102, PR #49).
- The service records the deferral for each session-end request of a Restricted User, also when it refuses the Session End Hold, and for each Forced Logoff.
- A lock that becomes due during the deferral is owed. It does not run, and the service sends no lock message for it. At the end of the deferral, if the session still exists, the service checks the limits, the Breaks and the suspensions again at once and locks the session if a lock is still due.
- The check is directly before the call that locks the session, so a lock whose message started before the sign-out is deferred too.
- If the unlock check (or a sign-in during a Break) finds a deferral, the service tracks the session as unlocked. Its time then counts as used time, and the tick at the end of the deferral locks it.
- The delayed lock of the parent from the admin page (a countdown of 5 to 60 seconds) also waits for the end of a deferral. The child can start a sign-out during the countdown to save the work.
- The time of a deferral is monotonic (`Environment.TickCount64`), so a change of the system clock does not make it longer.
- The request has no authentication, so the deferral has a bound: a request during a deferral does not extend it, and after a deferral that delayed a lock and ended while the session still exists, the service grants no deferral to that Restricted User for 15 minutes. A deferral that ended with the session, or that delayed no lock, does not count.
- New glossary term: Lock Deferral.

Out of scope:
- The lock of the parent from Telegram. It locks at once, without a countdown, and stays as it is.
- The lock of the Data Folder Lockdown. In the lockdown no Restricted User is known, so no deferral can start.
- Authentication of the agent channel (#6).
- A sign-out without a running Session Agent. Then the service gets no signal, as before.
- A change of the Session End Hold or the Session End Guard.

## Capabilities

### New Capabilities

- `session-lock`: the Lock Deferral of the Enforced Workstation Lock while a session ends, and its bound.

### Modified Capabilities

- `usage-breaks`: the requirements "Start a Break with the Enforced Workstation Lock" and "Keep the session locked during a Break" get a reference to the Lock Deferral. Their timings stay when no deferral is active.
- `category-limits`: the requirement "Fallback when a process cannot be suspended" gets the same reference.
- `storage-failure`: the requirement "Storage Lockdown" gets the same reference. Its lock loop locks through the same locker (design D10).

## Impact

- `src/ParentalControl.Domain/Sessions`: new pure class `LockDeferrals` with xUnit tests.
- `src/ParentalControl.Service`: new class `SessionLocker` (the single call site of `SessionManager.LockSession` for the lock paths of the limits, the Breaks and the suspensions). `UsageMonitorWorker` (also the lock loop of the Storage Lockdown), `BreakEnforcer`, `SessionTracker` and `CategoryEnforcer` call it. `WebServerHost` reports the session-end request and the Forced Logoff to it, and its delayed lock of the parent locks through it. `UsageMonitorWorker` runs a tick at the end of a deferral that owes a lock.
- No new API route, no new table, no new column, no new WinAPI call. The Session Agent does not change.
