# Tasks

## 1. Domain: pure logic

- [x] 1.1 Add `ResumeLocks` in `src/ParentalControl.Domain/Categories` (design D1 to D4, D6): the watched processes (`MarkSuspended`, `MarkResumedByService`, `Retain`), `Check` (one Child Resume per user and check, 30 seconds between two, 3 in 10 minutes start a lock, a Child Resume during a lock adds its exes) and `Release` (new day, more Bonus Minutes, no exe still due; the strikes clear). Verify with xUnit tests in `tests/ParentalControl.Domain.Tests/ResumeLocksTests.cs`:
  - a watched process that runs again counts one Child Resume; an unwatched one does not;
  - 10 resumed processes of one user in one check count 1;
  - the same process does not count again in the next check of its Pending Suspension;
  - a check 15 seconds after a counted Child Resume does not count; a check 30 seconds after does;
  - 3 Child Resumes in 10 minutes start one lock with the exes of all 3; 2 do not; 3 over 11 minutes do not;
  - different exes of one user count together; two users count apart;
  - hold path: `MarkResumedByService`, then the process runs again → no count; after a new `MarkSuspended` it counts again;
  - guard path: the same with the covered processes;
  - `Retain` drops a process without a record;
  - `Release` ends a lock on a new day, after more Bonus Minutes, and when no exe is still due; it keeps the lock while one exe is still due; after the end a new lock needs 3 new Child Resumes;
  - a Child Resume during a lock starts no second lock and adds its exe.
  - Done: `Domain/Categories/ResumeLocks`, with 22 tests in `ResumeLocksTests`.
- [x] 1.2 Add the rows `resume_lock_title` and `resume_lock_body` to `SessionTexts` in de, en and id (design D8). Verify with an xUnit test of the German and English texts.
  - Done: the theory `ResumeLockTexts` in `SessionTextsTests`.
## 2. Service

- [x] 2.1 In `CategoryEnforcer`, hold one `ResumeLocks`. In `Tick`: `Retain` with the records; `MarkSuspended` for each recorded process that the tick sees suspended outside a hold and a guard; after the plan, `Release` and `Check` (design D5); `MarkSuspended` after `Suspend` = `Ok`. `MarkResumedByService` in `ResumeRecord` and in `ResumeSessionForEnd` (each record of the session, also for a refused hold, so also the processes of a guard). Verify that `dotnet build -p:EnableWindowsTargeting=true` passes.
- [x] 2.2 For each new lock: the `SECURITY_ALERT` event and the parent alert with the key `resumelock:<userId>` (design D8). Collect the unlocked sessions of each locked user, and lock them after the gate: `ShouldDefer`, the child message (at most one time in 10 seconds per session), `SessionLocker.Lock`, `SessionTracker.OnSessionLock` (design D5). Verify that the build passes and that the log names each lock with the reason "Resume Lock".

  - Done: `CheckResumeLocks` and `LockForResumeLock` in `CategoryEnforcer`. The log lines are "Child Resume of … (n of 3 in 10 min)", "Resume Lock of … started", "Session … locked: Resume Lock" and "Resume Lock of … ended: <reason>".
## 3. Documentation

- [x] 3.1 Add the term "Resume Lock" (with Child Resume) to `GLOSSARY.md` after "Blocked Start" (design D9). Verify that `git diff origin/main -- GLOSSARY.md` shows only the new term.

  - Done: the new term is after "Blocked Start", before "Language Preset".
## 4. Verification on the Mac

- [x] 4.1 `dotnet build -p:EnableWindowsTargeting=true` passes, `dotnet test tests/ParentalControl.Domain.Tests` passes, and `openspec validate --all --strict` passes.

  - Done (2026-11-28): the build has 0 warnings and 0 errors; 1098 tests pass; `openspec validate --all --strict` gives 11 passed.
## 5. Verification on VM 102

Setup for each check: roll back to the snapshot `test-base-net10`, install the build of this branch with `scripts/vm/guest/deploy.ps1`, sign in as `Kid` in the Proxmox console (not RDP), Language Preset "de", Breaks off. Add the rule `TestGame.exe` → Games, give Games a limit of 1 minute today, and keep `TestGame.exe` in front until it is suspended. `scripts/vm/kid-run.sh resume-process.ps1 -Name TestGame` (from the Mac; it runs the script as `Kid` in the console session through a scheduled task) calls `NtResumeProcess` one time. Read the service log with `scripts/vm/guest/state.ps1` or the newest file in `C:\ProgramData\ParentalControl\logs`.

- [ ] 5.1 Repeated resume. Run `kid-run.sh resume-process.ps1 -Name TestGame` after each "Suspended … TestGame.exe … again" line in the service log, three times, at least 60 seconds apart. Expect: the log counts three Child Resumes; after the third, the child gets the message "Du hast pausierte Programme immer wieder fortgesetzt. …" and the session locks within 70 seconds after the third resume; TestGame still exists and is suspended again; the event list has one `SECURITY_ALERT` "Paused programs resumed again and again (TestGame.exe): session locked (Resume Lock)"; the parent gets one Telegram alert.
- [ ] 5.2 Re-lock. Unlock the session as `Kid` during the Resume Lock. Expect: the message, and the lock again within 10 seconds. Sign out and sign in again. Expect: the lock within 70 seconds. Notepad with unsaved text still has its text after each unlock.
- [ ] 5.3 End. Approve 5 Bonus Minutes for `Kid` (`scripts/vm/guest/grant.ps1`). Expect: the log shows the end of the Resume Lock; the next unlock is not locked again; TestGame resumes because Games has time left. On a second run of 5.1 on the same day, expect a new event and no second Telegram alert.
- [ ] 5.4 No false count. Suspend TestGame. Turn off the block of `msedge.exe` in the web admin, start Edge as `Kid` with `kid-run.sh kidstart.ps1` (`kidstart.txt` lists `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`), and turn the block on again, so that the running Edge (many processes) is suspended because of the block. Leave both suspended for 15 minutes without a resume tool. Then start a sign-out as `Kid` and cancel it with unsaved Notepad text, three times within 10 minutes. Expect: the log shows no Child Resume and no Resume Lock; the programs are suspended again after each hold or guard.
- [ ] 5.5 Many processes. With the blocked Edge of 5.4 suspended, resume all Edge processes in one run: `kid-run.sh resume-process.ps1 -Name msedge` (`Get-Process msedge` gives all processes, and `$p.Handle` of an array fails, so first change the last line of a copy of the script to `Get-Process msedge | ForEach-Object { [W.N]::NtResumeProcess($_.Handle) }`). Expect: the log counts one Child Resume, not one per process, and no lock; Edge is suspended again within 70 seconds.
- [ ] 5.6 Lock Deferral. Open Notepad with unsaved text. Run the third resume of 5.1 (`kid-run.sh resume-process.ps1 -Name TestGame`), and at once start a sign-out as `Kid`; cancel it at the Notepad prompt. Expect: the log shows the start of the Resume Lock and "Lock of session … deferred"; no lock during the 75 seconds of the deferral; the message and the lock within 10 seconds after its end; Notepad keeps its text.

## Workflow follow-up

- `/opsx:verify`, then `/opsx:archive` after the VM checks pass.
- Final check on the child's PC (docs/ROADMAP.md, "Итоговая проверка на ПК ребёнка").
