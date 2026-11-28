# Design

## Context

See proposal.md for the motivation and the spec delta for the required behaviour.

The current code has these properties:

- `CategoryEnforcer.Tick` runs every 60 seconds (`UsageMonitorWorker`). `RequestTick` runs it earlier: after Bonus Minutes, an admin change, an unlock (`SessionTracker.SessionUnlocked`), and the follow-up timer at the end of a Pending Suspension, a Session End Hold or a Session End Guard.
- `ProcessControl.ListProcesses` gives `SessionProcess.IsSuspended`. `ThreadSuspension.AllSuspended` decides it from the thread states.
- `SuspensionPlanner.Plan` gives a `SuspendAction` with `IsRecorded: true` for a recorded process that runs again while its exe still has no time left or is still a Blocked Program. It gives no such action during a Session End Hold of the session or for a process that a Session End Guard covers. This is the existing detection of "the child resumed it". The action goes through a new Pending Suspension (10 seconds), and then `Suspend` calls `NtSuspendProcess` again.
- `ResumeSessionForEnd` resumes the records of a session for its end and keeps the records. When an unconfirmed hold ends, the planner gives `IsRecorded` actions for these processes. After a refused hold, the Session Agent resumes the processes and a guard covers them; when the guard ends, the planner gives `IsRecorded` actions for them too.
- `SessionLocker.Lock` applies the Enforced Workstation Lock (`SessionManager.LockSession`, which calls `WTSDisconnectSession`) unless a Lock Deferral stops it. A deferred lock is owed. At the end of the deferral, `OwedLockDue` wakes `UsageMonitorWorker`, and its tick calls `CategoryEnforcer.Tick`.
- The suspension fallback (`CategoryEnforcer.Fallback`) is the only lock of the enforcer today. It locks inside the gate of the enforcer, writes a `SECURITY_ALERT` event, and alerts the parent one time per exe and day (key in `_sentToday`).

Each decision below is decided by the orchestrator under the user's delegation (2026-11-28).

## Goals / Non-Goals

**Goals:**
- A resume script of the child ends in a locked session after a few minutes, and the parent learns about it.
- No false lock because of a sign-out, a cancelled sign-out, a Forced Logoff, or a resume of the service itself.
- No change of the files of PR #43, except one new term in `GLOSSARY.md`.

**Non-Goals:**
- No stop of the resume itself. A process of the child can always call `NtResumeProcess` on its own processes.
- No close or kill of a program (the hard rule).

## Decisions

### D1. A pure `ResumeLocks` class decides
`ParentalControl.Domain.Categories.ResumeLocks` holds the whole state in memory and has no WinAPI call. The enforcer calls it in its gate. It has three parts:

1. **Watched processes.** A map from (pid, start time) of the recorded processes that the service saw suspended to their CPU time at that time (D10). `MarkSuspended` adds a process or updates its CPU time. `MarkResumedByService` removes it. `Retain` removes the processes without a record.
2. **Strikes.** For each user, the monotonic times of the counted Child Resumes in the last 10 minutes, each with the exe paths of that check.
3. **Locks.** For each user with a Resume Lock: the day of the Control Time Zone at its start, the Bonus Minutes at its start, and the exe paths of its strikes.

`Check(resumed, now, day, bonus)` takes the `IsRecorded` actions of the plan, each with its CPU time of this tick. It counts a Child Resume for a user if at least one of its processes is watched and shows evidence of a run (D10). It removes each such process from the watched set, so that the same resume does not count again while its Pending Suspension runs. A watched process without evidence stays watched with its old CPU time until the next `MarkSuspended`. It returns the counted users, the new locks, and the processes without evidence (for the log). `Release(day, bonus, stillDue)` returns the locks that end.

Rejected: put the count into `SuspensionPlanner`. The planner is stateless, and the count needs a state across ticks.

