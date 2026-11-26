# Spec Delta

## MODIFIED Requirements

### Requirement: Resume suspended programs
The system SHALL resume a suspended process when its exe has time left again under its limit of the day: on a new day, after new Bonus Minutes, after the parent raises or removes a Category Limit or an App Limit, or after an App Rule change gives the exe a limit with time left. It SHALL resume all processes it suspended when the service stops. A process that the system suspended because its exe is a Blocked Program SHALL stay suspended while the block applies, also if its exe has time left; `blocked-programs` defines when it resumes.

#### Scenario: Next morning
- **WHEN** a game was suspended yesterday and the date changes
- **THEN** the system resumes the game within 60 seconds of the first tick of the new day

#### Scenario: Bonus Minutes approved
- **WHEN** Games is exhausted and the parent approves 15 Bonus Minutes
- **THEN** the system resumes the Games processes within 60 seconds

#### Scenario: Service stop
- **WHEN** the service stops while games are suspended
- **THEN** the system resumes the suspended games before it exits

#### Scenario: Blocked browser on a new day
- **WHEN** the system suspended a blocked Chrome yesterday, the block is still on, and the date changes
- **THEN** Chrome stays suspended

### Requirement: Suspensions survive a service restart
The system SHALL record each process it suspends, with the reason (a limit or a block). After a service start, it SHALL resume each recorded process that still exists and that has no reason to stay suspended: for a limit, its exe has time left; for a block, its exe is no longer a Blocked Program and has time left. It SHALL forget records of processes that no longer exist. It SHALL NOT resume a process that it did not suspend.

#### Scenario: Crash while games are suspended
- **WHEN** the service crashes while a game is suspended and starts again on the same day with Games still exhausted
- **THEN** the game stays suspended and the record stays

#### Scenario: Process ID reused
- **WHEN** a recorded process ended and a new process has the same process ID
- **THEN** the system does not resume or suspend the new process because of the old record

#### Scenario: Crash while a blocked browser is suspended
- **WHEN** the service crashes while a blocked Firefox is suspended and starts again with the block still on and Unknown with time left
- **THEN** Firefox stays suspended and the record stays
