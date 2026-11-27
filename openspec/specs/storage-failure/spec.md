# storage-failure Specification

## Purpose
Keeps the limits on when the database cannot store data while the service runs, for example on a full disk. No time is lost, no lock is skipped, and the service never closes or kills a program because of it.

## Requirements

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
- **WHEN** the Session Agent reports a sample of a Games exe while the database cannot write
- **THEN** the seconds of the sample stay in the Pending Usage and are not lost

### Requirement: A failed write never skips a lock
The system SHALL run the limit checks and the Schedule Window check of each tick also when a write of the tick fails. In each path that locks a session or suspends a process, the system SHALL call the lock before any write of that path. A failed write of an event, a log line or a notification SHALL not stop the lock.

#### Scenario: Limit ends on a full disk
- **WHEN** the Allowance Time of a Restricted User ends while the database cannot write
- **THEN** the system applies the Enforced Workstation Lock in the same tick

#### Scenario: Schedule Window ends on a full disk
- **WHEN** the Schedule Window ends while the database cannot write
- **THEN** the system applies the Enforced Workstation Lock in the same tick

### Requirement: No suspension without a record
The system SHALL suspend a process only after it stored the record of the suspension. If the write of the record fails, it SHALL not suspend the process, and the tick SHALL count as a failed tick.

#### Scenario: Category limit ends on a full disk
- **WHEN** the Category Limit of Games ends while the database cannot write
- **THEN** the system does not suspend the game, and the Storage Lockdown starts at the latest after the third failed tick in a row

### Requirement: Storage Lockdown
After 3 ticks in a row in which a write to the database failed, the system SHALL enter the Storage Lockdown. In it, the system SHALL apply the Enforced Workstation Lock to each active session of a user who is not an administrator, at least every 2 seconds. It SHALL never close or kill a program, and SHALL not end or restart its process because of the failed writes. A Lock Deferral delays this lock; the lock then comes within 3 seconds after the deferral ends.

#### Scenario: Child fills the disk
- **WHEN** the Restricted User fills the system disk from its profile, so each write to the database fails
- **THEN** within 3 ticks the system locks the session of the Restricted User, and the session locks again within 2 seconds after each unlock
- **AND** each program of the child stays open

#### Scenario: Any write error counts
- **WHEN** the writes fail for another reason than a full disk, for example a locked database file, in 3 ticks in a row
- **THEN** the system enters the Storage Lockdown

#### Scenario: Administrator during the Storage Lockdown
- **WHEN** an administrator signs in during the Storage Lockdown
- **THEN** the system does not lock the session of the administrator

#### Scenario: Sign-out during the Storage Lockdown
- **WHEN** the Storage Lockdown runs, the child unlocks the session and starts a sign-out with unsaved text in Notepad before the next lock of the loop
- **THEN** the system does not lock the session for 75 seconds, and Notepad can show its save dialog
- **AND** if the session still exists after the deferral, the system locks it within 3 seconds

### Requirement: End of the Storage Lockdown
In the Storage Lockdown, the system SHALL try a write to the database every minute. It SHALL leave the Storage Lockdown when the write and the store of all Pending Usage succeed and the volume of the data folder has at least 100 MB free space. Then it SHALL enforce as normal in the same process.

#### Scenario: Parent frees space
- **WHEN** the parent deletes the large file of the child, so the volume has 5 GB free space
- **THEN** within 60 seconds the system stores the Pending Usage, leaves the Storage Lockdown, and enforces the limits as normal

#### Scenario: Write succeeds but the disk is almost full
- **WHEN** a write succeeds but the volume has less than 100 MB free space
- **THEN** the system stays in the Storage Lockdown

### Requirement: Report the Storage Lockdown
The system SHALL write a Windows event log entry at each entry into and each exit from the Storage Lockdown. At the first entry on a day of the Control Time Zone, it SHALL send a Telegram alert to the parent that tells the parent to free space as an administrator. The alert and its state SHALL not need a write to the database.

#### Scenario: First Storage Lockdown of the day
- **WHEN** the system enters the Storage Lockdown for the first time today and Telegram is configured
- **THEN** the parent gets one Telegram alert, and the event log shows the entry

#### Scenario: Second Storage Lockdown on the same day
- **WHEN** the system leaves the Storage Lockdown and enters it again on the same day
- **THEN** the event log shows the second entry and the parent gets no second alert

#### Scenario: No network at the entry
- **WHEN** the Telegram send fails at the entry
- **THEN** the system keeps the alert in memory and tries to send it again every minute while the service runs
