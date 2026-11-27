# Spec Delta

## MODIFIED Requirements

### Requirement: Start a Break with the Enforced Workstation Lock
When Continuous Use reaches N minutes, the system SHALL start a Break within 60 seconds. It SHALL send a session message that says the length of the Break and the time of its end in the Control Time Zone, and then lock the session with the Enforced Workstation Lock. A failed write of the break state or of an event SHALL not stop the lock. The system SHALL NOT close, kill or suspend a program because of a Break. A Lock Deferral delays this lock (capability session-lock).

#### Scenario: Break after 45 minutes
- **WHEN** the Break Rule is "45 minutes of use, 10 minutes of break" and Continuous Use reaches 45 minutes at 15:00
- **THEN** the child gets a message that the Break lasts 10 minutes, until 15:10
- **AND** the session is locked

#### Scenario: Game stays open
- **WHEN** a Steam game and a Study program run and a Break starts
- **THEN** after the Break, both programs still run with the same process IDs

#### Scenario: Break end after a time zone change
- **WHEN** the Control Time Zone is "W. Europe Standard Time", the child set the system time zone to "Tokyo Standard Time", and a Break of 10 minutes starts at 15:00 in the Control Time Zone
- **THEN** the message says that the Break lasts until 15:10

#### Scenario: Break starts on a full disk
- **WHEN** Continuous Use reaches N minutes while the database cannot write
- **THEN** the session is locked in the same tick

### Requirement: Keep the session locked during a Break
Until a Break ends, the system SHALL lock the session again after each unlock and after each sign-in of the Restricted User. It SHALL first send a message with the end of the Break in the Control Time Zone and the remaining minutes, at most one time in 10 seconds. It SHALL NOT sign the child out. It SHALL also lock the session within 60 seconds if a tick finds the session unlocked during a Break. A Lock Deferral delays this lock (capability session-lock).

#### Scenario: Unlock during a Break
- **WHEN** a Break runs until 15:10 and the child unlocks the session at 15:04
- **THEN** the child gets a message that the Break lasts until 15:10
- **AND** the session is locked again

#### Scenario: Sign-in during a Break
- **WHEN** a Break runs and the child signs out and signs in again before the Break ends
- **THEN** the session is locked after the sign-in
- **AND** the system does not sign the child out
