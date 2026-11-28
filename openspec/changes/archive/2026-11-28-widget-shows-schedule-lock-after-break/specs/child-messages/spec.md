# Spec Delta

## ADDED Requirements

### Requirement: The widget shows the Lock Reason
While the session of the Restricted User is locked, the widget and the tray tooltip of the Session Agent SHALL show the text of the Lock Reason in the Language Preset. Only the text of a Break SHALL say "Pause". A locked session without a known reason in the status answer SHALL show the text of another lock.

#### Scenario: Break ends at the Schedule Window end
- **WHEN** the preset is "de", the Schedule Window ends at 20:00, a Break of 10 minutes starts at 19:55, and the session stays locked after 20:00
- **THEN** after 20:00 the widget shows "🌙 Gesperrt (keine Computerzeit)" and not "⏸ Gesperrt (Pause)"

#### Scenario: Running Break
- **WHEN** the preset is "de" and a Break runs inside the Schedule Window with Allowance Time left
- **THEN** the widget shows "⏸ Gesperrt (Pause)"

#### Scenario: Allowance Time ends during a Break
- **WHEN** the preset is "de", a Break runs, and the parent lowers the daily limit below the used minutes
- **THEN** the widget shows "⏸ Gesperrt (Zeit für heute um)"

#### Scenario: Other lock
- **WHEN** the preset is "de" and the child locks the session inside the Schedule Window with Allowance Time left and no Break
- **THEN** the widget shows "⏸ Gesperrt (Zeit läuft nicht)"

### Requirement: The service decides the Lock Reason live
The status answer of the Agent Channel SHALL give the Lock Reason of a locked session. The service SHALL decide it from its own state at the time of each status answer, never from the state at the time of the lock and never from data of the Session Agent. The priority SHALL be: outside the Schedule Window, then no Allowance Time left, then a running Break, then another lock.

#### Scenario: Reason after the end of a Break
- **WHEN** a Break locked the session at 19:55 and the Schedule Window ended at 20:00
- **THEN** a status answer at 20:01 gives the Lock Reason of the Schedule Window, not of the Break

#### Scenario: Unlocked session
- **WHEN** the session is unlocked outside the Schedule Window during a Lock Deferral
- **THEN** the status answer gives no Lock Reason
