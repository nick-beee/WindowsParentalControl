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
- [x] 3.3 `scripts/vm-test.sh`: `ALL ASSERTIONS PASSED` and the Domain tests pass on VM 102. Result (2026-11-27): `ALL ASSERTIONS PASSED`, 856 Domain tests passed.

## 4. VM checks (VM 102, console, not RDP)

Run on 2026-11-27 with the build of this change (`deploy.ps1`). The times are local VM times. A process stop log (`Win32_ProcessStopTrace`, as SYSTEM) recorded the exit status.

- [x] 4.1 Second sign-out within 15 minutes, cancelled. Setup: Kid signed in, blocked Notepad copy with unsaved text suspended; the budget used by an earlier hold that did not complete; second sign-out with `shutdown /l` as Kid. Expected: "hold refused" and "guard started" with the Notepad pid; no "Pending suspension … again" for Notepad during the 60 s; Notepad threads run about 30 s after the query; after the cancel Notepad is suspended again within 20 s after the end of the guard.
  Result: passed. `10:25:01.251 hold refused`, `10:25:01.256 Session end guard started … covers pids [9628, 7812, 8024, 6316, 7052, 7744, 5300, 576]` (Notepad and Chrome). At 10:25:31 Notepad had 6 threads, 0 suspended. No "Pending suspension" until `10:26:01.755 Session end guard of session 1 ended by its time`. Windows cancelled the sign-out by itself at 10:26:06 (65 s after the query) and locked the session. Notepad: `10:26:12.259 Suspended … again` (10.5 s after the guard). The same steps on `main` (design.md, Reproduction) suspended Notepad 11 s into the sign-out.
- [x] 4.2 Second sign-out within 15 minutes that completes. Setup: a blocked TestGame suspended, a hold refused. Expected: the agent resumes it, it answers Windows and ends, and the session ends without a blocking screen.
  Result: passed. `10:29:32.870 hold refused`, `10:29:32.875 guard started … covers pids [8448]`, TestGame stopped at 10:29:33.819, `10:29:38.293 User logged off`, `Session end guard of session 1 ended: the session ended`. The exit status was 0x40010004. This is the normal status when Windows ends a program after `WM_ENDSESSION`: the Session Agent, Chrome and the TestGame of 4.3 (a granted hold) had the same status. So the exit status does not show a frozen program; the sign-out that completed in 5.4 s without a blocking screen does.
- [x] 4.3 Single sign-out with a granted hold (after a service restart). Expected: "hold started", the program is resumed, the session ends, and the hold is confirmed.
  Result: passed. `10:34:37.045 Session end hold started`, `Resumed C:\Games\TG\TestGame.exe (pid 7008) … "Ok"`, `10:34:37.934 User logged off`, `10:34:37.936 Session end hold of session 2 confirmed`.
- [x] 4.4 Chrome: a Chrome that the service suspended (started while the service was stopped), a refused sign-out. Expected: the agent log shows the resume result of each `chrome.exe` pid. Record whether any result is not Ok.
  Result: the agent resumed each of the 8 recorded pids with "Ok" (10:25:01.231 to .244), the guard covered them, and every `chrome.exe` process stopped between 10:25:01 and 10:25:03 while Notepad still blocked the sign-out. No Chrome process was suspended again. The sandboxed Chrome processes that the service could not close in #33 did not stop the local resume here.
- [x] 4.5 Fake request as Kid (`Invoke-RestMethod`, the same request as `curl`) after a refused hold, while Notepad is suspended. Expected: a refusal; Notepad stays suspended.
  Result: passed. `10:26:33.087 hold refused`, `10:26:33.092 guard started … covers pids [9628]`, and Notepad had 6 of 6 threads suspended 25 s later. The guard covered the eligible pid, but nobody resumed it, so the request gained nothing. A fake request before it, at 10:23:41, was granted (the first of the 15 minutes) and the service suspended all processes again at 10:24:52 (71 s).

## 5. Review of PR #49 (2026-11-27)

- [x] 5.1 Finding 1: `SessionEndGuards.Start` records no guard without an eligible process (`GuardStartOutcome.NothingToCover`). xUnit: `Request_without_an_eligible_process_starts_no_guard`, `Empty_request_does_not_block_a_real_guard_soon_after`.
- [x] 5.2 Finding 2: document the two gaps (a sign-out during a running guard, a sign-out in the re-suspension window) in design.md "Risks" and pin them with xUnit tests (`Gap_*`).
- [x] 5.3 Finding 3: a repeated guard alerts the parent (design D6, spec "Report a repeated Session End Guard"). `Expire(nowUtc, sessionExists)` counts unconfirmed guards per user; `StartedGuard.Repeated`; the enforcer writes a `SECURITY_ALERT` event and sends one alert per user and day. xUnit: the repeated-flag tests and `Sign_out_and_cancel_cycle_gives_at_most_about_70_seconds_per_cycle_and_is_reported`. A first version locked the session at the start of a repeated guard; the VM run 5.7 rejected it.
- [x] 5.4 Finding 4: move the observe step to `SessionEndGuards.Observe`; the chain tests use it instead of a copy. Add the third sign-out after a re-suspension (`Third_sign_out_after_the_re_suspension_is_covered_again_and_repeated`).
- [x] 5.5 Nits: D3 says "without a tool"; Risks says to measure the margin on the child's PC; the two guard requirements define an eligible process in the same words.
- [x] 5.6 Build, Domain tests on the Mac, `scripts/vm-test.sh`. Result: build 0 errors, 0 warnings; 872 Domain tests passed on the Mac; `ALL ASSERTIONS PASSED` and 872 Domain tests on VM 102.
- [x] 5.7 VM 102 (console): hold, guard and repeated guard with the suspended Pad Notepad (unsaved text) and a manual Cancel each time; the text is still there at the end.
  Result (the code of commit 3fc891e; the run used the same code before the docs of this task were added. A watcher logged the suspended threads of Notepad each second):
  - Hold: `11:54:58.005 Session end hold started`, Cancel, `11:55:58.518 … ended without the end of the session`, `11:56:09.018 Suspended … again`.
  - First guard: `11:56:33.412 hold refused`, `11:56:33.417 Session end guard started … covers pids [4216]`. Notepad 0 of 3 threads suspended until `11:57:33.906 … ended by its time without the end of the session`; suspended again at 11:57:44.408 (71 s after the query).
  - Repeated guard: `11:58:08.657 Session end guard started … covers pids [4216], repeated`, and the alert line at 11:58:08.658 (no Telegram chat on the VM). Notepad 0 of 3 suspended until 11:59:09.156; suspended again at 11:59:19.658 (71 s).
  - A manual Cancel (`esc` on the blocking screen) does not lock the session: the watcher showed `Active` through all three cycles.
  - After the three cycles the service resumed Notepad (rule unblocked, service restart), and the screenshot showed the text `kid homework unsaved`.
  - The first version (a lock at the start of a repeated guard) lost the text: `11:46:55.895 … guard started … locked`, the disconnect at 11:46:55.97, Notepad 0 of 4 threads suspended and no save dialog, `11:47:06.992 User logged off: Kid`, and Notepad was gone. See design D6.
