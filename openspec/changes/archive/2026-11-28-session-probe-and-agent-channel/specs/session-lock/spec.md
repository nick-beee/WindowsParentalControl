## MODIFIED Requirements

### Requirement: Limit the Lock Deferral
A session-end request of the Session Agent is a claim of the child, so a request during a deferral of the session SHALL NOT extend it. After a deferral that owed a lock and ended by its time while the session still existed, the system SHALL start no deferral for that Restricted User for 15 minutes from its start, except before a Forced Logoff. A deferral that ended with the session, or that owed no lock, SHALL NOT count. The system SHALL log each deferral, each deferred lock and each refusal.

#### Scenario: Fake request at the limit
- **WHEN** the Allowance Time ends and the child makes its Session Agent send the session-end request but does not sign out
- **THEN** the system locks the session at most 75 seconds plus 10 seconds after the time when it was due

#### Scenario: Repeated fake request
- **WHEN** the child makes its Session Agent send the session-end request again 5 minutes after a deferral that owed a lock and did not end with a sign-out
- **THEN** the system starts no deferral and locks the session within 60 seconds

#### Scenario: Request repeated during a deferral
- **WHEN** the child makes its Session Agent send the session-end request every 10 seconds during a deferral that owes a lock
- **THEN** the deferral ends 75 seconds after the first request

#### Scenario: Requests while no lock is due
- **WHEN** the child makes its Session Agent send the session-end request many times during the day while the Allowance Time and the Schedule Window have time left and no Break is due
- **THEN** a later deferral at the limit is still granted

#### Scenario: Session-end request on the old route
- **WHEN** a process of the child sends the session-end request to `POST /api/agent/session-ending`
- **THEN** the web server answers 404 and no deferral starts
