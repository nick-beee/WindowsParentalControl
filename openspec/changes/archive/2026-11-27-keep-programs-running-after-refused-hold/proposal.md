# Proposal

## Why

Issue #47: after a sign-out that did not complete, the service refuses the next Session End Hold for 15 minutes (the rate limit against fake agent requests). If the child signs out again in this time, the Session Agent still resumes the Suspended Apps, but nothing stops the next tick. On VM 102 the service suspended Notepad again 11 seconds into the second sign-out, while Windows still waited for Notepad. A frozen program cannot answer Windows, so the child cannot save its work, and "Sign out anyway" ends it unsaved. The suspension of the service causes this loss, so it breaks the hard rule.

## What Changes

- A refused session-end request starts a **Session End Guard** (60 seconds) for the session. During the guard the service does not suspend again a recorded process that the service suspended and that someone else resumed since then (the Session Agent at the end-session query, or a tool).
- The guard does not resume a process, does not stop a new suspension, and does not cover a process that the service resumed itself. A process gets at most one guard for each suspension by the service. So a request from `curl` alone gains no play time, and a chain of fake requests cannot keep a game running.
- When the guard ends and the session still exists, the service suspends each covered process again within 20 seconds, like after a hold.
- A guard does not count against the limit of one unconfirmed hold in 15 minutes and does not alert the parent. The refusal still writes its event.
- New glossary term: Session End Guard.

Out of scope:
- Authentication of the agent channel (#6).
- A protected end-of-session path without the Session Agent. If the agent does not run, the second sign-out has no protection, as before.
- A process that the Session Agent cannot open (for example, a sandboxed process). On a refused request only the local resume of the agent runs; the service resumes nothing.
- A repeated resume with a tool (#24).
- A change of the hold duration, the 15-minute limit or the daily alert.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `category-limits`: new requirements "Session End Guard" and "Limit the Session End Guard"; "Keep a suspended process suspended", "Limit the Session End Hold from the agent", "Keep a blocked suspension" and "Minimize a process before its suspension" name the guard.

## Impact

- `src/ParentalControl.Domain/Categories`: new pure class `SessionEndGuards`; `SuspensionPlanner` gets the covered processes as a new input.
- `src/ParentalControl.Service/CategoryEnforcer.cs`: starts a guard on a refused request, tracks which records the service suspended, schedules a follow-up tick at the end of a guard, and ends the guards of an ended session.
- xUnit tests in `tests/ParentalControl.Domain.Tests`.
- No new API, no new table, no new column, no new WinAPI call. The Session Agent does not change.
