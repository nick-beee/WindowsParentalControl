# usage-breaks Specification

## Purpose
Gives a Restricted User a Break of M minutes after N minutes of Continuous Use. A Break locks the session and keeps all programs open.

## Requirements

### Requirement: One Break Rule per Restricted User
Each Restricted User SHALL have one Break Rule for the whole session: on or off, N minutes of use (10 to 240) and M minutes of break (5 to 120). A Restricted User without a stored Break Rule SHALL get the default rule: on, 45 minutes of use, 10 minutes of break. An Administrator or an unrestricted user SHALL get no Break.

#### Scenario: New install
- **WHEN** the service starts with a new database and the parent marks the account `Kid` as a Restricted User
- **THEN** `Kid` has the Break Rule "on, 45 minutes of use, 10 minutes of break"

#### Scenario: Update of an earlier install
- **WHEN** an install without break tables gets this version and has the Restricted User `Kid`
- **THEN** `Kid` has the Break Rule "on, 45 minutes of use, 10 minutes of break"

#### Scenario: Administrator
- **WHEN** an Administrator uses the computer for 3 hours without a lock
- **THEN** the Administrator gets no break warning and no Break

### Requirement: Count Continuous Use in the service
The system SHALL count Continuous Use per Restricted User in whole minutes. It SHALL count the same unlocked session minutes that it adds to the Allowance Time, and nothing more. It SHALL NOT use data from the Session Agent for this count. A locked or disconnected session, a signed-out user and a sleeping PC SHALL add no Continuous Use.

#### Scenario: Unlocked use
- **WHEN** the Restricted User uses an unlocked session for 30 minutes without a lock
- **THEN** Continuous Use is 30 minutes

#### Scenario: Agent samples do not count
- **WHEN** a process of the child sends false Foreground Window samples to `POST /api/agent/activity`
- **THEN** Continuous Use does not change because of these samples

#### Scenario: Locked session
- **WHEN** the session is locked for 5 minutes
- **THEN** Continuous Use does not increase in these 5 minutes

### Requirement: A Rest resets Continuous Use
A Rest is a time without use: the session is locked or disconnected, the user is signed out, or the PC sleeps or is off. When a Rest of at least M minutes ends, the system SHALL set Continuous Use to 0. A shorter Rest SHALL keep the value. A lock of a session that is already locked SHALL NOT move the start of a Rest.

#### Scenario: Lock for 12 minutes
- **WHEN** the Break Rule has M = 10, Continuous Use is 30 minutes, and the child locks the session for 12 minutes
- **THEN** Continuous Use is 0 after the unlock

#### Scenario: Rest one minute too short
- **WHEN** the Break Rule has M = 10, Continuous Use is 30 minutes, and the child locks the session for 9 minutes
- **THEN** Continuous Use is still 30 minutes after the unlock

#### Scenario: Sign-out and sign-in
- **WHEN** Continuous Use is 40 minutes and the child signs out and signs in again 15 minutes later, with M = 10
- **THEN** Continuous Use is 0

#### Scenario: PC off overnight
- **WHEN** the child used the PC for 40 minutes in the evening and starts it again the next morning
- **THEN** Continuous Use is 0

### Requirement: Rest at a service start
A time while the service did not run and the session of the user was unlocked SHALL count neither as use nor as a Rest. If the PC started after the last use, the time from the last use SHALL be a Rest, also if the session is already unlocked at the service start.

#### Scenario: Sign-in before the service runs
- **WHEN** the child used the PC for 40 minutes, the PC was off for 30 minutes, and the child signs in before the service starts, with M = 10
- **THEN** Continuous Use is 0

#### Scenario: Service restart while the child plays
- **WHEN** Continuous Use is 30 minutes and the service restarts after 15 minutes of down time, while the session of the child stays unlocked
- **THEN** Continuous Use is still 30 minutes