### D2. When a process is watched
- `Suspend` gives `Ok` → `MarkSuspended` with the CPU time that `ProcessControl.Suspend` reads on the same handle after `NtSuspendProcess`, also for a recorded process.
- A tick sees a recorded process suspended (`IsSuspended`), its session has no Session End Hold, and no Session End Guard covers it → `MarkSuspended` with the CPU time of that tick. This covers the first tick after a service start.
- `ResumeRecord` (time left again, block removed, service stop) → `MarkResumedByService`.
- `ResumeSessionForEnd` (the end of a session, a Forced Logoff) → `MarkResumedByService` for each record of the session, before the hold check. A refused hold does this too, because the Session Agent resumes the processes. The processes that a Session End Guard covers are records of that session, so the guard needs no step of its own.

So a resume for the end of a session never counts: when the hold or the guard ends, the process is not watched, and the planner suspends it again without a count. The service watches it again after the next `Suspend`.

The mark comes from `Suspend` = `Ok`, not only from a tick that sees the process suspended. A script that calls `NtResumeProcess` in a loop resumes the process a few milliseconds after the suspension, so a tick would never see it suspended, and the script would never count.

Rejected: a count per Pending Suspension that starts "again". A failed suspension (fallback), the end of a hold and the end of a guard also start one.

### D3. One Child Resume per user and check, and 30 seconds between two
A tool resumes all processes of a game, or about 10 processes of a blocked Chrome, in one run. A count per process would lock the session at the first resume. So one check counts at most one Child Resume per user. `RequestTick` can run two checks a few seconds apart (an unlock, the end of a Pending Suspension), so a check less than 30 seconds after the last counted Child Resume of the user does not count. The resumed processes of such a check stay unwatched, and the planner suspends them again.

Rejected: 60 seconds. The tick interval is 60 seconds, so a gap of 60 seconds can skip a regular tick that comes some milliseconds early.

### D4. 3 Child Resumes of a user in 10 minutes
The count is per Restricted User over all exes, so a rotation of exes does not help. The window is 10 minutes of monotonic time (`Environment.TickCount64`, the clock of `SessionLocker`). A script that resumes at once after each suspension counts one Child Resume per 60-second tick, so the lock comes after about 2 to 3 minutes. With the Pending Suspension of 10 seconds per resume, the child gets back less than 3 minutes of play.

The window counts plain time, not unlocked time. While the session is locked, the child cannot start a tool, so the difference does not matter, and plain time needs no lock state.

Rejected: a count per exe (rotation of exes) and a count per day without a window (a rare resume, for example one by a third-party tool, adds up over a day).

### D5. The lock
In each tick, after the plan, the enforcer:

1. calls `Release` with the day, the Bonus Minutes of each user (`UsageRepository.GetUsage(...).BonusMinutes`) and the verdict function of the planner (the exe has no time left, or is a Blocked Program);
2. calls `Check` with the `IsRecorded` actions;
3. writes the event and sends the alert for each new lock;
4. collects each unlocked session (`ActiveSession.IsLocked` is false) of each user with a Resume Lock.

After it leaves its gate, it locks each collected session like `BreakEnforcer.LockForRunningBreak`: `SessionLocker.ShouldDefer`, then the child message (`NotificationManager.SendMessage`, which calls `WTSSendMessageW`, with `wait: true` and 5 seconds, at most one time in 10 seconds per session), then `SessionLocker.Lock` and `SessionTracker.OnSessionLock`. The message waits up to 5 seconds, so it runs outside the gate. Otherwise the gate would stop the hold requests of the Session Agent for 5 seconds.

The timings:
- The third Child Resume: the tick that counts it locks at once. The tick runs at most 60 seconds after the resume, so the lock comes within 70 seconds.
- An unlock: `SessionUnlocked` calls `RequestTick`, so the lock comes within 10 seconds.
- A sign-in: the next tick, within 70 seconds.
- A Lock Deferral: `ShouldDefer` makes the lock owed. `OwedLockDue` runs the tick of the worker at the end of the deferral, and the tick locks if the Resume Lock still runs.

