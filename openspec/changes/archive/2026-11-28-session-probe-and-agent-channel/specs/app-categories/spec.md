## MODIFIED Requirements

### Requirement: Exe data is untrusted
The system SHALL treat the exe path, the exe name and the window title that it gets from the Session Probe or the Session Agent as untrusted text: it SHALL use them only to match rules and to show them escaped in the admin pages.

#### Scenario: Script in a window title
- **WHEN** the Session Probe reports a window title or path that contains `<script>`
- **THEN** the admin page shows the text as text and runs no script