### Requirement: Warnings before a Break
The system SHALL send a session message to the child when 5 minutes and when 1 minute of Continuous Use remain before a Break. It SHALL send each warning at most one time per Break. It SHALL NOT send a break warning when the Allowance Time or the Schedule Window ends at the same time as the Break starts or before it.

#### Scenario: Five minutes before a Break
- **WHEN** the Break Rule is 45 minutes of use and Continuous Use reaches 40 minutes
- **THEN** the child gets one message that the Break starts in 5 minutes

#### Scenario: Allowance Time ends first
- **WHEN** Continuous Use is 40 of 45 minutes and the Allowance Time ends in 3 minutes
- **THEN** the child gets no break warning

### Requirement: Start a Break with the Enforced Workstation Lock
When Continuous Use reaches N minutes, the system SHALL start a Break within 60 seconds. It SHALL send a session message that says the length of the Break and the local time of its end, and then lock the session with the Enforced Workstation Lock. The system SHALL NOT close, kill or suspend a program because of a Break.

#### Scenario: Break after 45 minutes
- **WHEN** the Break Rule is "45 minutes of use, 10 minutes of break" and Continuous Use reaches 45 minutes at 15:00
- **THEN** the child gets a message that the Break lasts 10 minutes, until 15:10
- **AND** the session is locked

#### Scenario: Game stays open
- **WHEN** a Steam game and a Study program run and a Break starts
- **THEN** after the Break, both programs still run with the same process IDs

### Requirement: Keep the session locked during a Break
Until a Break ends, the system SHALL lock the session again after each unlock and after each sign-in of the Restricted User. It SHALL first send a message with the local time of the end of the Break and the remaining minutes, at most one time in 10 seconds. It SHALL NOT sign the child out. It SHALL also lock the session within 60 seconds if a tick finds the session unlocked during a Break.

#### Scenario: Unlock during a Break
- **WHEN** a Break runs until 15:10 and the child unlocks the session at 15:04
- **THEN** the child gets a message that the Break lasts until 15:10
- **AND** the session is locked again

#### Scenario: Sign-in during a Break
- **WHEN** a Break runs and the child signs out and signs in again before the Break ends
- **THEN** the session is locked after the sign-in
- **AND** the system does not sign the child out

### Requirement: End of a Break
At the end time of a Break, the system SHALL end the Break and SHALL set Continuous Use to 0. After that, an unlock SHALL work as before the Break. The system SHALL NOT unlock the session itself.

#### Scenario: Unlock after the Break
- **WHEN** a Break ended at 15:10 and the child unlocks the session at 15:12
- **THEN** the session stays unlocked and Continuous Use is 0

### Requirement: A Break uses no Allowance Time
During a Break, the Allowance Time and the Category Usage SHALL NOT increase. The Break SHALL NOT change the daily Allowance Time or a Category Limit.

#### Scenario: Ten minutes of Break
- **WHEN** the child has used 60 of 120 minutes of Allowance Time and a Break of 10 minutes starts
- **THEN** after the Break, the child has used 60 of 120 minutes

### Requirement: A Break ends at the Schedule Window end
A Break SHALL end at the earlier of two times: the start of the Break plus M minutes, or the end of today's Schedule Window. After the Schedule Window end, the lock of the Schedule Window SHALL apply as before. A Break SHALL NOT continue into the next Schedule Window.

#### Scenario: Break near the end of the evening
- **WHEN** the Schedule Window ends at 20:00 and a Break of 10 minutes starts at 19:55
- **THEN** the Break ends at 20:00
- **AND** the session stays locked because the Schedule Window ended

#### Scenario: Next morning
- **WHEN** a Break ended at the Schedule Window end yesterday and the child signs in this morning in the Schedule Window
- **THEN** there is no Break and Continuous Use is 0

### Requirement: Allowance Time and Schedule Window come first
The system SHALL check the end of the Allowance Time and of the Schedule Window before a Break. If one of them locks the session in a tick, no Break SHALL start in that tick, and the child SHALL get only the message of the Allowance Time or of the Schedule Window.

