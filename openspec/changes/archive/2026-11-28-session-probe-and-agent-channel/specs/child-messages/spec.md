## MODIFIED Requirements

### Requirement: The Session Agent follows the preset
The service SHALL send the Language Preset in the status answer of the Agent Channel. The Session Agent SHALL show each of its texts (widget, tray menu, prompt dialog, full-screen settings) in that preset, and "de" before the first response or without a preset. A text that the parent or the child typed SHALL stay as it is.

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
