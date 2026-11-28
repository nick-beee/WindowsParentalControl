# Proposal

## Why

After a Break ended at the end of the Schedule Window, the widget of the Session Agent still showed "⏸ Gesperrt (Pause)" (issue #44, found in the VM checks of PR #31). The lock itself was correct: the Schedule Window lock applied. The cause is in the widget: it has one text for each lock, "⏸ Gesperrt (Pause)", and the tray tooltip has "Parental Control: Gesperrt (Pause)". The status answer of the Agent Channel sends only `isLocked`, not the reason of the lock. So the child reads "Pause" as a Break after each lock: the Schedule Window, the Allowance Time, a Break or another lock. The specs say nothing about this text.

## What Changes

- The service decides the Lock Reason for each status answer from its own state at that time: the Schedule Window, the Allowance Time, a running Break, or another lock. It does not keep the reason from the time of the lock. The priority is Schedule Window, then Allowance Time, then Break, then other (design D1).
- The status answer of the Agent Channel gets the field `lockReason` (`schedule`, `allowance`, `break`, `other`, or null if the session is not locked).
- The widget and the tray tooltip show one text for each Lock Reason. Only the Break text says "Pause". The text for another lock, and for a status without `lockReason`, is "⏸ Gesperrt (Zeit läuft nicht)".
- No Break starts in a tick after the Allowance Time or the Schedule Window already ended, also if a Lock Deferral keeps the session unlocked for a short time (design D3).
- Decided by the orchestrator under the user's delegation (2026-11-28): the priority Schedule Window > Allowance Time > Break > other, computed live in pure code (`LockReasons`). Rejected: a reason stored at the time of the lock. See design.md.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `child-messages`: two new requirements, "The widget shows the Lock Reason" and "The service decides the Lock Reason live".
- `usage-breaks`: the requirement "Allowance Time and Schedule Window come first" also covers a tick after the end of the Allowance Time or the Schedule Window.

## Out of scope

- The end time of a Break in the widget ("Pause bis 15:10"). The session message already says it.
- The text of the Request Window and of the child request page (`session_locked`).
- A change of the lock itself, of the session messages, or of the end of a Break. The Break state already ends at the Schedule Window end (spec `usage-breaks`).
- A Break that starts before the start of the Schedule Window. The Schedule Window lock covers that time.
- The Lock Reason in the web admin or in Telegram.

## Impact

- `src/ParentalControl.Domain/Sessions/LockReason.cs`: the new pure type `LockReasons` (resolve, wire name, text keys), with xUnit tests.
- `src/ParentalControl.Domain/Sessions/AgentStatus.cs`: the new field `lockReason`.
- `src/ParentalControl.Domain/Messages/AgentTexts.cs`: texts for each Lock Reason in "de", "en" and "id".
- `src/ParentalControl.Domain/Breaks/BreakPlanner.cs`: no Break start when `MinutesUntilOtherLock` is 0.
- `src/ParentalControl.Service/AgentRequests.cs`: gets `BreakEnforcer` and sets `lockReason`.
- `src/ParentalControl.Agent`: `AgentStatusResponse.lockReason`, the widget and the tray tooltip.
- No change of the database, the settings, the web admin or the Admin app. An older agent ignores the new field. A newer agent with an older service shows the text for another lock.
