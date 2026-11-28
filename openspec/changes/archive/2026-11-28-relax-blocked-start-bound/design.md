# Design

## Context

The spec says that the service closes a Blocked Start within 2 seconds after its start. The service uses the WMI class `Win32_ProcessStartTrace` (`ProcessStartWatcher`). On VM 102, WMI delivered one event late: one Edge start took about 3.2 seconds. 24 of 25 starts took 0.23 to 1.7 seconds (issue #46).

## Decisions

### D1. Change the bound to 5 seconds and keep WMI

Decided by the orchestrator under the user's delegation (2026-11-28).

The spec bound changes to 5 seconds. The service keeps WMI.

Reasons:
- The late event is rare: 1 of 25 starts.
- 5 seconds is above the worst start that the VM run measured (3.2 seconds) and leaves room.
- The bound is a target for a close. It is not a safety bound. The start window of 10 seconds (`BlockedStartDecider.StartWindow`) is the safety bound, and it stays.

Alternative that we reject for now: ETW (Event Tracing for Windows) instead of WMI. It needs a kernel or real-time ETW session. It needs more code and more privileges. It gains about 2 seconds on a rare late event. Revisit it if a later VM run shows starts over 5 seconds.

### D2. The hard rule is not affected

The close decision does not depend on the delay of the WMI event. We checked this in the code and in the archived design:

- `ProcessStartWatcher` takes only `ProcessID`, `ParentProcessID` and `SessionID` from the event (`SELECT ProcessID, ParentProcessID, SessionID FROM Win32_ProcessStartTrace`). It takes no time from the event.
- `BlockEnforcer.OnProcessStart` reads the exe path and the creation time of the process from Windows (`ProcessControl.QueryPathAndStart`).
- `BlockedStartDecider.Decide` closes only if the creation time is later than the block epoch and the age (detection time minus creation time) is 10 seconds or less. Else it gives `Suspend`. It never gives `Close` for a late event that falls outside the start window.
- The archived design `add-blocked-programs` (D3 and the decider section) states the same.

A late event thus can only change a close to a suspension, never a suspension to a close. A late event up to 10 seconds still closes the process. A program of the child that ran before the block is never closed.

## Risks

- A later VM run can show starts over 5 seconds. Then we revisit D1 and look at ETW.
