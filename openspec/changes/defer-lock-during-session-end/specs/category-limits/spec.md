# Spec Delta

## MODIFIED Requirements

### Requirement: Fallback when a process cannot be suspended
If the system cannot suspend a process that it must suspend (for example, anti-cheat software blocks it), it SHALL lock the session with the Enforced Workstation Lock and SHALL notify the parent through the Alert Channel. It SHALL NOT close or kill the process. A Lock Deferral delays this lock (capability session-lock).

#### Scenario: Suspension refused
- **WHEN** Games is exhausted and the system gets "access denied" when it tries to suspend a game process
- **THEN** the session is locked
- **AND** the parent gets an alert that names the exe
- **AND** the game process still exists

#### Scenario: Unlock while the process still cannot be suspended
- **WHEN** the session is locked because of this fallback and the child unlocks it on the same day while Games is still exhausted
- **THEN** the system tries the suspension again and locks the session again if it fails
