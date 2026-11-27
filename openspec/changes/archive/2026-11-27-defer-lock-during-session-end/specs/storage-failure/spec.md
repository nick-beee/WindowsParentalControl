# Spec Delta

## MODIFIED Requirements

### Requirement: Storage Lockdown
After 3 ticks in a row in which a write to the database failed, the system SHALL enter the Storage Lockdown. In it, the system SHALL apply the Enforced Workstation Lock to each active session of a user who is not an administrator, at least every 2 seconds. It SHALL never close or kill a program, and SHALL not end or restart its process because of the failed writes. A Lock Deferral delays this lock (capability session-lock); the system locks the session at the next run of the lock loop, within 3 seconds after the end of the deferral.

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
