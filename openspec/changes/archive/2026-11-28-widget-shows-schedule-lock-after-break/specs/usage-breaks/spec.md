# Spec Delta

## MODIFIED Requirements

### Requirement: Allowance Time and Schedule Window come first
The system SHALL check the end of the Allowance Time and of the Schedule Window before a Break. If one of them locks the session in a tick, no Break SHALL start in that tick, and the child SHALL get only the message of the Allowance Time or of the Schedule Window. No Break SHALL start in a tick less than 1 minute before or after the end of the Allowance Time or the Schedule Window, also if a Lock Deferral keeps the session unlocked.

#### Scenario: Allowance Time and Break end together
- **WHEN** Continuous Use reaches N minutes in the same tick in which the Allowance Time ends
- **THEN** the child gets the message that the daily screen time is over
- **AND** no Break starts and the child gets no break message

#### Scenario: Deferred lock at the Schedule Window end
- **WHEN** the Schedule Window ended at 20:00, a Lock Deferral keeps the session unlocked, and Continuous Use reaches N minutes at 20:00
- **THEN** no Break starts and the lock of the Schedule Window applies at the end of the deferral

#### Scenario: Use limit reached in the last minute of the Schedule Window
- **WHEN** the Schedule Window ends at 20:00 and Continuous Use reaches N minutes at 19:59:30
- **THEN** no Break starts and the lock of the Schedule Window applies at 20:00
