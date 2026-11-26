## MODIFIED Requirements

### Requirement: Quota for screen captures of the Session Agent
The service SHALL accept at most 200 screen captures and 50 MB per user on one day (since local midnight), and at most 5 MB per capture, on the capture upload of the Agent Channel. The user SHALL be the user of that channel. The service SHALL refuse more without a file and without a row, and it SHALL write the refusal to the log one time per user and day.

#### Scenario: Flood of captures
- **WHEN** the Session Agent sends a 201st capture for its user on one day
- **THEN** the service refuses it, writes no file and adds no row

#### Scenario: Capture too large
- **WHEN** the Session Agent sends a capture with more than 5 MB
- **THEN** the service refuses it and keeps no file

#### Scenario: Capture on the old route
- **WHEN** a process sends `POST /api/agent/capture`
- **THEN** the web server answers 404, writes no file and adds no row

### Requirement: Child-facing routes stay open
The routes for the Restricted User (`/`, `/request`, `/api/request`) SHALL work without an Admin Session. The Agent Channel SHALL work without an Admin Session. None of them SHALL grant Bonus Minutes, resolve a Grace Request, or change a limit, an App Rule, a schedule, a setting or the admin credential.

#### Scenario: Grace Request before the setup
- **WHEN** the Restricted User sends a Grace Request on an install without the setup
- **THEN** the system saves it as pending and sends it to the parent as before
- **AND** the Bonus Minutes do not change until the parent approves it

#### Scenario: Session Agent without a session
- **WHEN** the Session Agent asks for its status through the Agent Channel
- **THEN** the system answers as before, with no Admin Session
