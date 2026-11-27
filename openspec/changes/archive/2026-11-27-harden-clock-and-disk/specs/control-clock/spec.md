# Spec Delta

## Purpose

Defines the day and the time of day of every limit. They come from a Control Time Zone that only an administrator sets, so a time zone change by the Restricted User does not move the day or the Schedule Window.

## ADDED Requirements

### Requirement: Control Time Zone
The system SHALL keep one Control Time Zone, a Windows time zone ID. Only SYSTEM and the Administrators group SHALL be able to change it. It SHALL survive a restart, an update, and a move or a new creation of the data folder. If it is absent at a start, the service SHALL set it to the system time zone of that moment and SHALL tell the parent once.

#### Scenario: First start after the update
- **WHEN** the service starts for the first time with this change, and the system time zone is "W. Europe Standard Time"
- **THEN** the Control Time Zone is "W. Europe Standard Time"
- **AND** the parent gets one Telegram message that names the Control Time Zone and tells how to change it, if Telegram is configured

#### Scenario: Child tries to change the value
- **WHEN** a process of the Restricted User tries to write the Control Time Zone
- **THEN** Windows denies the write and the value does not change

#### Scenario: New database after a move of the data folder
- **WHEN** the service starts with a new database after it moved an untrusted data folder
- **THEN** the Control Time Zone is the same as before the move

### Requirement: Day and time of day of the limits
The system SHALL take each day, weekday and time of day that a limit, a quota, a request or a usage row uses from the Control Time Zone, with its daylight saving rules. This covers the Allowance Time, the Schedule Window, Schedule Exceptions, Category Usage, Category Limits, App Usage, Grace Requests, Schedule Change Requests, the end time of a Break and the capture quota. The system time zone SHALL have no effect on them.

#### Scenario: Time zone change and reboot
- **WHEN** the Control Time Zone is "W. Europe Standard Time", the Restricted User used all Allowance Time today at 20:00 local time, sets the system time zone to "Tokyo Standard Time" with `tzutil /s`, and restarts the PC
- **THEN** the day stays the same day, the Allowance Time stays used up, and the session locks at the sign-in

#### Scenario: Schedule Window after a time zone change
- **WHEN** the Schedule Window ends at 20:00 in the Control Time Zone and the child sets a system time zone that is 3 hours behind
- **THEN** the session locks at 20:00 in the Control Time Zone

#### Scenario: Admin app shows the same day
- **WHEN** the system time zone differs from the Control Time Zone and the parent opens the Admin app
- **THEN** the Admin app shows the usage of the same day as the service

### Requirement: Elapsed time does not depend on the time zone
The system SHALL count elapsed session time from UTC and the monotonic clock, not from the local time. A change of the offset of the Control Time Zone or of the system time zone SHALL neither add time nor count as clock tampering.

#### Scenario: End of daylight saving time
- **WHEN** a Restricted User uses the PC while the local time goes from 03:00 back to 02:00 at the end of daylight saving time
- **THEN** the system adds only the real minutes, does not report clock tampering, and does not lock the session for the day

#### Scenario: Start of daylight saving time
- **WHEN** the local time goes from 02:00 to 03:00 at the start of daylight saving time
- **THEN** the system adds only the real minutes and does not report a time gap

### Requirement: Report a time zone change
The system SHALL detect each change of the system time zone and of its daylight saving setting while it runs and across a restart. For each change it SHALL write an event with the old and the new value. It SHALL send a Telegram alert to the parent at most once per day of the Control Time Zone.

#### Scenario: Child changes the time zone
- **WHEN** a process of the Restricted User runs `tzutil /s "Tokyo Standard Time"`
- **THEN** within 60 seconds the system writes an event with the old and the new time zone
- **AND** the parent gets one Telegram alert, if Telegram is configured

#### Scenario: Second change on the same day
- **WHEN** the child changes the time zone a second time on the same day
- **THEN** the system writes a second event and sends no second alert

#### Scenario: Change before a reboot
- **WHEN** the child changes the time zone and restarts the PC before the next tick
- **THEN** the system writes the event at its next start

#### Scenario: Daylight saving setting
- **WHEN** the child turns off "Adjust for daylight saving time automatically"
- **THEN** the system writes an event, and the day and the time of day of the limits do not change

### Requirement: Parent changes the Control Time Zone
The Admin app SHALL show the Control Time Zone and the current system time zone, and SHALL let the parent select another Windows time zone. The Admin app SHALL show a warning while the two differ. The service SHALL use a new value within 60 seconds. An invalid value SHALL not be saved.

#### Scenario: Family moves
- **WHEN** the parent selects "GMT Standard Time" in the Admin app
- **THEN** the service uses "GMT Standard Time" for the day and the Schedule Window within 60 seconds

#### Scenario: Warning after a change by the child
- **WHEN** the system time zone differs from the Control Time Zone and the parent opens the Admin app
- **THEN** the Admin app shows both time zones and a warning

### Requirement: Unknown Control Time Zone
If the stored Control Time Zone is not a time zone that Windows knows, the service SHALL use the last valid value that it used in this process. Without one, it SHALL use the system time zone. In both cases it SHALL write an event and alert the parent at most once per day.

#### Scenario: Value removed by a Windows update
- **WHEN** the stored ID does not exist in the time zones of Windows at a start
- **THEN** the service uses the system time zone, writes an event, and alerts the parent
