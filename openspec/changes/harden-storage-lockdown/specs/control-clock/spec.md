## ADDED Requirements

### Requirement: Schedule Window end in the repeated hour
If the end of the Schedule Window is a time of day that occurs two times at the end of daylight saving time, the Schedule Window SHALL end at the first of the two instants. It SHALL stay closed for the rest of that day of the Control Time Zone. This covers the check of each tick, the sign-in and the unlock.

#### Scenario: Window ends at 02:30 on the last Sunday of October
- **WHEN** the Control Time Zone is "W. Europe Standard Time", the Schedule Window ends at 02:30, and the local time goes from 03:00 back to 02:00
- **THEN** the session locks at the first 02:30
- **AND** the child cannot sign in or unlock while the local time goes from 02:00 to 02:30 the second time

#### Scenario: Window end outside the repeated hour
- **WHEN** the Schedule Window ends at 20:00 on the last Sunday of October
- **THEN** the session locks at 20:00 as on each other day
