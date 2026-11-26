# Spec Delta

## Purpose

Sets the language of the texts that the Restricted User sees from the service, the Session Agent and the child request page, keeps the texts that the parent edited, and makes German the default of a new install.

## ADDED Requirements

### Requirement: Language Presets
The system SHALL have the Language Presets "de" (German), "en" (English) and "id" (Indonesian). Each preset SHALL have a text for each child message key, for each fixed session message, for each text of the Session Agent and for each text of the child request page. The text of a key SHALL have the same placeholders in each preset, for example `{minutes}`, `{curfew_end}` and `{name}`.

#### Scenario: Same placeholders
- **WHEN** the "en" text of `msg_curfew_warn` has the placeholders `{curfew_end}` and `{minutes}`
- **THEN** the "de" and the "id" texts of `msg_curfew_warn` have the same two placeholders and no other placeholder

#### Scenario: German warning
- **WHEN** the preset is "de" and 5 minutes of the daily Allowance Time remain
- **THEN** the child gets the message "Deine Bildschirmzeit für heute ist in 5 Minuten um. Speichere jetzt, was du gerade machst."

### Requirement: Word forms for a number
A text SHALL use the correct word form for a number of minutes, seconds, hours or requests. A placeholder of the form `{name|one|other}` SHALL give the text `one` when the value of `name` is 1, and the text `other` for each other value. The placeholder `{name}` SHALL give the number. The Indonesian texts SHALL use one word form, because Indonesian has no plural form after a number.

#### Scenario: One minute in German
- **WHEN** the preset is "de" and 1 minute of the daily Allowance Time remains
- **THEN** the child gets the message "Deine Bildschirmzeit für heute ist in 1 Minute um. Speichere jetzt, was du gerade machst."

#### Scenario: One minute in English
- **WHEN** the preset is "en" and 1 minute of the daily Allowance Time remains
- **THEN** the child gets the message "You have 1 minute of daily screen time remaining. Please save your work."

#### Scenario: Indonesian
- **WHEN** the preset is "id" and 1 or 5 minutes of the daily Allowance Time remain
- **THEN** the message says "1 menit" or "5 menit"

### Requirement: German is the default of a new install
When the database has no Language Preset, the system SHALL apply the preset "de" with the rule of a preset switch: it SHALL store "de" and write the German text of each child message key whose stored text is missing, empty or equal to a default text. The WPF Admin SHALL NOT ask for a language at its first start.

#### Scenario: New install
- **WHEN** the service starts with a new, empty database
- **THEN** the stored Language Preset is "de"
- **AND** each child message key has its German text

#### Scenario: Install without a preset row
- **WHEN** an earlier install has no Language Preset but has the Indonesian default text of `msg_category_warn`
- **THEN** the system sets the preset "de" and replaces that text with the German default text

#### Scenario: First start of the WPF Admin
- **WHEN** the parent starts the WPF Admin for the first time
- **THEN** the WPF Admin shows no language question

### Requirement: An update keeps the preset and the edited texts
When the database already has a Language Preset, an update of the program SHALL NOT change the preset and SHALL NOT change a text that the parent edited. A stored text that is equal to a default text of an earlier program version SHALL get the current default text of the stored preset.

#### Scenario: Indonesian install after the update
- **WHEN** an install with the preset "id" gets this version
- **THEN** the preset stays "id" and each stored text stays the same

#### Scenario: English install with an earlier default text
- **WHEN** an install with the preset "en" has the earlier default text "You have {minutes} minutes of daily screen time remaining. Please save your work." and gets this version
- **THEN** the preset stays "en"
- **AND** `msg_limit_warn` has the current "en" default text with the word forms of `{minutes}`

#### Scenario: Edited English text after the update
- **WHEN** an install with the preset "en" has an edited `msg_remote_lock` and gets this version
- **THEN** `msg_remote_lock` stays the same

### Requirement: A preset switch keeps the edited texts
When the parent applies a Language Preset, the system SHALL store the preset. For each child message key, it SHALL write the text of the new preset only if the stored text is empty, is equal to the default text of a preset, or is equal to a default text of an earlier program version. It SHALL keep each other text. The same rule SHALL apply to the list of prompt reasons.

#### Scenario: Unedited text
- **WHEN** the preset is "id", the stored `msg_limit_reached` is the Indonesian default, and the parent applies "de"
- **THEN** `msg_limit_reached` has the German default text

#### Scenario: Edited text
- **WHEN** the parent changed `msg_remote_lock` to "Abendessen!" and then applies "de"
- **THEN** `msg_remote_lock` stays "Abendessen!"

