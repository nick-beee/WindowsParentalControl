# Spec Delta

## MODIFIED Requirements

### Requirement: Close a Blocked Start
The system SHALL close a process of a Blocked Program only if the process started after the block took effect and after the service started, and the system detects it within 10 seconds after its creation. If the parent process is a live Blocked Program process that started before the block took effect, the system SHALL suspend the new process, not close it. This is the only close of a program of a Restricted User.

#### Scenario: Child starts a browser
- **WHEN** `chrome.exe` is blocked and the child starts Chrome
- **THEN** the system closes the Chrome process within 5 seconds after its start

#### Scenario: Browser started before the block
- **WHEN** Chrome runs and then the parent turns on the block for `chrome.exe`
- **THEN** the system does not close Chrome

#### Scenario: New tab in a browser that ran before the block
- **WHEN** Firefox ran before the parent turned on the block for `firefox.exe`, and the child opens a new tab before the system suspends Firefox
- **THEN** the system suspends the new Firefox process and does not close it

### Requirement: Close a Blocked Start without a Pending Suspension
The system SHALL close a Blocked Start without a Pending Suspension. It SHALL close a Blocked Start also during a Session End Hold of its session.

#### Scenario: Browser start during a cancelled sign-out
- **WHEN** a Session End Hold of the session of the child is active and the child starts Chrome, which is blocked
- **THEN** the system closes the Chrome process within 5 seconds after its start
