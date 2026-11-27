# Spec Delta

## MODIFIED Requirements

### Requirement: Start a Break with the Enforced Workstation Lock
When Continuous Use reaches N minutes, the system SHALL start a Break within 60 seconds. It SHALL send a session message that says the length of the Break and the time of its end in the Control Time Zone, and then lock the session with the Enforced Workstation Lock. A failed write of the break state or of an event SHALL not stop the lock. The system SHALL NOT close, kill or suspend a program because of a Break.

#### Scenario: Break after 45 minutes
- **WHEN** the Break Rule is "45 minutes of use, 10 minutes of break" and Continuous Use reaches 45 minutes at 15:00
- **THEN** the child gets a message that the Break lasts 10 minutes, until 15:10
- **AND** the session is locked

#### Scenario: Game stays open
- **WHEN** a Steam game and a Study program run and a Break starts
- **THEN** after the Break, both programs still run with the same process IDs

#### Scenario: Break end after a time zone change
- **WHEN** the Control Time Zone is "W. Europe Standard Time", the child set the system time zone to "Tokyo Standard Time", and a Break of 10 minutes starts at 15:00 in the Control Time Zone
- **THEN** the message says that the Break lasts until 15:10

#### Scenario: Break starts on a full disk
- **WHEN** Continuous Use reaches N minutes while the database cannot write
- **THEN** the session is locked in the same tick

### Requirement: Keep the session locked during a Break
Until a Break ends, the system SHALL lock the session again after each unlock and after each sign-in of the Restricted User. It SHALL first send a message with the end of the Break in the Control Time Zone and the remaining minutes, at most one time in 10 seconds. It SHALL NOT sign the child out. It SHALL also lock the session within 60 seconds if a tick finds the session unlocked during a Break.

#### Scenario: Unlock during a Break
- **WHEN** a Break runs until 15:10 and the child unlocks the session at 15:04
- **THEN** the child gets a message that the Break lasts until 15:10
- **AND** the session is locked again

#### Scenario: Sign-in during a Break
- **WHEN** a Break runs and the child signs out and signs in again before the Break ends
- **THEN** the session is locked after the sign-in
- **AND** the system does not sign the child out

### Requirement: A Break ends at the Schedule Window end
A Break SHALL end at the earlier of two times: the start of the Break plus M minutes, or the end of today's Schedule Window in the Control Time Zone. After the Schedule Window end, the lock of the Schedule Window SHALL apply as before. A Break SHALL NOT continue into the next Schedule Window.

#### Scenario: Break near the end of the evening
- **WHEN** the Schedule Window ends at 20:00 and a Break of 10 minutes starts at 19:55
- **THEN** the Break ends at 20:00
- **AND** the session stays locked because the Schedule Window ended

#### Scenario: Next morning
- **WHEN** a Break ended at the Schedule Window end yesterday and the child signs in this morning in the Schedule Window
- **THEN** there is no Break and Continuous Use is 0

#### Scenario: Schedule Window end after a time zone change
- **WHEN** the Schedule Window ends at 20:00 in the Control Time Zone, the child set a system time zone that is 3 hours behind, and a Break of 10 minutes starts at 19:55 in the Control Time Zone
- **THEN** the Break ends at 20:00 in the Control Time Zone

### Requirement: Break texts follow the Language Preset
The child message keys `msg_break_warn`, `msg_break_start` and `msg_break_running` SHALL have a default text in each Language Preset ("de", "en", "id") with the same placeholders. The parent SHALL be able to edit them like the other child message keys. `{break_end}` SHALL be the end of the Break in the Control Time Zone in the format `HH:mm`. The title of the three messages SHALL be the fixed session text `break_title` of the Language Preset.

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
When the setting `telegram_notify_break` is on and Telegram is configured, the system SHALL send a notice to the parent at each Break start. The notice SHALL name the user and the end time in the Control Time Zone and SHALL have a button that ends this Break. The setting SHALL be off by default.

#### Scenario: Notice on
- **WHEN** `telegram_notify_break` is on and a Break of `Kid` starts at 15:00
- **THEN** the parent gets a Telegram notice that the Break of `Kid` lasts until 15:10, with a button to end it

#### Scenario: Default
- **WHEN** the parent did not change `telegram_notify_break` and a Break starts
- **THEN** the parent gets no Telegram notice
