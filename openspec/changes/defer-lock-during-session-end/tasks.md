# Tasks

## 1. Pure logic (Domain)

- [x] 1.1 Add `LockDeferrals` in `src/ParentalControl.Domain/Sessions` (D6): `Request`, `TryDefer`, `SessionEnded`, `NextEnd`, `Expire`, `Duration` = 75 s, `UnconfirmedWindow` = 15 min.
- [x] 1.2 xUnit tests `LockDeferralsTests`: a granted request defers for 75 s; a request during a deferral does not extend it; `TryDefer` marks the deferral as owing; the deferral ends by time and by `SessionEnded`; an owed deferral that ends while the session exists refuses the next request of the user for 15 minutes; a deferral that owed nothing, or that ended with the session, does not count; a trusted request is never refused; another user is not affected.
- [x] 1.3 xUnit test of the bound (D3): a fake request every 10 s for two hours while a lock is always due gives at most one owed deferral in each 15 minutes, and each deferral ends 75 s after its start.
- [x] 1.4 Monotonic time (D9): rename the `…Utc` members of `LockDeferrals`, compute the 15-minute window without a subtraction from the time, and test a time base that starts at year 1.

## 2. Service

- [x] 2.1 Add `SessionLocker` in `src/ParentalControl.Service` (D4, D5): `OnSessionEnding`, `ShouldDefer`, `Lock`, a timer at the end of the earliest deferral, the event `OwedLockDue`. Register it as a singleton.
- [x] 2.2 `SessionTracker`: give the locker the session test and the ended sessions. Use the locker in the unlock check, in the clock check, and for a sign-in during a Break. A deferred lock does not change the tracked state.
- [x] 2.3 `UsageMonitorWorker`: use the locker for the Allowance Time and the Schedule Window, without the event and the message for a deferred lock. Run a tick at once on `OwedLockDue`.
- [x] 2.4 `BreakEnforcer`: use the locker in the Break start and in `LockForRunningBreak`. A deferred lock sends no message and does not call `OnSessionLock`.
- [x] 2.5 `CategoryEnforcer.Fallback`: use the locker. The alert of the parent stays.
- [x] 2.6 `WebServerHost`: call `SessionLocker.OnSessionEnding` in the session-end route (untrusted) and in the Forced Logoff route (trusted), before `ResumeSessionForEnd`.
- [x] 2.7 Check with grep that no lock path of section 2 calls `SessionManager.LockSession` directly. Result: the direct calls left are `SessionLocker`, `DataFolderLockdown` and the Telegram lock (`TelegramBotService.OnLockSessionRequested` in `WebServerHost`), as the proposal states.
- [x] 2.8 `GLOSSARY.md`: add Lock Deferral.
- [x] 2.9 Review of PR #51: `SessionTracker` marks the session as unlocked when the unlock check or a sign-in during a Break finds a deferral (D4). `OnUserLogon` uses the result of `LockForRunningBreak`.
- [x] 2.10 `SessionLocker.LockForParentAsync` (D8); `/api/admin/lock-session` locks through it after its countdown.
- [x] 2.11 `SessionLocker` uses `Environment.TickCount64` through an injected clock (D9); the logs give the seconds left, not a time.
- [x] 2.12 `CategoryEnforcer.Fallback` checks the deferral before the alert. The session-end route returns only `result`, not the deferral.
- [x] 2.13 Rebase on `main` with #40 and #42 (D10): the lock loop of the Storage Lockdown locks through `SessionLocker`; each lock path keeps the order "lock before write" of #40 and writes no event for a deferred lock; the tracked state uses `Stamped()`; MODIFIED delta for `storage-failure`, and the `usage-breaks` delta takes the Control Time Zone text of #40.

## 3. Build and tests

- [x] 3.1 `dotnet build -p:EnableWindowsTargeting=true` on the Mac: 0 errors. Result: 0 errors, 0 warnings.
- [x] 3.2 `dotnet test tests/ParentalControl.Domain.Tests` on the Mac: all pass.
- [x] 3.3 `scripts/vm-test.sh` on VM 102 (2026-11-27): `ALL ASSERTIONS PASSED` and 891 Domain tests passed. This includes the new block "Lock Deferral in the tracker": a deferred unlock check tracks the session as unlocked, the deferral ends by the locker clock, and the parent lock waits for its end.

## 4. VM checks (VM 102, console, not RDP)

Use the Proxmox console, not RDP. In each check, Kid is signed in, Notepad has the unsaved text `kid homework unsaved`, and the sign-out starts from the Start menu (or `shutdown /l` as Kid). Make the lock due at the start of the sign-out: set the limit, the Schedule Window end or the Break Rule so that the next tick of the service falls 0 to 2 s after the start of the sign-out (watch the tick times in the service log). The text must survive in each check. Record the log lines `Lock Deferral started`, `Lock of session … deferred` and the time of the lock.

- [ ] 4.1 Break start at the start of a sign-out. Expected: "Lock Deferral started" before "deferred"; no lock in the 75 s; Notepad shows its save dialog; Cancel → the Break message and the lock within 10 s after the end of the deferral; after the unlock at the end of the Break, Notepad has the text.
- [ ] 4.2 End of the Schedule Window at the start of a sign-out. Expected: as 4.1, with the message of the Schedule Window. Then "Save" in Notepad and "Sign out anyway" or a completed sign-out: the deferral ends with the session, and no lock runs.
- [ ] 4.3 End of the Allowance Time at the start of a sign-out. Expected: as 4.1, with the message of the daily screen time.
- [ ] 4.4 Fallback of a suspension at the start of a sign-out (`PARENTAL_CONTROL_TEST_SUSPEND_FAIL` set to the exe of a game, Games exhausted, and a hold refused before so that the game is suspended during the sign-out). Expected: no lock in the 75 s; the text survives; after Cancel a new Pending Suspension starts, and the session locks within 20 s after the end of the deferral.
- [ ] 4.5 Unlock check and lock again during a Break: a Break runs, the child unlocks and signs out at once. Expected: no lock in the 75 s, the text survives.
- [ ] 4.6 Timing of Windows: a sign-out with Notepad that the child does not answer. Measure from the log line `Lock Deferral started`, not from the query of Notepad: the agent gets the first query, so the deferral starts earlier. Run it with Notepad alone, and again with 2 or 3 programs open (Notepad and a browser or a game). Record the time to the cancel of Windows in each run (expected less than 75 s). Record whether the owed lock runs after the cancel and the text survives.
- [ ] 4.7 Fake request as Kid (`Invoke-RestMethod` to `/api/agent/session-ending`) at the end of the Allowance Time, without a sign-out. Expected: the lock runs at most about 85 s after it was due. A second fake request 5 minutes later: "Lock Deferral refused", and the next tick locks after the unlock.
- [ ] 4.8 Run the same checks on the child's PC before the release.
- [ ] 4.9 Probe: a lock of the parent from the admin page (countdown 5 s) while the blocking screen of Windows shows ("programs prevent sign-out"). Use a sign-out that no deferral covers: run it within 15 minutes after the refused request of 4.7, so the log shows "Lock Deferral refused" for this sign-out. (Do not stop the agent: the service starts it again.) Record whether Notepad keeps its text. The result tells how much the margin of 4.6 matters.
- [ ] 4.10 A lock of the parent with a countdown of 15 s; the child starts a sign-out during the countdown and clicks Cancel in Notepad. Expected: "Lock of session … deferred" with reason `lock of the parent`, the lock about 0.5 s after the end of the deferral, and the text survives.
