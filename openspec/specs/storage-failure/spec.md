# storage-failure Specification

## Purpose
Keeps the limits on when the database cannot store data while the service runs, for example on a full disk. No time is lost, no lock is skipped, and the service never closes or kills a program because of it.

## Requirements

### Requirement: Pending Usage
If the system cannot store counted Allowance Time or Category Usage, it SHALL keep the time in memory as Pending Usage for the user, the day and the category. It SHALL store the Pending Usage at the next successful write. Each check of a limit, and each remaining time that the service and the Session Agent show, SHALL use the stored time plus the Pending Usage. A store that runs during this read SHALL not make a minute count two times or not at all.

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

#### Scenario: Store during a limit check
- **WHEN** a sample of the Session Probe stores the Pending Usage while the tick reads the stored Allowance Time for a limit check
- **THEN** the limit check counts each minute one time

#### Scenario: Admin app during a failure
- **WHEN** the parent opens the Admin app while minutes are in the Pending Usage
- **THEN** the Admin app shows the stored time only, because it reads the database and not the memory of the service

### Requirement: A failed write never skips a lock
The system SHALL run the limit checks and the Schedule Window check of each tick also when a write of the tick fails. In each path that locks a session or suspends a process, the system SHALL call the lock before any write of that path. A failed write of an event, a log line or a notification SHALL not stop the lock.

#### Scenario: Limit ends on a full disk
- **WHEN** the Allowance Time of a Restricted User ends while the database cannot write
- **THEN** the system applies the Enforced Workstation Lock in the same tick

#### Scenario: Schedule Window ends on a full disk
- **WHEN** the Schedule Window ends while the database cannot write
- **THEN** the system applies the Enforced Workstation Lock in the same tick

### Requirement: No suspension without a record
The system SHALL suspend a process only after it stored the record of the suspension. If the write of the record fails, it SHALL not suspend the process. In place of the suspension, it SHALL apply the Enforced Workstation Lock to the session of the process, and the tick SHALL count as a failed tick. It SHALL never close or kill the process because of the failed write.

#### Scenario: Category limit ends on a full disk
- **WHEN** the Category Limit of Games ends while the database cannot write
- **THEN** the system does not suspend the game, and it locks the session of the child at the moment it would suspend the game
- **AND** the game stays open

#### Scenario: Unlock while the record still cannot be written
- **WHEN** the child unlocks the session, and the write of the record still fails
- **THEN** the system locks the session again within 15 seconds after the unlock

#### Scenario: Write works again
- **WHEN** the child unlocks the session after the database can write again
- **THEN** the system stores the record and suspends the game, and it does not lock the session for the game

### Requirement: Storage Lockdown
After 3 failed ticks within 10 minutes, the system SHALL enter the Storage Lockdown. In it, the system SHALL apply the Enforced Workstation Lock to each active session of a user who is not an administrator, at least every 2 seconds. It SHALL never close or kill a program, and SHALL not end or restart its process because of the failures. A Lock Deferral delays this lock; the lock then comes within 3 seconds after the deferral ends.

#### Scenario: Child fills the disk
- **WHEN** the Restricted User fills the system disk from its profile, so each write to the database fails
- **THEN** within 3 ticks the system locks the session of the Restricted User, and the session locks again within 2 seconds after each unlock
- **AND** each program of the child stays open

#### Scenario: Child fills and frees the disk in turns
- **WHEN** the writes fail in the ticks 1, 3 and 5 and succeed in the ticks 2 and 4
- **THEN** the system enters the Storage Lockdown in tick 5

#### Scenario: Any write error counts
- **WHEN** the writes fail for another reason than a full disk, for example a locked database file, in 3 ticks within 10 minutes
- **THEN** the system enters the Storage Lockdown

#### Scenario: A failed limit check counts
- **WHEN** the limit check throws an error that is not a database error in 3 ticks within 10 minutes
- **THEN** the system enters the Storage Lockdown, because a limit that the system cannot check must not give the child free time

#### Scenario: Administrator during the Storage Lockdown
- **WHEN** an administrator signs in during the Storage Lockdown
- **THEN** the system does not lock the session of the administrator

#### Scenario: Sign-out during the Storage Lockdown
- **WHEN** the Storage Lockdown runs, the child unlocks the session and starts a sign-out with unsaved text in Notepad before the next lock of the loop
- **THEN** the system does not lock the session for 75 seconds, and Notepad can show its save dialog
- **AND** if the session still exists after the deferral, the system locks it within 3 seconds

### Requirement: End of the Storage Lockdown
In the Storage Lockdown, the system SHALL try a write to the database every minute, and SHALL run the reads of the limit check also for each locked session. It SHALL leave the Storage Lockdown when the write and the store of all Pending Usage succeed, no limit check of the tick failed, and the volume of the data folder has at least 100 MB free space. Then it SHALL enforce as normal in the same process.

