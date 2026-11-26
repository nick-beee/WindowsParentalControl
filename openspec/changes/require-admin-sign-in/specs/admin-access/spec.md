# Spec Delta

## Purpose

Controls who can use the admin functions of the Remote Admin Interface: the setup and the recovery of the admin credential, the Admin Session, the limits on failed logins, and the routes that stay open for the Restricted User and the Session Agent.

## ADDED Requirements

### Requirement: Admin routes require an Admin Session
The system SHALL refuse each request to an admin route without a valid Admin Session, with status 401 and with no change of data. This SHALL be true for each install and each setting. The admin routes are all routes below `/api/admin/`, except the login and the logout. A new route below `/api/admin/` SHALL be an admin route without a change to the gate.

#### Scenario: Bonus Minutes on a fresh install
- **WHEN** a process of the Restricted User sends `POST /api/admin/grant` to `127.0.0.1:5050` on a fresh install, with no cookie
- **THEN** the system refuses the request with status 401
- **AND** the Bonus Minutes of the day do not change

#### Scenario: Category Limit without a session
- **WHEN** a request without a valid Admin Session changes a Category Limit, an App Rule, the schedule or a setting
- **THEN** the system refuses the request with status 401 and keeps the old value

#### Scenario: Unknown token
- **WHEN** a request carries an admin cookie with a token that the system did not issue
- **THEN** the system refuses the request with status 401

### Requirement: Admin credential is set up only on the PC by an administrator
The system SHALL let the parent set up the Authenticator TOTP secret only in the Parental Control Admin app, which runs with administrator rights. No route of the web server SHALL create, show or change the secret. The app SHALL save a new secret only after the parent enters a valid current code for it. After the setup, the app SHALL not show the secret again.

#### Scenario: Setup with a confirmed code
- **WHEN** the parent starts the setup in the Admin app, scans the QR code and enters the current code from the authenticator app
- **THEN** the app saves the secret and the Remote Admin Interface is set up

#### Scenario: Wrong confirmation code
- **WHEN** the parent enters a code that does not match the new secret
- **THEN** the app does not save the secret and the old credential stays in use

#### Scenario: No setup on the web
- **WHEN** a request to the web server tries to set or read the TOTP secret
- **THEN** no route does it

#### Scenario: Secret hidden after the setup
- **WHEN** the parent opens the settings of the Admin app after the setup
- **THEN** the app shows that the sign-in is set up, but not the secret or the QR code

### Requirement: Remote Admin Interface before the setup
The Remote Admin Interface SHALL be set up only when TOTP is on and a valid secret is saved. Before that, the system SHALL show on `/admin` only a text that tells the parent to set up the sign-in in the Admin app on this PC, with no form. The login SHALL refuse each code.

#### Scenario: Admin page on a fresh install
- **WHEN** someone opens `/admin` before the setup
- **THEN** the page shows the setup text and no data, no form and no admin function

#### Scenario: Login before the setup
- **WHEN** a request sends any code to the login before the setup
- **THEN** the system refuses the code and issues no Admin Session

#### Scenario: TOTP on with an empty secret
- **WHEN** the setting for TOTP is on but the saved secret is empty or not valid Base32
- **THEN** the Remote Admin Interface is not set up

### Requirement: Sign in with Authenticator TOTP
The system SHALL issue an Admin Session for a valid current TOTP code of the saved secret. The system SHALL accept a code at most one time: after a successful login, it SHALL refuse the same code and each code of an earlier time step.

#### Scenario: Valid code
- **WHEN** the parent enters the current code from the authenticator app
- **THEN** the system issues an Admin Session and shows the dashboard

#### Scenario: Code used again
- **WHEN** someone enters a code that a login already used, inside its validity window
- **THEN** the system refuses the code and counts a failed login

#### Scenario: Wrong code
- **WHEN** someone enters a code that does not match
- **THEN** the system refuses it, counts a failed login and shows the login page again

### Requirement: Limit on failed logins
The system SHALL allow at most 10 failed logins from one source and at most 30 from all sources in 24 hours. All addresses of this PC SHALL count as one source. While a limit applies, the system SHALL refuse each code without a check, also a valid one. The counts SHALL survive a restart of the service and of the PC. A valid login SHALL clear the failed logins of its source.

#### Scenario: Script of the child on this PC
- **WHEN** a process on this PC sends 10 wrong codes in 24 hours, from `127.0.0.1`, `::1` or an address of this PC
- **THEN** the system refuses each further code from this PC until the oldest of these failures is 24 hours old

#### Scenario: Parent on the phone while this PC is blocked
- **WHEN** this PC has reached its limit and the parent signs in from a phone on the LAN with a valid code
- **THEN** the system issues an Admin Session

#### Scenario: Restart does not reset the count
- **WHEN** this PC has reached its limit and the PC restarts
- **THEN** the system still refuses codes from this PC

#### Scenario: Successful login clears the count of its source
- **WHEN** the parent enters two wrong codes and then a valid code from the phone
- **THEN** the system issues an Admin Session and the failed logins of the phone count zero again

