# Tasks

## 1. Domain: pure logic

- [x] 1.1 Add `LockReason` and `LockReasons` in `Domain/Sessions` (design D1, D2): `Resolve`, `ToWire`, `FromWire`, `WidgetTextKey`, `TrayTextKey`. Verify with xUnit tests: a Break ended at the Schedule Window end gives the Schedule Window; a running Break with time left gives the Break; a used-up Allowance Time during a Break gives the Allowance Time; an unlocked session gives no reason; a missing or unknown wire name on a locked session gives "other".
  - Done: `LockReasonsTests`.
- [x] 1.2 Add the texts of each Lock Reason to `AgentTexts` in "de", "en" and "id" (design D4). Verify with xUnit tests: the German texts, only the Break text says "Pause", each tray tooltip fits 63 characters.
  - Done: `LockReasonsTests`, `AgentTextsTests.TrayTooltipsFitTheLimit`.
- [x] 1.3 Add `lockReason` to `AgentStatus`. Verify that the largest status answer still fits in one frame.
  - Done: `AgentStatusLimitsTests`, `LockReasonsTests.Status_answer_carries_the_reason_under_its_wire_name`.
- [x] 1.4 In `BreakPlanner.Tick`, start no Break if `MinutesUntilOtherLock` is 0 (design D3). Verify with xUnit tests.
  - Done: `BreakPlannerTests.NoBreakAfterTheScheduleWindowEndWhenADeferralKeepsTheSessionUnlocked`, `BreakStartsWhenTheOtherLockIsLater`.

## 2. Service and agent

- [x] 2.1 `AgentRequests` gets `BreakEnforcer` and sets `lockReason` from the Schedule Window, the remaining Allowance Time and the running Break of each status answer.
- [x] 2.2 The agent reads `lockReason` (`AgentStatusResponse`), and the widget and the tray tooltip show the text of the reason.
- [x] 2.3 Add the term Lock Reason to `GLOSSARY.md`.

## 3. Verification

- [x] 3.1 On the Mac: `dotnet build -p:EnableWindowsTargeting=true` passes, `dotnet test tests/ParentalControl.Domain.Tests` passes, and `openspec validate --all --strict` passes.
- [ ] 3.2 On VM 102 (snapshot `test-base-net10`, Language Preset "de", Proxmox console session of `Kid`, not RDP):
  1. Install the build of this branch.
  2. Set the Break Rule of `Kid` to 10 minutes of use and 5 minutes of break. Set today's Schedule Window of `Kid` to end about 13 minutes after the sign-in in step 3 (for example 20:00), with enough Allowance Time.
  3. As `Kid`, sign in and use the PC for 10 minutes. Expect: the Break starts about 3 minutes before the Schedule Window end, and its message says it lasts until the Schedule Window end.
  4. Expect during the Break: an unlock locks again, and in the short unlock before the new lock the widget shows "⏸ Gesperrt (Pause)" (take a screenshot of the console).
  5. Wait until after the Schedule Window end. Unlock the session as `Kid`.
  6. Expect: the session locks again because of the Schedule Window, and in the short unlock before the new lock the widget shows "🌙 Gesperrt (keine Computerzeit)", not "⏸ Gesperrt (Pause)". The event list has the Break end with the reason "schedule end".
