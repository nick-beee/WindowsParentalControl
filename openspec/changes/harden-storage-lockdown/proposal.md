# Proposal

## Why

The review of PR #40 (`harden-clock-and-disk`) found gaps in the Storage Lockdown (GitHub issue #55):

- **S1: the wrong cause.** Each exception in the tick counts as a failed tick, not only a database error. A bug in a limit check, or a WinAPI error, starts and holds the Storage Lockdown. The event and the alert then say that the disk is full. The parent frees space, and the child stays locked with no correct cause.
- **S2: a game runs past its limit.** If the write of a suspension record fails, the service does not suspend the process, and only the Storage Lockdown covers the gap. The Storage Lockdown needs 3 failed ticks in a row, and one good tick resets the count. A child who fills and frees the disk in turns keeps the count below 3. The game then runs past its Category Limit without an event or an alert.
- **S3: no check at the start.** The design of #40 claims that a start on a full disk falls into the start lockdown of the data folder. Nobody checked this, and the open of an existing database does not need a write. So each restart on a full disk gives the child up to 3 more ticks without a lock, and the restart also loses the Pending Usage of those ticks.
- **Smaller points.** A minute can count two times (or zero times) in a limit check if a store runs during the read of the stored time and the Pending Usage. The entry into the Storage Lockdown writes to the database before it starts the lock loop. The lock loop can use the token of a disposed `CancellationTokenSource`. A Schedule Window end in the repeated hour at the end of daylight saving time lets the child in for up to one more hour. The spec says that the Admin app shows the Pending Usage, but it shows the stored time. No test covers the limits 60 seconds back, 100 MB free and the gap of 2 to 3 minutes.

## What Changes

- **The cause of a failure.** The service classifies each failure: a full disk, a read-only or denied database, a locked database, a disk or file error, another database error, or an internal error (not a database error). Event 1015 and the Telegram alert name the failed operation and the cause. Only a full disk gives the text "the disk is full" and the advice to free space. A persistent internal error still locks the sessions (fail closed), with its correct cause.
- **A lock in place of a suspension without a record.** If the record of a suspension cannot be written, the service locks the session of the process in place of the suspension. It never suspends a process without a record, and it never closes a program.
- **A window of failed ticks.** The Storage Lockdown starts after 3 failed ticks within 10 minutes, not after 3 failed ticks in a row. A good tick between them does not reset the count.
- **A check at the start.** If the probe write of the first tick after the start fails, the service enters the Storage Lockdown in that tick.
- **Small fixes.** A consistent read of the stored time and the Pending Usage. The lock loop starts before the writes of the entry. The lock loop owns its token. The Schedule Window ends at the first instant of a repeated end time. The spec says that the Admin app shows the stored time. Boundary tests for 60 seconds back, 100 MB free and the gap of 2 to 3 minutes.
- **A test hook.** A build with `-p:VmTest=true` has the hook `PARENTAL_CONTROL_TEST_CHECK_FAIL`. It makes the limit check throw a non-database error, so the VM can check the text of a non-disk cause.

## Out of scope

- A new name for the Storage Lockdown. It now also covers a limit check that cannot run, but the term stays (design D3).
- The remaining time that the agent status and the request page show in the repeated hour. These are display values; the lock comes from the checks of the tick, the sign-in and the unlock (design D9).
- A message to the child about the cause of the lock.
- German texts for the Storage Lockdown alert. The language of the parent alerts is a separate change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `storage-failure`: the cause of a failure in the report, the lock in place of a suspension without a record, the window of failed ticks, the check at the start, the consistent read, and the Admin app view.
- `control-clock`: the end of the Schedule Window in the repeated hour.

## Impact

- `src/ParentalControl.Domain`: new `StorageFailure` (classification and texts), `FailedTickWindow`; changes in `StorageLockdownState`, `PendingUsage` and `ControlClock`.
- `src/ParentalControl.Service`: `StorageHealth`, `UsageLedger`, `UsageMonitorWorker`, `SessionTracker`, and a few lines in `CategoryEnforcer.Suspend` (a file of the open PR #62).
- Tests: xUnit tests in `tests/ParentalControl.Domain.Tests`, the console checks in `tests/ParentalControl.Tests`.
- Docs: `GLOSSARY.md` (Storage Lockdown), `docs/vm.md` (the new hook), `docs/ROADMAP.md`.
- No new table, column, setting or registry value. No new WinAPI call.
