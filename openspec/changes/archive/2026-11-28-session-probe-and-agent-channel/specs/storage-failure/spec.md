## MODIFIED Requirements

### Requirement: Pending Usage
If the system cannot store counted Allowance Time or Category Usage, it SHALL keep the time in memory as Pending Usage for the user, the day and the category. It SHALL store the Pending Usage at the next successful write. Each check of a limit and each remaining time that the system shows SHALL use the stored time plus the Pending Usage.

#### Scenario: Disk full while the child plays
- **WHEN** the database cannot write for 2 ticks while a Restricted User plays a game
- **THEN** the Allowance Time and the Category Usage for Games still increase by the real minutes
- **AND** the Session Agent shows the remaining time with these minutes

#### Scenario: Write works again
- **WHEN** a write succeeds after 2 failed ticks
- **THEN** the database has the minutes of all 3 ticks, and no minute counts two times

#### Scenario: Agent sample during a failure
- **WHEN** the Session Probe reports a sample with a Games exe as a counted process while the database cannot write
- **THEN** the seconds of the sample stay in the Pending Usage and are not lost