#### Scenario: Parent frees space
- **WHEN** the parent deletes the large file of the child, so the volume has 5 GB free space
- **THEN** within 60 seconds the system stores the Pending Usage, leaves the Storage Lockdown, and enforces the limits as normal

#### Scenario: Write succeeds but the disk is almost full
- **WHEN** a write succeeds but the volume has less than 100 MB free space
- **THEN** the system stays in the Storage Lockdown

#### Scenario: Exactly 100 MB free
- **WHEN** a write succeeds and the volume has exactly 100 MB free space
- **THEN** the system leaves the Storage Lockdown

#### Scenario: The limit check still fails
- **WHEN** the writes succeed, but the limit check throws an error in each tick, also for the locked session of the child
- **THEN** the system stays in the Storage Lockdown

### Requirement: Report the Storage Lockdown
The system SHALL write a Windows event log entry at each entry into and exit from the Storage Lockdown. The entry and the alert SHALL name the failed operation and the cause, and SHALL say "disk is full" only for a full disk. At the first entry of a day of the Control Time Zone, and at a later entry that day with a known cause of another kind, it SHALL send a Telegram alert to the parent, without a write to the database.

#### Scenario: First Storage Lockdown of the day
- **WHEN** the system enters the Storage Lockdown for the first time today and Telegram is configured
- **THEN** the parent gets one Telegram alert, and the event log shows the entry

#### Scenario: Second Storage Lockdown on the same day
- **WHEN** the system leaves the Storage Lockdown and enters it again on the same day with a cause of the same kind, or with an unknown cause
- **THEN** the event log shows the second entry and the parent gets no second alert

#### Scenario: Another cause on the same day
- **WHEN** the first entry of the day had the cause "another program locks the database", and a later entry on the same day has the cause "the disk is full"
- **THEN** the parent gets a second alert that says that the disk is full

#### Scenario: No network at the entry
- **WHEN** the Telegram send fails at the entry
- **THEN** the system keeps the alert in memory and tries to send it again every minute while the service runs

#### Scenario: Disk full
- **WHEN** the system enters the Storage Lockdown because SQLite reports a full disk
- **THEN** the event and the alert say that the disk is full and tell the parent to free space as an administrator

#### Scenario: Another cause with low free space
- **WHEN** the system enters the Storage Lockdown with a cause that is not a full disk, and the volume of the data folder has less than 100 MB free space or the free space is not known
- **THEN** the event and the alert tell the parent to free space to at least 100 MB, because the exit needs it, and do not say that the disk is full or that the cause is not a full disk

#### Scenario: Error that is not a database error
- **WHEN** the system enters the Storage Lockdown because the limit check throws an error that is not a database error
- **THEN** the event and the alert name the limit check and an internal error, and do not say that the disk is full
- **AND** the alert tells the parent to read event 1015 and the service log

#### Scenario: No data of a program in the alert
- **WHEN** the write of the suspension record of a program of the child is the last failure
- **THEN** the event and the alert name the operation "suspension record" without the path or the name of the program

### Requirement: Failed tick
A tick SHALL count as a failed tick if a write to the database failed since the last tick, or if the count of the session time or a limit check of the tick could not run, for any reason. A limit check is the check of the Allowance Time, the Schedule Window, a Category Limit or an App Limit. A successful tick SHALL not clear the failed ticks of the last 10 minutes. The exit from the Storage Lockdown SHALL clear them.

#### Scenario: One good tick between failures
- **WHEN** the writes fail in 2 ticks, succeed in the next tick, and fail again in the tick after it, all within 10 minutes
- **THEN** the system enters the Storage Lockdown in the fourth tick

#### Scenario: A failed category check counts
- **WHEN** the check of the Category Limits throws an error in 3 ticks within 10 minutes, for example because a stored suspension record cannot be read
- **THEN** the system enters the Storage Lockdown, and the event and the alert name the operation "category check"

#### Scenario: Failures far apart
- **WHEN** a write fails in one tick every 6 minutes
- **THEN** the system does not enter the Storage Lockdown

### Requirement: Storage check at the start
In the first tick after the start of the service, the system SHALL write the probe to the database. If that probe write fails, the system SHALL enter the Storage Lockdown in that tick, without more failed ticks.

#### Scenario: Restart on a full disk
- **WHEN** the PC restarts while each write to the database fails
- **THEN** the system enters the Storage Lockdown in the first tick after the start, and the session of the child locks

#### Scenario: Start with a working database
- **WHEN** the service starts and the probe write succeeds
- **THEN** the system does not enter the Storage Lockdown at the start
