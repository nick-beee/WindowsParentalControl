# Proposal

## Why

The child owns its processes, so it can resume a Suspended App with a tool, for example a PowerShell script that calls `NtResumeProcess` (issue #24). The service suspends the process again within 70 seconds ("Keep a suspended process suspended"), but a script that resumes the game again and again gets most of the play time back. Nothing stops the pattern, and the parent does not learn about it.

Decided by the orchestrator under the user's delegation (2026-11-28).

## What Changes

- The service counts a **Child Resume**: a check in which it finds that a process that it suspended runs again, while the reason of the suspension (no time left, or a block) still applies. The service does not count its own resumes (the end of a session) or the resumes of the Session Agent under a Session End Guard. One check counts at most one Child Resume per Restricted User, also if the tool resumed ten processes at once, and a check less than 30 seconds after the last counted Child Resume does not count.
- After 3 Child Resumes of one Restricted User in 10 minutes, the service starts a **Resume Lock**: the Enforced Workstation Lock of each session of that user. The count is per user, not per exe, so a rotation of exes does not help.
- The Resume Lock locks the session again after each unlock and each sign-in. It ends at the next day of the Control Time Zone, when the parent gives Bonus Minutes, or when no exe that the child resumed has a reason to stay suspended any more (the parent raised the limit or removed the block). A Lock Deferral delays it, as each other lock.
- At the start of a Resume Lock, the service writes a `SECURITY_ALERT` event and sends the parent one alert per day through the Alert Channel. The child gets a fixed session message before each lock.
- The Resume Lock never closes, kills or suspends a program. The resumed programs are suspended again as today.
- New term in `GLOSSARY.md`: Resume Lock (with Child Resume).

Out of scope:

- A faster re-check (for example every 10 seconds) of a resumed process. The 60-second tick stays (design D7).
- A stored count or a stored Resume Lock. A restart of the service or of Windows clears both (design D6).
- A new event type, a new setting for the threshold, a new editable child message, and a parent button that ends the Resume Lock (these touch the files of PR #43; Bonus Minutes end it).
- Detection of other tools of the child, for example a debugger that attaches to a game.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `category-limits`: a new requirement "Lock the session after repeated resumes" (the Resume Lock); "Session limits stay unchanged" names the Resume Lock as a second exception to "Category Limits do not lock the session".
- `session-lock`: "Owed lock at the end of the deferral" and "Lock timings with a Lock Deferral" include the Resume Lock.

## Impact

- `src/ParentalControl.Domain/Categories/ResumeLocks.cs` (new, pure): the count, the threshold, the window and the end conditions. xUnit tests in `tests/ParentalControl.Domain.Tests`.
- `src/ParentalControl.Service/CategoryEnforcer.cs`: feeds the tracker in each tick, marks its own resumes, applies the lock through `SessionLocker`, writes the event and sends the alert.
- `src/ParentalControl.Domain/Messages/SessionTexts.cs`: two fixed child texts (de, en, id).
- `GLOSSARY.md`: one new term. PR #43 also changes this file; the new term is in a part that PR #43 does not change.
- No new table, column, setting, route or WinAPI call.
