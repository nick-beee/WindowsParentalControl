# Design

## Context

See proposal.md for the motivation and specs/ for the required behaviour. The base is the design of the archived change `2026-11-27-harden-clock-and-disk` (D6 Pending Usage, D7 the order of the tick, D8 Storage Lockdown).

Current state that shapes the approach:

- **The failed tick.** `UsageMonitorWorker.ProcessTick` sets `readFailed` if `SessionTracker.TickAllSessions` or `CheckLimits` throws, for any exception. `StorageHealth` counts the failures of `TryWrite` and of the ledger as one number. `ReportStorage` gives the Storage Lockdown a bool only, so the entry text of event 1015 and the alert do not know the cause. The alert text always says "the disk is full or the database cannot be written" and tells the parent to free space.
- **The count.** `StorageLockdownState.FailedTicksInRow` goes back to 0 after one tick without a failure.
- **The suspension record.** `CategoryEnforcer.Suspend` calls `SuspensionRepository.Insert` before `NtSuspendProcess`. If the insert fails, it returns: no suspension, no lock, no event.
- **The start.** `StartupWorker` opens and migrates the database. An existing database at the current schema needs no write for that, so a full disk does not stop the start. The first tick of `UsageMonitorWorker` writes the probe at its end, but one failed tick does not enter.
- **The read of a limit.** The callers read the stored row (`UsageRepository.GetUsage`) and then `UsageLedger.AllowanceUsed(userId, day, stored)` adds the Pending Usage. A store on another thread (an activity sample of the Session Probe) can commit an entry and remove it from the Pending Usage between the two reads.
- **The open PRs.** PR #62 changes `CategoryEnforcer.cs` (but not `Suspend`). PR #66 changes `WebServerHost.cs` and `AgentRequests.cs`. This change edits `CategoryEnforcer.Suspend` in a few lines only, and edits neither `WebServerHost.cs` nor `AgentRequests.cs`.

## Goals / Non-Goals

**Goals:**

- The parent learns the real cause of a Storage Lockdown, and a non-disk cause never claims a full disk.
- A failed write of a suspension record never gives the child time past a Category Limit.
- A child who fills and frees the disk in turns, or restarts the PC, gets no time without a lock.
- The decision logic is pure code in `src/ParentalControl.Domain` with xUnit tests on the Mac.

**Non-Goals:**

- A new term for the Storage Lockdown.
- A change of the remaining time that the agent status and the request page show in the repeated hour.
- A German text of the alert (separate change for the language of parent alerts).

## Decisions

All decisions below were decided by the orchestrator under the user's delegation (2026-11-28).

### D1. Classify each failure

New pure type `StorageFailure` in `src/ParentalControl.Domain/DataFolder`: the operation (a fixed label, D2), the kind, and the name of the exception type (for example `SqliteException`). The type name holds no data of the child, so the texts can show it. New enum `StorageFailureKind`: `DiskFull`, `ReadOnly`, `Locked`, `DiskError`, `DatabaseError`, `Internal`.

- The pure function `StorageFailureClassifier.Classify(facts)` takes plain values, so the Domain needs no SQLite package: the primary SQLite error code (if the exception chain has a `SqliteException`), the HResult of an `IOException`, and the flags "is an I/O error" and "access denied".
  - SQLite 13 (`SQLITE_FULL`), or HResult `0x80070070` (`ERROR_DISK_FULL`) or `0x80070027` (`ERROR_HANDLE_DISK_FULL`): `DiskFull`.
  - SQLite 8 (`SQLITE_READONLY`), 3 (`SQLITE_PERM`) or 23 (`SQLITE_AUTH`), or access denied: `ReadOnly`.
  - SQLite 5 (`SQLITE_BUSY`) or 6 (`SQLITE_LOCKED`): `Locked`.
  - SQLite 10 (`SQLITE_IOERR`) or 14 (`SQLITE_CANTOPEN`), or another I/O error: `DiskError`.
  - Another SQLite code: `DatabaseError`.
  - No SQLite code and no I/O error: `Internal`.
- The extended SQLite codes carry the primary code in the low 8 bits, so the classifier masks the code with `0xFF`.
- The service maps an `Exception` to the facts (`StorageFailureFacts.From` in the service, which walks the inner exceptions).
- The free space does not change the kind. The texts (D2) name the free space, so a parent sees a nearly full disk also with another kind.
- *Alternative: a disk-full kind if the free space is under 100 MB.* Rejected. On a nearly full disk a locked database is still a locked database, and the text names the free space anyway.

### D2. The texts name the operation and the cause

`StorageHealth` keeps the count of the failures and the last failure (operation and kind) since the last tick. `TryWrite(write, what, logger)` keeps its signature and gets an optional `operation`; it classifies the exception, and the operation is `operation ?? what`. `ProcessTick` reports the exceptions of `TickAllSessions` and `CheckLimits` as failures of the operations "count of the session time" and "limit check", with their classification. `StorageLockdownState` keeps the last failure of the failed ticks, so the entry has a cause also when the tick of the entry had only a failed probe.

