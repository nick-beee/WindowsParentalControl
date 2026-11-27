# Tasks

## 1. Pure logic (Domain)

- [x] 1.1 Add `SessionEndGuards` in `src/ParentalControl.Domain/Categories` (D2): `MarkSuspended`, `MarkResumed`, `Start`, `Covered`, `NextEndUtc`, `Expire`, `SessionEnded`, `Retain`, `Duration` = 60 s.
- [x] 1.2 xUnit tests `SessionEndGuardsTests`: a guard covers only eligible keys of its session; a key that the service resumed is not covered; a key is covered at most once for each suspension and is eligible again after `MarkSuspended`; a running guard is not extended; the guard ends by time and by `SessionEnded`; `NextEndUtc`; `Retain` forgets keys.
- [x] 1.3 `SuspensionPlanner`: new input `GuardedProcesses`. A recorded process in the set is not suspended again and its record stays. A process without a record is not affected. xUnit tests in the planner tests.
- [x] 1.4 xUnit test of the chain (D1): granted hold, expire, refused request with a guard, guard end, refused request during the Pending Suspension → the process is not covered and the Pending Suspension becomes Ready.

## 2. Service

- [x] 2.1 `CategoryEnforcer`: mark each Ok suspension, and each resume by the service (`ResumeRecord`, the resume of `ResumeSessionForEnd`).
- [x] 2.2 On a refused request, start a guard for the session with the keys of its records and log the covered pids.
- [x] 2.3 Pass the covered keys to the planner. Expire guards in the tick. End the guards of an ended session. Retain only the keys of current records.
- [x] 2.4 Include the end of each guard in the follow-up timer (`PublishAndSchedule`).
- [x] 2.5 `GLOSSARY.md`: add Session End Guard; name it in Pending Suspension and Session End Hold where it applies.

## 3. Build and tests

- [x] 3.1 `dotnet build -p:EnableWindowsTargeting=true` on the Mac: 0 errors.
- [x] 3.2 `dotnet test tests/ParentalControl.Domain.Tests` on the Mac: all pass.
- [ ] 3.3 `scripts/vm-test.sh`: `ALL ASSERTIONS PASSED` and the Domain tests pass on VM 102.

## 4. VM checks (VM 102, console, not RDP)

- [ ] 4.1 Second sign-out within 15 minutes, cancelled. Setup: Kid signed in, blocked Notepad copy with unsaved text suspended; first sign-out cancelled by Windows; unlock; second sign-out. Expected: "hold refused" and "guard started" with the Notepad pid; no "Pending suspension … again" for Notepad during the 60 s; Notepad threads run about 30 s after the query; after the cancel Notepad is suspended again within 20 s after the end of the guard.
- [ ] 4.2 Second sign-out within 15 minutes that completes. Setup: a blocked test program without unsaved data suspended, a hold refused. Expected: the agent resumes it, it closes itself (exit status 0 in a process stop log, not 0x40010004), and the session ends.
- [ ] 4.3 Single sign-out with a granted hold (after a service restart). Expected: "hold started", the program is resumed, the session ends, and the hold is confirmed.
- [ ] 4.4 Chrome: a Chrome that the service suspended (started while the service was stopped), a refused sign-out. Expected: the agent log shows the resume result of each `chrome.exe` pid. Record whether any result is not Ok.
- [ ] 4.5 Fake request with `curl` as Kid after a refused hold, while Notepad is suspended. Expected: a refusal; the guard covers no pid; Notepad stays suspended.
