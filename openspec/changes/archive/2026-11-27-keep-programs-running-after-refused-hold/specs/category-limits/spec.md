## ADDED Requirements

### Requirement: Session End Guard
When the system refuses a session-end request of a Restricted User, it SHALL start a Session End Guard (60 seconds) for that session. During the guard it SHALL NOT suspend again a recorded process of the session that it suspended itself and that another party resumed since then, for example the Session Agent at the end-session query. The guard SHALL end when the session ends or when its time is over. A guard SHALL NOT resume a process and SHALL NOT stop a new suspension.

#### Scenario: Second sign-out within 15 minutes
- **WHEN** a sign-out of the child did not complete, Notepad is suspended because of a block, and the child signs out again 7 minutes later
- **THEN** the system refuses the hold and starts a guard
- **AND** the Session Agent resumes Notepad, and a tick during the sign-out does not suspend Notepad

#### Scenario: Second sign-out completes
- **WHEN** the system refused a hold and started a guard, and the agent resumed a suspended game for the sign-out
- **THEN** the game gets the end-session query of Windows while it runs and closes itself
- **AND** the session ends and the guard ends with it

#### Scenario: Guard ends and the session goes on
- **WHEN** a guard covers a game and the child cancels the sign-out
- **THEN** the system suspends the game again within 20 seconds after the end of the guard, while Games is still exhausted

#### Scenario: Due game during a guard
- **WHEN** a guard of the session runs and Games usage reaches the Category Limit while a game runs that the system did not suspend before
- **THEN** the system suspends the game within 70 seconds, as without the guard

### Requirement: Limit the Session End Guard
A Session End Guard SHALL NOT cover a process that the system resumed itself since its last suspension, and SHALL cover a process at most once for each suspension by the system. A request during an active guard of the session SHALL NOT extend it. A guard SHALL NOT count against the limit of Session End Holds and SHALL NOT alert the parent. The system SHALL log the start of each guard.

#### Scenario: Fake request without a resume
- **WHEN** Games is exhausted, the game is suspended, a hold was refused, and the child sends the session-end request with `curl`
- **THEN** the game stays suspended

#### Scenario: Fake request after a granted hold
- **WHEN** the system resumed the game for a hold that ended without a sign-out, and the child sends the session-end request during the Pending Suspension after the hold
- **THEN** the system refuses the request and the guard does not cover the game
- **AND** the system suspends the game at the end of the Pending Suspension

#### Scenario: Fake request after a guard
- **WHEN** a guard covered the game, the guard ended, and the child sends the session-end request again during the Pending Suspension of the game
- **THEN** the new guard does not cover the game
- **AND** the system suspends the game at the end of the Pending Suspension

## MODIFIED Requirements

### Requirement: Keep a suspended process suspended
If a recorded process runs again (for example, the child resumes it with a tool), the system SHALL suspend it again within 70 seconds while its exe still has no time left or while it is still a Blocked Program. The 70 seconds include the 10 seconds of the Pending Suspension. During a Session End Hold of its session, or a Session End Guard that covers it, the system SHALL wait until the hold or the guard ends, and SHALL then suspend it again within 20 seconds.

#### Scenario: Child resumes a suspended game
- **WHEN** Games is exhausted and the child resumes the suspended game with a tool
- **THEN** the system suspends the game again within 70 seconds

#### Scenario: Recorded game during a hold
- **WHEN** the system resumed a recorded game for a sign-out and the hold is active
- **THEN** the system does not suspend the game until the hold ends
- **AND** the suspension record stays

#### Scenario: Recorded game during a guard
- **WHEN** the Session Agent resumed a recorded game for a sign-out, the system refused the hold, and the guard is active
- **THEN** the system does not suspend the game until the guard ends
- **AND** the suspension record stays

#### Scenario: Child resumes a suspended blocked browser
- **WHEN** `chrome.exe` is blocked, a Chrome process is suspended because of the block, Unknown has time left, and the child resumes Chrome with a tool
- **THEN** the system suspends Chrome again within 70 seconds

### Requirement: Limit the Session End Hold from the agent
A request from the Session Agent is not authenticated, so the system SHALL grant at most one Session End Hold per Restricted User in 15 minutes that does not end with the end of the session. For a request outside this limit, no hold starts, the system resumes no process, and the requirement "Session End Guard" applies. The system SHALL log each request and each refusal.

#### Scenario: Fake request from the child
- **WHEN** Games is exhausted and the child sends the session-end request to the service, but does not sign out
- **THEN** the game runs for at most the hold plus 20 seconds
- **AND** the system then suspends the game again

#### Scenario: Repeated fake request
- **WHEN** the child sends the session-end request again 5 minutes after a hold that did not end with a sign-out
- **THEN** the system refuses the request
- **AND** the game stays suspended

#### Scenario: Fake end-session message to the agent
- **WHEN** a hold was refused and the child makes the Session Agent resume the game without a sign-out
- **THEN** the system suspends the game again within 20 seconds after the end of the guard, so the game runs for at most 80 seconds

#### Scenario: Sign-out, sign-in and sign-out again
- **WHEN** the child signs out with a suspended game, signs in again, and signs out again 5 minutes later with a suspended game
- **THEN** the system grants the second hold, because the first hold ended with a sign-out