#### Scenario: All sources together
- **WHEN** 30 failed logins came from all sources in 24 hours
- **THEN** the system refuses each code from each source until the oldest of these failures is 24 hours old

### Requirement: Alert on blocked logins
The system SHALL send a Telegram message to the configured chat when a source or all sources reach the limit on failed logins. The message SHALL name the source address and the time. Without a Telegram configuration, the system SHALL write the event to the log only.

#### Scenario: Limit reached
- **WHEN** this PC reaches the limit on failed logins and Telegram is configured
- **THEN** the parent gets one Telegram message about the blocked logins from this PC

### Requirement: Admin Session lifetime
An Admin Session SHALL end 12 hours after the login, at a logout, and at a restart of the service. The system SHALL check the expiry on the server, not only through the cookie. The logout SHALL be a POST request.

#### Scenario: Session after 12 hours
- **WHEN** a request uses an Admin Session that started more than 12 hours ago
- **THEN** the system refuses the request with status 401

#### Scenario: Restart of the service
- **WHEN** the service restarts
- **THEN** each Admin Session from before the restart is not valid

#### Scenario: Logout
- **WHEN** the parent signs out
- **THEN** the token of that Admin Session is not valid for a further request

### Requirement: End all Admin Sessions from the Admin app
The system SHALL end all Admin Sessions within one request when the parent sets up a new secret or selects "Sign out all web sessions" in the Admin app, without a restart of the service.

#### Scenario: Stolen session
- **WHEN** the parent selects "Sign out all web sessions" in the Admin app
- **THEN** the next admin request with an earlier Admin Session gets status 401

#### Scenario: New secret
- **WHEN** the parent sets up a new secret in the Admin app
- **THEN** codes of the old secret do not work and all earlier Admin Sessions end

### Requirement: Recovery in the Admin app
The Admin app SHALL let an administrator of the PC set up a new secret and clear the limit on failed logins. Then the parent is never locked out of the Remote Admin Interface for longer than the time to open the Admin app on this PC.

#### Scenario: Phone lost
- **WHEN** the parent has no access to the authenticator app and sets up a new secret in the Admin app
- **THEN** the parent can sign in with the new authenticator entry

#### Scenario: Limit cleared
- **WHEN** this PC has reached the limit on failed logins and the parent selects "Clear login limit" in the Admin app
- **THEN** the next valid code from this PC gives an Admin Session

### Requirement: No admin request from another origin
The system SHALL refuse a state-changing admin request (POST, PUT or DELETE) and a login when the request has an `Origin` header that is not the origin of the Remote Admin Interface. A different port is a different origin.

#### Scenario: Page of the child on another port
- **WHEN** a page from `http://localhost:8000` makes the browser of the signed-in parent send `POST /api/admin/grant` to port 5050
- **THEN** the system refuses the request and the Bonus Minutes do not change

### Requirement: Child-facing routes stay open
The routes for the Restricted User and the Session Agent (`/`, `/request`, `/api/request`, `/api/agent/*`) SHALL work without an Admin Session. None of them SHALL grant Bonus Minutes, resolve a Grace Request, or change a limit, an App Rule, a schedule, a setting or the admin credential.

#### Scenario: Grace Request before the setup
- **WHEN** the Restricted User sends a Grace Request on an install without the setup
- **THEN** the system saves it as pending and sends it to the parent as before
- **AND** the Bonus Minutes do not change until the parent approves it

#### Scenario: Session Agent without a session
- **WHEN** the Session Agent polls `/api/agent/status`
- **THEN** the system answers as before, with no Admin Session

### Requirement: Restricted User cannot read the admin secret
The system SHALL keep the data folder (`C:\ProgramData\ParentalControl`) readable only by SYSTEM and the Administrators group. The service SHALL set these access rules at each start, also when the folder already exists, and SHALL log an error if it cannot.

#### Scenario: Child reads the database
- **WHEN** a process of the Restricted User tries to open `data.db`
- **THEN** Windows denies the access

#### Scenario: Old install with open access rules
- **WHEN** the folder exists from an older install with other access rules and the service starts
- **THEN** the folder has only the rules for SYSTEM and the Administrators group

### Requirement: Upgrade of an install without the setup
At the first start after the upgrade, an install with TOTP on and a valid secret SHALL keep its sign-in. Each other install SHALL get a Remote Admin Interface that is not set up. If Telegram is configured, the system SHALL send one message that tells the parent to do the setup.

#### Scenario: TOTP was on
- **WHEN** an install with TOTP on and a valid secret gets the upgrade
- **THEN** the parent signs in with the same authenticator entry as before

#### Scenario: TOTP was off
- **WHEN** an install with TOTP off gets the upgrade
- **THEN** each admin route refuses requests and `/admin` shows the setup text
- **AND** the parent gets one Telegram message about the setup, if Telegram is configured

#### Scenario: Old secret while TOTP was off
- **WHEN** an install with TOTP off has an old saved secret
- **THEN** the Remote Admin Interface is not set up until the parent confirms a secret in the Admin app