- Event 1015 and the alert come from the pure `StorageLockdownTexts` in Domain: the operation, a cause text per kind, and the free space.
- Only `DiskFull` says "the disk is full" and tells the parent to free space. The other kinds tell the parent to read event 1015 and the service log, and name the cause ("the database is read-only or access is denied", "another program locks the database", "a disk or file error", "a database error", "an internal error of the service, not a database error").
- The operation is a fixed label, for example "suspension record", "usage store", "storage probe", "limit check". Each `what` text of `TryWrite` is a fixed text, with one exception: `suspension record of <path of the exe>`. That call passes the label "suspension record" as `operation`, and the full `what` text goes to the service log only. So no data from a program of the child goes into the event or the Telegram alert (CLAUDE.md, "Security"). The detail of the exception (type and message) goes to the log only, because a message can quote a path too.
- The texts stay in English, as before.

### D3. A non-disk failure still locks

A persistent error that is not a database error (a bug in a limit check, a WinAPI error) still counts as a failed tick, starts the Storage Lockdown and holds it. A limit that the service cannot check must not give the child free time, so this fails closed. The event and the alert give the correct cause, so the parent knows that free space does not help.

- The term "Storage Lockdown" stays. `GLOSSARY.md` says that it also covers a limit check that cannot run.
- *Alternative: count only database errors.* Rejected. A limit check that throws on each tick stops the Allowance Time and the Schedule Window, and the child gains the same as on a full disk.
- *Alternative: a new term (for example "Enforcement Lockdown").* Rejected. The behaviour is the same lock; a second term adds churn in the code, the specs and the event IDs.

### D4. A lock in place of a suspension without a record

If `SuspensionRepository.Insert` fails in `CategoryEnforcer.Suspend`, the enforcer calls `Fallback` with the reason "the suspension record could not be stored". `Fallback` locks the session through `SessionLocker` (with the Lock Deferral), marks it with `OnSessionLock`, and then writes the security event and sends the parent alert once per exe and day. The rule "lock before write" holds.

- After an unlock, `OnSessionUnlocked` requests a category tick. The planner plans the suspension again, the Pending Suspension waits 10 seconds, and the next failed insert locks again. So the child gets at most about 10 seconds after each unlock. If the insert works again, the enforcer suspends the process as normal.
- The edit in `CategoryEnforcer.cs` is small: it computes the user before the insert and calls `Fallback` in place of `return`. PR #62 does not change `Suspend`.
- *Alternative: suspend the process and keep the record in memory, and try the write again later.* Rejected. The record comes before the suspension so that a crash of the service cannot leave a suspended process without a record. An in-memory record does not survive a crash, and `ResumeAll` at a service stop reads only the stored records. The game would stay frozen, and the hard rule says that the service resumes a suspended process later.

### D5. A window of failed ticks

New pure class `FailedTickWindow` in `src/ParentalControl.Domain/DataFolder`. It keeps the monotonic times (`Environment.TickCount64`) of the failed ticks of the last 10 minutes. `StorageLockdownState` enters the Storage Lockdown when the window has 3 failed ticks. A successful tick does not remove a failed tick from the window. The exit from the Storage Lockdown clears the window.

- The window uses time, not a count of ticks. An owed lock (the end of a Lock Deferral) runs an extra tick, so a count of the last 10 ticks would cover less than 10 minutes.
- 10 minutes: 3 ticks in a row still enter at the third tick. A child who fills and frees the disk in turns enters at the third failed tick. A single short error every few minutes (a virus scanner, a backup) does not lock.
- The rest of `StorageLockdownState` stays: the exit needs a successful retry, an empty Pending Usage and at least 100 MB free, and the alert comes once per control day.
- *Alternative: keep "3 in a row".* Rejected. S2: one good tick resets the count, so the child can keep it below 3. With D4 the game no longer runs, but the Pending Usage and the other writes still lose their protection.

### D6. The probe write of the first tick

`StorageLockdownState.ReportTick` gets the flag `firstTick`. If the probe write of the first tick after the start of the service fails, it enters the Storage Lockdown at once. `UsageMonitorWorker` passes `true` for the first `ReportStorage` of the process.

- The first tick runs right after the start gate, so the lock comes within seconds of the start.
- Only a failed probe write enters at the start. Low free space alone does not, because a start with less than 100 MB free and working writes is not an error.
- `StartupWorker` does not change.
- *Alternative: a separate probe in `StartupWorker` before the gate.* Rejected. The start lockdown of the data folder has its own exit rule (a full start), and its exit would not check the Pending Usage or the free space. The first tick already has the probe.
- Risk: a database that is busy for 5 seconds (`busy_timeout`) at the start locks the child until the next tick (60 seconds). This fails closed and is rare.

