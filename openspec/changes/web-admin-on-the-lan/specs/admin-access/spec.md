# Spec Delta

## MODIFIED Requirements

### Requirement: No admin sign-in from the session of the child
The system SHALL refuse a login and each admin request with an Admin Session from a process on this PC that belongs to a Restricted User, runs in the session of a Restricted User, or runs as a service account. The system SHALL find the process from the TCP connection table. If it cannot find the process or its user, it SHALL refuse the request (fail closed). A refused login SHALL not count as a failed login.

#### Scenario: Login from the child's session
- **WHEN** the parent enters a valid code in a browser in the session of the Restricted User on `http://127.0.0.1:5050/admin`
- **THEN** the system refuses the login with status 403, issues no Admin Session, and shows a German text that tells the parent to use the own Windows account, a phone or a laptop

#### Scenario: Stolen cookie in the child's session
- **WHEN** a process of the Restricted User sends an admin request with a valid Admin Session cookie
- **THEN** the system refuses the request with status 401

#### Scenario: Parent on this PC
- **WHEN** the parent signs in from a browser in the parent's own Windows session on this PC
- **THEN** the system checks the code as usual

#### Scenario: Owner not found
- **WHEN** the system cannot find the process of a connection from this PC
- **THEN** the system refuses the login

#### Scenario: Owner is not a user account
- **WHEN** the process of a connection from this PC, or the user of its session, is not a local or domain user account (for example a virtual account `S-1-5-90-…` or `S-1-5-96-…`, a service SID, or an Azure AD account)
- **THEN** the system refuses the login

#### Scenario: Session user not known
- **WHEN** the session of the process has a user, but the system cannot read the SID of that user
- **THEN** the system refuses the login

#### Scenario: Process id given to a new process
- **WHEN** the TCP table gives the create time of the connection and the owner process started after it
- **THEN** the system refuses the login

#### Scenario: Login from the child's session on the LAN Admin Endpoint
- **WHEN** the parent enters a valid code in a browser in the session of the Restricted User on `https://<LAN IP of this PC>:5051/admin`
- **THEN** the system refuses the login with status 403 and issues no Admin Session, as on port 5050

### Requirement: No admin request from another origin
The system SHALL refuse a state-changing admin request (POST, PUT or DELETE) and a login when the request has an `Origin` header that is not the origin of the Remote Admin Interface. A different port is a different origin. The origin of the Remote Admin Interface is the scheme of the connection (`http` or `https`) plus the `Host` header. A header such as `X-Forwarded-Proto` SHALL not change the scheme.

#### Scenario: Page of the child on another port
- **WHEN** a page from `http://localhost:8000` makes the browser of the signed-in parent send `POST /api/admin/grant` to port 5050
- **THEN** the system refuses the request and the Bonus Minutes do not change

#### Scenario: Login over HTTPS
- **WHEN** the admin page on `https://192.168.1.63:5051/admin` sends `POST /api/admin/login` with the header `Origin: https://192.168.1.63:5051`
- **THEN** the `Origin` check passes and the system checks the code

#### Scenario: Wrong scheme
- **WHEN** a request to port 5051 has the header `Origin: http://192.168.1.63:5051`, or a request to port 5050 has the header `X-Forwarded-Proto: https` and an `Origin` with `https`
- **THEN** the system refuses the request with status 403

### Requirement: Limits do not depend on the web server
The service SHALL enforce the limits also when its web server cannot start. A failure of one worker SHALL not stop the other workers. Port 5050 and the LAN Admin Endpoint on port 5051 SHALL start and retry independently: a failure on one port SHALL not stop the other port.

#### Scenario: Port taken by a program of the child
- **WHEN** a program of the Restricted User holds port 5050 or port 5051 before the service starts its web server
- **THEN** the service enforces the limits, and tries to start the web server on that port again after a wait that grows to at most 60 seconds
- **AND** the service logs the port, the process name, the process id and the user of the program that holds the port, and does not close or kill that program
- **AND** the Admin app shows a warning that names the port and the program, does not show the sign-in as set up, and tells the parent not to type a code on this PC
- **AND** after the start gate opened, the service sends one Telegram alert that names the port, if Telegram is configured
- **AND** the web server answers after the port is free, and the warning goes away

#### Scenario: Port bound before the data folder step
- **WHEN** the service starts
- **THEN** the web server binds port 5050 and port 5051 before the data folder step, so that no other program can bind them, also not on `127.0.0.1` or `[::1]`
- **AND** each route answers 503 until the start gate opens, and no route reads the database before it

#### Scenario: Only the HTTPS port is taken
- **WHEN** a program of the Restricted User holds port 5051 and port 5050 is free
- **THEN** the Session Agent, the child request page and the admin page on `http://localhost:5050` work