#### Scenario: Allowance Time and Break end together
- **WHEN** Continuous Use reaches N minutes in the same tick in which the Allowance Time ends
- **THEN** the child gets the message that the daily screen time is over
- **AND** no Break starts and the child gets no break message

### Requirement: Bonus Minutes do not skip a Break
Bonus Minutes SHALL add to the Allowance Time only. They SHALL NOT change Continuous Use, SHALL NOT end a running Break and SHALL NOT move the next Break.

#### Scenario: Bonus during a Break
- **WHEN** a Break runs until 15:10 and the parent approves 15 Bonus Minutes at 15:05
- **THEN** the Break still ends at 15:10

#### Scenario: Bonus before a Break
- **WHEN** Continuous Use is 44 of 45 minutes and the parent approves 30 Bonus Minutes
- **THEN** the Break starts within 60 seconds after Continuous Use reaches 45 minutes

### Requirement: The parent ends a running Break
The parent SHALL be able to end a running Break from the Remote Admin Interface and from Telegram. Then the system SHALL end the Break at once and SHALL set Continuous Use to 0. The session SHALL stay locked until the child unlocks it, and the unlock SHALL work.

#### Scenario: End a Break from the web admin
- **WHEN** a Break of `Kid` runs and the parent sends `POST /api/admin/breaks/{userId}/end` with a valid Admin Session
- **THEN** the Break ends and the child can unlock the session

#### Scenario: End a Break from Telegram
- **WHEN** a Break of `Kid` runs and the parent sends `/break` in the configured Telegram chat and taps the button to end the Break of `Kid`
- **THEN** the Break ends and the parent gets a confirmation

### Requirement: An old Telegram button does not end a later Break
A Telegram button to end a Break SHALL end only the Break for which the system made the button. The system SHALL accept the button only from the configured Telegram chat.

#### Scenario: Old button
- **WHEN** the parent taps a button of a Break that already ended, while a later Break runs
- **THEN** the later Break continues and the parent gets a message that the Break of the button already ended

#### Scenario: Other chat
- **WHEN** a Telegram user in another chat sends the callback of the button
- **THEN** the Break continues

### Requirement: Change of the Break Rule
When the parent turns the Break Rule off, the system SHALL stop the count and SHALL end a running Break at once. When the parent turns it on, Continuous Use SHALL start at 0. A change of N or M SHALL not change the end of a running Break. After a change of N, Continuous Use SHALL be at most N − 5, so that the 5-minute warning comes before the Break.

#### Scenario: Rule off during a Break
- **WHEN** a Break runs and the parent turns the Break Rule off
- **THEN** the Break ends and no new Break starts

#### Scenario: Shorter N
- **WHEN** Continuous Use is 40 minutes and the parent changes N from 60 to 30
- **THEN** Continuous Use is 25 minutes and the child gets the 5-minute warning
- **AND** the Break starts 5 minutes later

### Requirement: Break state survives a service restart
The system SHALL store the Break Rule, Continuous Use, the time of the last use, the end time of a running Break and the warnings sent. After a service start, a running Break SHALL continue until its stored end time, and the system SHALL lock an unlocked session of that user within 60 seconds.

#### Scenario: Restart during a Break
- **WHEN** a Break runs until 15:10 and the service restarts at 15:04
- **THEN** the session stays locked until 15:10 and an unlock before 15:10 is locked again

#### Scenario: Restart before a Break
- **WHEN** Continuous Use is 30 minutes and the service restarts at once
- **THEN** Continuous Use is 30 minutes after the restart

### Requirement: Admin API for Breaks
The system SHALL have the routes `GET /api/admin/breaks` (Break Rule and state of each Restricted User), `PUT /api/admin/breaks/{userId}` (`enabled`, `useMinutes`, `breakMinutes`) and `POST /api/admin/breaks/{userId}/end`. Each route SHALL require an Admin Session. A value out of range SHALL give status 400 and no change. A user that is not a Restricted User SHALL give 404.

