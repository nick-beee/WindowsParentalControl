# Spec Delta

## MODIFIED Requirements

### Requirement: Child-facing routes stay open
The routes for the Restricted User (`/request`, `/api/request`) SHALL work without an Admin Session. They SHALL need a valid Request Token instead (request-window, "The request routes take the user only from the Request Token"). The route `/` SHALL redirect to `/admin` without an Admin Session. The Agent Channel SHALL work without an Admin Session. None of them SHALL grant Bonus Minutes, resolve a Grace Request, or change a limit, an App Rule, a schedule, a setting or the admin credential.

#### Scenario: Grace Request before the setup
- **WHEN** the Restricted User sends a Grace Request from the Request Window on an install without the setup
- **THEN** the system saves it as pending and sends it to the parent as before
- **AND** the Bonus Minutes do not change until the parent approves it

#### Scenario: Session Agent without a session
- **WHEN** the Session Agent asks for its status through the Agent Channel
- **THEN** the system answers as before, with no Admin Session

#### Scenario: Request page without a token
- **WHEN** a process sends `GET /request` without an Admin Session and without a Request Token
- **THEN** the system answers 401, not a redirect to the admin login

#### Scenario: Parent opens the root address
- **WHEN** the parent opens `http://localhost:5050/` in a browser
- **THEN** the system redirects to `/admin`, not to `/request`
- **AND** the web admin asks for the sign-in if there is no Admin Session