### Requirement: A missing text uses the default of the current preset
When the stored text of a child message key is empty or missing, the system SHALL use the default text of the current Language Preset for that key only. It SHALL NOT change the text of another key.

#### Scenario: One empty text
- **WHEN** `msg_bonus_granted` is empty, the parent edited `msg_remote_lock`, and the parent grants 15 Bonus Minutes
- **THEN** the child gets the default bonus text of the current preset with 15 minutes
- **AND** `msg_remote_lock` keeps the text of the parent

### Requirement: Category names follow the preset
In a child message, the `{name}` and `{category}` placeholders of an App Category SHALL show the name of the category in the language of the preset. With "de", the names SHALL be Spiele, Videos, Soziale Medien, Lernen, Kreativ, System and Sonstiges. With "en" and "id", the names SHALL be Games, Video, Social, Study, Creative, System and Unknown. The name of an app SHALL stay as it is.

#### Scenario: German category name
- **WHEN** the preset is "de" and the Games programs are suspended
- **THEN** the message names "Spiele", not "Games"

#### Scenario: App name
- **WHEN** the preset is "de" and 5 minutes of the App Limit of Minecraft remain
- **THEN** the warning names "Minecraft"

### Requirement: Fixed session messages follow the preset
The session messages that the service sends with a fixed text SHALL use the Language Preset. These are the logon banner, the titles of the warnings, the countdown text of a remote lock, the text when the parent ends the session, the text of an approved Bonus Minutes request, of an approved schedule exception, of an approved weekly schedule, of a declined request, the title of a parent message, and the decline reasons that the parent picks in Telegram, because the child sees these reasons.

#### Scenario: Remote lock in German
- **WHEN** the preset is "de" and the parent locks the session with a countdown of 15 seconds
- **THEN** the popup ends with "(Der Computer wird in 15 Sekunden gesperrt.)"

#### Scenario: Declined request in German
- **WHEN** the preset is "de" and the parent declines a request with the reason "Erst Hausaufgaben"
- **THEN** the child gets the message "Deine Eltern haben deine Anfrage abgelehnt: „Erst Hausaufgaben“"

#### Scenario: Decline reason from Telegram
- **WHEN** the preset is "de" and the parent declines a request with the first decline reason in Telegram
- **THEN** the child gets the message "Deine Eltern haben deine Anfrage abgelehnt: „Mach zuerst deine Hausaufgaben.“"

### Requirement: The Session Agent follows the preset
The service SHALL send the Language Preset in `/api/agent/status`. The Session Agent SHALL show each of its texts (widget, tray menu, prompt dialog, full-screen settings) in that preset, and "de" before the first response or without a preset. A text that the parent or the child typed SHALL stay as it is.

#### Scenario: German widget
- **WHEN** the preset is "de" and the Restricted User signs in
- **THEN** the widget shows "RESTZEIT HEUTE" and the button "🎮 Mehr Zeit wünschen"

#### Scenario: German prompt dialog
- **WHEN** the preset is "de" and the parent sends an urgent prompt
- **THEN** the dialog shows the title "🚨 WICHTIGE NACHRICHT VON DEINEN ELTERN" and the buttons "✅ Ja / Okay" and "❌ Nein"
- **AND** the message of the parent stays as the parent typed it

#### Scenario: Preset switch while the agent runs
- **WHEN** the agent shows German texts and the parent applies "id"
- **THEN** the widget shows "SISA WAKTU HARI INI" after the next status poll

### Requirement: The child request page follows the preset
The child request page (`/request`) SHALL show each of its texts in the language of the Language Preset, with the `lang` attribute of the preset. This includes the day names of the schedule strip, the status of the last request, and the error and success messages of `/api/request`. The page SHALL read the preset on the server and SHALL NOT take a language from the request. A text that the child or the parent typed SHALL stay as it is and SHALL stay escaped.

#### Scenario: German request page
- **WHEN** the preset is "de" and the child opens `/request` on a Monday
- **THEN** the page shows "Deine Restzeit heute", the day "Montag (heute)" and the option "+15 Minuten"

#### Scenario: German success message
- **WHEN** the preset is "de" and the child asks for 15 more minutes
- **THEN** the page shows "Deine Anfrage für +15 Minuten ist bei deinen Eltern."

### Requirement: Telegram texts for the parent
A Telegram text for the parent that has an Indonesian and an English version SHALL be Indonesian when the Language Preset is "id", and English for each other preset. This change adds no German Telegram text.

#### Scenario: German preset
- **WHEN** the preset is "de" and the child signs in
- **THEN** the parent gets the sign-in notification in English