#### Scenario: Read the rules
- **WHEN** the parent sends `GET /api/admin/breaks` with a valid Admin Session
- **THEN** the response lists `Kid` with `enabled`, `useMinutes`, `breakMinutes`, `continuousUseMinutes` and `breakEndsAt` (null when no Break runs)

#### Scenario: Value out of range
- **WHEN** the parent sends `PUT /api/admin/breaks/{userId}` with `useMinutes` 5
- **THEN** the system answers with status 400 and keeps the old Break Rule

#### Scenario: Child ends its own Break
- **WHEN** a process of the Restricted User sends `POST /api/admin/breaks/{userId}/end` to `127.0.0.1:5050` without an Admin Session
- **THEN** the system answers with status 401 and the Break continues

#### Scenario: No Break runs
- **WHEN** the parent sends `POST /api/admin/breaks/{userId}/end` and no Break of that user runs
- **THEN** the system answers with status 409 and changes nothing

### Requirement: Admin page for Breaks
The web admin SHALL show for each Restricted User the Break Rule (on or off, N, M) with fields that the parent can change, and the state: the Continuous Use, or the end time of a running Break. For a running Break, it SHALL show a button that ends the Break. The settings of the web admin SHALL include `telegram_notify_break` and the three break texts.

#### Scenario: Parent changes the rule on the page
- **WHEN** the parent sets N to 30 and M to 5 for `Kid` on the page and saves
- **THEN** the page sends `PUT /api/admin/breaks/{userId}` with the three fields, and the system stores the new Break Rule

#### Scenario: Parent ends a Break on the page
- **WHEN** a Break of `Kid` runs and the parent clicks the end button and confirms
- **THEN** the page sends `POST /api/admin/breaks/{userId}/end`, the Break ends, and the session stays locked until the child unlocks it

### Requirement: Break texts follow the Language Preset
The child message keys `msg_break_warn`, `msg_break_start` and `msg_break_running` SHALL have a default text in each Language Preset ("de", "en", "id") with the same placeholders. The parent SHALL be able to edit them like the other child message keys. `{break_end}` SHALL be the local time of the end of the Break in the format `HH:mm`. The title of the three messages SHALL be the fixed session text `break_title` of the Language Preset.

#### Scenario: German warning
- **WHEN** the preset is "de" and 5 minutes remain before a Break
- **THEN** the child gets the message "In 5 Minuten ist Pause. Speichere jetzt, was du gerade machst. Deine Programme bleiben offen."

#### Scenario: German Break start
- **WHEN** the preset is "de" and a Break of 10 minutes starts at 15:00
- **THEN** the child gets the message "Zeit für eine Pause! Der Computer wird jetzt für 10 Minuten gesperrt, bis 15:10 Uhr. Deine Programme bleiben offen."

#### Scenario: German unlock during a Break
- **WHEN** the preset is "de", a Break runs until 15:10, and the child unlocks the session at 15:09
- **THEN** the child gets the message "Die Pause dauert noch bis 15:10 Uhr (1 Minute). Der Computer wird wieder gesperrt."

### Requirement: Telegram notice at a Break start
When the setting `telegram_notify_break` is on and Telegram is configured, the system SHALL send a notice to the parent at each Break start. The notice SHALL name the user and the end time and SHALL have a button that ends this Break. The setting SHALL be off by default.

#### Scenario: Notice on
- **WHEN** `telegram_notify_break` is on and a Break of `Kid` starts at 15:00
- **THEN** the parent gets a Telegram notice that the Break of `Kid` lasts until 15:10, with a button to end it

#### Scenario: Default
- **WHEN** the parent did not change `telegram_notify_break` and a Break starts
- **THEN** the parent gets no Telegram notice
