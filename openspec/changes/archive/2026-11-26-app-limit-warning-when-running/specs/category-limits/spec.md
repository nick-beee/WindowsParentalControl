## MODIFIED Requirements

### Requirement: Warnings before a Category Limit ends
The system SHALL send a session message to the child when 5 minutes and when 1 minute of a Category Limit or of an App Limit remain. It SHALL send each warning at most one time per limit and day. An App Limit warning SHALL name the app. It SHALL send an App Limit warning only while a process of an exe that the App Limit covers runs in a session of that Restricted User, as the process list of the service shows it, also if that process starts late.

#### Scenario: Five minutes left
- **WHEN** Games usage reaches 115 of 120 minutes
- **THEN** the child gets one message that Games time ends in 5 minutes

#### Scenario: App not running: no warning
- **WHEN** Games has the weekday limit 180, the Roblox rule has the App Limit 120, the Games usage reaches 115 minutes on a Tuesday, and no Roblox process runs in the session of the child
- **THEN** the child gets no message that Roblox ends

#### Scenario: App starts with 3 minutes left: one warning
- **WHEN** the Roblox rule has the App Limit 120, no Roblox process ran while the Games usage went from 115 to 117 minutes, and the child then starts Roblox
- **THEN** the child gets one message that Roblox ends in 3 minutes within 60 seconds
- **AND** the child gets no second 5-minute message for Roblox today

#### Scenario: App closed before the last minute
- **WHEN** the child got the 5-minute message for Roblox, then closes Roblox, and the Games usage reaches 119 of 120 minutes
- **THEN** the child gets no 1-minute message for Roblox

#### Scenario: Category Limit warning without a running program
- **WHEN** Games usage reaches 115 of 120 minutes and no Games program runs
- **THEN** the child gets one message that Games time ends in 5 minutes
