# Design

## Context

See proposal.md for the motivation and the spec deltas for the required behaviour.

The current code has these properties:

- `AgentRequests.Status` builds the `AgentStatus` for each poll of the Session Agent. It sends `isLocked` from `ActiveSession.IsLocked`, which the session events of Windows set. It already computes whether the time is outside the Schedule Window (`curfewSec`) and the remaining Allowance Time (`remMinutes`).
- `WidgetWindow.UpdateData` shows `widget_locked` ("⏸ Gesperrt (Pause)") if `isLocked` is true. `AgentController` shows `tray_locked` ("Parental Control: Gesperrt (Pause)").
- `BreakPlanner.Tick` clamps the end of a Break to the Schedule Window end, and `BreakState.IsBreakRunning(now)` depends on the time only. So the Break state ends correctly at 20:00. The issue is only the text.
- `BreakEnforcer.OtherLockTimes` gives `(0, null)` after the Schedule Window end and 0 minutes if the Allowance Time is used up.

Each decision below is decided by the orchestrator under the user's delegation (2026-11-28).

## Decisions

### D1. Priority: Schedule Window > Allowance Time > Break > other

The two limits of the day come first, because they keep the session locked after the end of a Break. A Break that ends at 20:00 with the Schedule Window then gives the Schedule Window reason. If the parent lowers the daily limit during a Break, the widget shows the Allowance Time. "Other" covers a lock by the child, by the parent, by a Category Limit that cannot pause a program, a Probe Lock and a Storage Lockdown.

Rejected: Break first. The child would see "Pause" after the end of the day.

### D2. Compute the reason live, in pure code

`LockReasons.Resolve(isLocked, outsideScheduleWindow, allowanceUsedUp, breakRunning)` runs for each status answer with the state at that time. It reads only service state (the schedule, the ledger, the break state), never data of the Session Agent. The wire name is a string, not an enum number, so an agent and a service of different versions agree. A missing or unknown name on a locked session gives "other".

Rejected: store the reason at the time of the lock (`SessionTracker.OnSessionLock`). The Break lock at 19:55 would still say "Break" at 20:01: the same bug.

### D3. No Break start after the end of the Allowance Time or the Schedule Window

`BreakPlanner.Tick` starts no Break if `MinutesUntilOtherLock` is 0. Before, a Lock Deferral at the Schedule Window end could keep the session unlocked, and a Break could start at 20:00 with an end at 20:10. That is against the requirement "A Break ends at the Schedule Window end". The widget shows the Schedule Window reason in that case too (D1), so this is a second guard, not the fix of issue #44.

### D4. Texts

| Reason | de (widget) | en (widget) |
|---|---|---|
| Schedule Window | 🌙 Gesperrt (keine Computerzeit) | 🌙 Locked (outside allowed hours) |
| Allowance Time | ⏸ Gesperrt (Zeit für heute um) | ⏸ Locked (no time left today) |
| Break | ⏸ Gesperrt (Pause) | ⏸ Locked (break) |
| Other | ⏸ Gesperrt (Zeit läuft nicht) | ⏸ Locked (time stopped) |

The tray tooltip has the same words after "Parental Control: ". Each tooltip has at most 63 characters (`NotifyIcon.Text`). The "id" text for another lock stays the text of the earlier agent.

No WinAPI call, table or column is new.

## Risks

- The agent gets the reason only with the next status poll (at most some seconds). A Windows session that is locked hides the widget, so the child sees the text only in a short unlock before the next lock, or in the tray tooltip.


## Review follow-up (2026-11-28)

Decided by the orchestrator under the user's delegation (2026-11-28): `MinutesUntilOtherLock` counts whole minutes, so no Break starts in the last minute before the end of the Allowance Time or the Schedule Window. The spec says so now. A Break of less than 1 minute before the lock of the day gave the child nothing. The Lock Reason is "allowance" only if the user has a limit row: without one, the service enforces no Allowance Time, and the default of 120 minutes in the status must not show "Zeit für heute um". The "id" texts of another lock say "waktu berhenti" (time stopped), not "Dijeda" (paused).
