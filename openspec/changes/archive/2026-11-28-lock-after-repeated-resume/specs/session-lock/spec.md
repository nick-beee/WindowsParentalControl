## MODIFIED Requirements

### Requirement: Owed lock at the end of the deferral
A lock that becomes due during a Lock Deferral is owed. When the deferral ends by its time and the session still exists, the system SHALL check the session again and SHALL lock it if a lock is still due: within 10 seconds for the Allowance Time, the Schedule Window, a Break and a Resume Lock, and within 20 seconds for the fallback of a suspension. A changed state SHALL apply: a lock that Bonus Minutes made unnecessary SHALL NOT run.

#### Scenario: Sign-out cancelled at the schedule end
- **WHEN** the Schedule Window ends during the sign-out, Notepad asks to save, and the child clicks Cancel
- **THEN** the system locks the session within 10 seconds after the end of the deferral
- **AND** Notepad still runs with its unsaved text

#### Scenario: Bonus Minutes during the deferral
- **WHEN** the lock of the Allowance Time is owed and the parent approves 15 Bonus Minutes before the deferral ends
- **THEN** the system does not lock the session at the end of the deferral

#### Scenario: Fallback owed
- **WHEN** a suspension failed during the deferral and the child cancelled the sign-out, while Games is still exhausted
- **THEN** the system tries the suspension again after a new Pending Suspension and locks the session within 20 seconds after the end of the deferral
- **AND** the parent gets no alert that says the session is locked before the session locks

#### Scenario: Break owed
- **WHEN** a Break started during the deferral and the child cancelled the sign-out
- **THEN** the system sends the message of the running Break and locks the session within 10 seconds after the end of the deferral

#### Scenario: Resume Lock owed
- **WHEN** a Resume Lock of the child runs, the child starts a sign-out, and cancels it
- **THEN** the system does not lock the session during the deferral
- **AND** the system sends the message of the Resume Lock and locks the session within 10 seconds after the end of the deferral

### Requirement: Lock timings with a Lock Deferral
The timings of the Enforced Workstation Lock in the requirements for Breaks, for the Allowance Time, for the Schedule Window, for the fallback of a suspension and for a Resume Lock SHALL apply as before when no Lock Deferral of the session is active. During a deferral, a lock that they require SHALL be owed and SHALL run as the requirement "Owed lock at the end of the deferral" states.

#### Scenario: No sign-out
- **WHEN** Continuous Use reaches N minutes and nobody started a sign-out
- **THEN** the system starts the Break and locks the session as before, within 60 seconds