### D7. A consistent read of the stored time and the Pending Usage

`PendingUsage` gets `Consistent<T>(Func<T> read)`. It runs the read under the gate of the store (`_storeGate`), so no store commits or removes an entry during the read. `UsageLedger.AllowanceUsed(userId, day)` and `UsageLedger.CategoryUsage(userId, day)` read the stored row and the Pending Usage inside it.

- The old `AllowanceUsed(userId, day, stored)` keeps its signature for the callers in the files of PR #66. It now reads the stored minutes again inside the gate and ignores the minutes of `stored`. So each caller gets the fix without an edit of these files.
- The callers outside the PR files (`UsageMonitorWorker`, `SessionTracker`, `BreakEnforcer`) use the new overload.
- A store holds the gate while it writes. On a full disk the writes fail fast. On a busy database a reader waits for at most the `busy_timeout` of each write.
- *Alternative: a sequence counter (seqlock).* Rejected. The callers pass the stored row, so a retry of the stored read needs the same change of the reader, and the gate is simpler.

### D8. The entry order and the token of the lock loop

- `EnterStorageLockdown` starts the lock loop first, then writes event 1015, then the event row, then posts the alert. A slow or failing write then cannot delay the first lock.
- `StartLockLoop` takes the token of the new `CancellationTokenSource` before `Task.Run`, and the loop uses only that token. The loop disposes the source in its `finally`. `StopLockLoop` only cancels it. So no code uses the token of a disposed source.

### D9. The end of the Schedule Window in the repeated hour

New pure method `ControlClock.IsOutsideWindow(moment, start, end)`: true if the time of day is before the start or at or after the end, or if the UTC time is at or after `ToUtc(today, end)`. `ToUtc` gives the earlier instant for a time in the repeated hour (design D2 of `harden-clock-and-disk`). So the window ends at the first instance of the end time and stays closed for the rest of the day.

- The check of each tick (`UsageMonitorWorker.CheckSession`), the sign-in check and the unlock check (`SessionTracker`) use it.
- A start time in the repeated hour needs no change: the time-of-day check closes the window again in the repeated part, which only locks more.
- Not changed: the remaining time that the agent status and the request page show (`AgentRequests`, `WebServerHost`, files of PR #66) and the warnings before the end. These are display values for one night per year.

### D10. The Admin app shows the stored time

The spec "Pending Usage" said that each remaining time that the system shows uses the Pending Usage. The Admin app reads the database directly and cannot see the memory of the service. The spec now says that the service and the Session Agent show the stored time plus the Pending Usage, and the Admin app the stored time only. No code change.

### D11. Boundary tests

xUnit tests for the limits of the existing rules:

- `ElapsedTime`: a UTC step back of exactly 60 seconds is not "clock back"; 60 seconds and 1 tick is. 2 minutes of the monotonic clock add 2 minutes; 179.999 seconds add 2 minutes; 3 minutes are a gap that adds 1 minute.
- `StorageLockdownState`: exactly 100 MB free leaves; 1 byte less stays.
- `FailedTickWindow`: a failed tick exactly 10 minutes old is out of the window.

### D12. A test hook for a non-disk failure

A build with `-p:VmTest=true` (compile symbol `VM_TEST`) has the hook `PARENTAL_CONTROL_TEST_CHECK_FAIL` in `UsageMonitorWorker`. If the variable names a file and the file exists, `CheckLimits` throws an `InvalidOperationException` for each unlocked session. So the VM can check the classification "internal error" and the text of event 1015.

- The hook is inside `#if VM_TEST`. `ProbeSourceTests` scans `UsageMonitorWorker.cs` for the name of the hook, as for the hooks of the probe, so the release has none.
- The existing hook `PARENTAL_CONTROL_TEST_WRITE_FAIL` (in Core) gives `SQLITE_READONLY`. With D1 the texts of that test say "read-only or access denied", not "disk full". The VM steps expect this text.

## Risks / Trade-offs

- [A persistent bug in a limit check locks the child until an update] → Accepted: fail closed (D3). The event and the alert name the cause, so the parent can act.
- [A busy database at the start locks the child for up to 60 seconds] → Accepted (D6).
- [The lock in place of a suspension also locks the other programs of the child] → Accepted. The lock is the fallback that the hard rule allows, and it ends when the write works again. Each program stays open.
- [The window of 10 minutes can enter on 3 short errors in 10 minutes] → Accepted. 3 errors in 10 minutes are not normal, and the lockdown ends at the next good tick with free space.
- [`CategoryEnforcer.cs` is also changed by PR #62] → The edit is a few lines in `Suspend`, which PR #62 does not change. The second PR to merge rebases.

## Migration Plan

No data change. The update replaces the service. No new setting, table, column or registry value.

## Open Questions

None.