No change of `SessionTracker.cs` (PR #43).

### D6. The end of a Resume Lock, and the state in memory
A Resume Lock ends at the first of these:

- the day of the Control Time Zone is not the day of its start;
- the Bonus Minutes of the user today are more than at its start;
- no exe of its strikes still has a reason to stay suspended (no time left, or a Blocked Program). A raised limit, a new App Rule or a removed block then ends it.

Bonus Minutes also end a lock that a blocked exe caused. The block itself stays: the blocked program stays suspended. This gives the parent one action that always ends the lock (Telegram or the admin page), without a new button in the files of PR #43. When a lock ends, the strikes of the user are cleared, so a new lock needs 3 new Child Resumes. A Child Resume during a lock adds its exes to the lock and starts no second lock.

The state is in memory only, as the Lock Deferral and the Session End Hold. A restart of the service or of Windows clears the strikes and the lock. The child cannot stop the service (it runs as LocalSystem), and a restart of Windows also ends the game. The parent got the alert already.

Rejected: a stored lock. It needs a new table or setting (`SettingsRepository.cs` is in PR #43).

### D7. No faster re-check of a resumed process
The 60-second tick stays. A re-check every 10 seconds after a Child Resume shortens the play time per resume, but it adds a process scan every 10 seconds and changes the timing of "Keep a suspended process suspended". The lock after 3 Child Resumes already limits the gain to less than 3 minutes per day and alert.

### D8. Event, alert and child message
- Event: `SECURITY_ALERT` with the text "Paused programs resumed again and again (<exe names>): session locked (Resume Lock)", one at each start of a Resume Lock. No new `EventType` (`EventType.cs` is in PR #43).
- Alert: "Parental Control: <user> on <machine> resumed paused programs again and again (<exe names>). The session is locked for the rest of the day. Bonus Minutes end the lock." One per user and day, with the key `resumelock:<userId>` in `_sentToday`. `SendParentAlert` escapes the Markdown characters of the exe names (untrusted data).
- Child message: two fixed rows in `SessionTexts` (`resume_lock_title`, `resume_lock_body`) in de, en and id. German: "Du hast pausierte Programme immer wieder fortgesetzt. Darum bleibt der Computer heute gesperrt. Deine Eltern können die Sperre beenden." The parent cannot edit it. An editable text needs `ChildMessageKeys`, `SettingsRepository.cs` and the web admin, which PR #43 changes.

### D9. WinAPI, tables, PR #43
- No new WinAPI call. D10 reads the kernel and user time of `GetProcessTimes`, which `ProcessControl` already calls for the start time. The change uses `WTSDisconnectSession` (`SessionManager.LockSession`), `WTSSendMessageW` (`NotificationManager.SendMessage`) and the existing `NtSuspendProcess` and `NtResumeProcess` of `ProcessControl`.
- No new table, column or setting.
- Files of PR #43: only `GLOSSARY.md` gets one new term (Resume Lock), after "Blocked Start", in a part that PR #43 does not change. The term "Lock Deferral" in the glossary stays as it is; the spec delta of `session-lock` names the Resume Lock.

### D10. A Child Resume needs evidence: the CPU time grew
Decided by the orchestrator under the user's delegation (2026-11-28).

The thread check (`ProcessControl.AreAllThreadsSuspended`, `ThreadSuspension.AllSuspended`) is weak evidence of a resume. It gives "running" if one thread is not in a wait with the reason Suspended or Executive, and it gives "running" for each exception. Before this change, a false "running" caused only a re-suspend. With the Resume Lock, it counts toward a lock of the session of the child. So a Child Resume counts only if the process really ran:

- The service keeps, for each watched process (key: pid and start time, as the Suspension Record), its CPU time (user + kernel, `GetProcessTimes`) at the last time that it saw the process suspended. `Suspend` = `Ok` gives the CPU time that `ProcessControl.Suspend` reads on the same handle after `NtSuspendProcess`. A tick that sees the process suspended outside a hold and a guard gives the CPU time of the listing (`SessionProcess.CpuTime`, read on the same handle as the start time).
- The CPU time of the listing at the suspension is not used: the process ran during its Pending Suspension, so the next tick would always see a growth.
- `ChildResumeEvidence.Judge(suspended CPU time, current CPU time)` in `ParentalControl.Domain` decides. It gives `Ran` only if the CPU time grew by at least 50 ms. It gives `NoGrowth` for less growth, `ReadFailed` if the current read failed, and `NoBaseline` if the service has no CPU time from the suspension.
- Only `Ran` counts. Each other result gives no count. The process stays watched with its old CPU time until the service sees it suspended again, so a run that the next check of its Pending Suspension sees still counts. The planner still suspends it again (the hard rule: no close). The check that gives no count does not use up the 30-second gap of D3.
- A failed read at a tick that sees the process suspended keeps a known CPU time. A new process with the same pid has another start time, so it is not a resume of the old record.
- The service logs each process without evidence: "No Child Resume: <exe> (pid …) of <user> looks resumed, but <evidence> (CPU time +<n> ms, at least 50 ms needed); suspended again".

The threshold is 50 ms. Windows charges the CPU time of a thread in clock ticks, usually 15.625 ms. So "more than 0" and "at least one tick" are the same filter, and one tick can come from a thread that ran for some microseconds: a suspended thread that runs a kernel APC, a thread whose wait ends after the suspension, or the suspension itself before it reaches a running thread. 50 ms is more than 3 ticks; in practice the bar is 4 charged ticks (62.5 ms), so the "+n ms" values of the log are multiples of 15.6 ms. A program that the child really uses between a resume and the next check (up to 60 seconds) uses much more: one paint of a game window takes some milliseconds, and a game paints many times each second. A resumed program that uses less than 50 ms of CPU time in that time gave the child almost nothing, so a missed count costs little.

Rejected: a better thread check. It cannot tell a thread that the child resumed from a thread that runs a kernel APC for a moment, and the VM case shows a false "running" with the current check. Rejected: a count after a "running" result in two checks in a row. A script resumes the process again after each suspension, so this rule only delays the count, and a stuck thread still counts.

## Risks / Trade-offs

- [The thread check sees a suspended process as running, but no tool resumed it.] → Observed on VM 102 on 2026-11-28 (build 5fa4c5d, `TestGame.exe sound=off`, Games limit 1 minute). The child resumed the game with `NtResumeProcess` at 13:35:08 and 13:37:14. The ticks at 13:35:52 and 13:37:52 counted these, and the service suspended the game again at 13:36:02 and 13:38:02. The tick at 13:38:52 counted a third Child Resume and started the Resume Lock, but no resume tool ran between 13:38:02 and 13:39:14. So the third count was very likely false. The cause in the thread states is not known. D10 removes such a count: without a CPU growth of 50 ms there is no count. The VM checks 5.1 and 5.4 check that no count comes without a resume.
- [A third-party program creates a thread in a suspended process (for example a debugger, an antivirus program, or Ctrl+C in a console program).] → A thread that only waits or ends at once uses less than 50 ms and does not count (D10). A thread that runs code for more than 50 ms still counts. The lock needs 3 of these in 10 minutes, and Bonus Minutes end a false lock.
- [A thread whose wait reason the thread check does not know keeps the process "running" after each suspension.] → The known case (Executive) is handled in `ThreadSuspension`. Each other case gives no count without a CPU growth (D10). The VM check 5.4 covers a blocked browser with many processes.
- [The child resumes a minimized program and does not use it. The program uses less than 50 ms of CPU time until the next check.] → No count. The child got almost nothing from the resume. When the child uses the program, it uses CPU time, and the next check counts.
- [A Resume Lock locks the whole session, also the Study programs.] → This is the purpose (issue #24). The child gets a message, and the parent can end the lock.
- [A restart of the service clears the lock.] → Only an administrator can restart the service. A crash restarts it; then a new script run starts a new count.
- [The child sends a fake session-end request and then resumes the game. The service or the Session Agent resumed the game for the end of the session, so the run after the hold or the guard is no Child Resume.] → This is the intended rule (D2): the hold is limited to one unconfirmed hold in 15 minutes, a refused request gives a guard only for an eligible process, and a repeated guard alerts the parent one time per day. Each such cycle gives the child about 80 seconds. A Child Resume of the same process counts again after the next suspension. PR #43 still calls `ResumeSessionForEnd` from its Agent Channel path, so the rule stays after #43.
- [The child resumes the game between two ticks and closes it before the next tick.] → No count, and the child got less than 60 seconds. This is the same as before the change.
