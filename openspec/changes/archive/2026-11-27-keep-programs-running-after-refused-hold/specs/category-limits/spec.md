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
A Session End Guard SHALL NOT cover a process that the system resumed itself since its last suspension, and SHALL NOT cover a process again until the system suspends it again or sees it suspended outside a guard. A request during an active guard of the session SHALL NOT extend it. A guard SHALL NOT count against the limit of Session End Holds and SHALL NOT alert the parent. The system SHALL log the start of each guard.

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

### Requirement: Keep a blocked suspension
A process that the system suspended because its exe is a Blocked Program SHALL stay suspended while the block applies, also if its exe has time left, until the session ends or the service stops; `blocked-programs` defines when it resumes. A blocked suspension SHALL go through the Pending Suspension, the Session End Hold and the Session End Guard like a limit suspension; time left does not drop its Pending Suspension. During a Session End Hold, the system SHALL still close a Blocked Start.

#### Scenario: Blocked browser on a new day
- **WHEN** the system suspended a blocked Chrome yesterday, the block is still on, and the date changes
- **THEN** Chrome stays suspended

#### Scenario: Blocked browser with time left in its category
- **WHEN** a blocked Chrome is a Pending Suspension and Unknown has time left
- **THEN** the system suspends Chrome at the end of the 10 seconds

#### Scenario: Chrome start during a Session End Hold
- **WHEN** a Session End Hold of the child's session runs and the child starts Chrome, and `chrome.exe` is blocked
- **THEN** the system closes Chrome at its start

### Requirement: Minimize a process before its suspension
Before the system suspends a process, it SHALL make the process a Pending Suspension for 10 seconds, and the Session Agent SHALL minimize the visible top-level windows of that process while it still runs. The system SHALL suspend the process after the 10 seconds, also if the agent does not run or answer. It SHALL drop a Pending Suspension without a suspension if the exe has time left again, if the process ended, if a Session End Hold of the session started, or if a Session End Guard covers it.

#### Scenario: Full-screen game reaches the limit
- **WHEN** Games usage reaches the Category Limit while a game fills the screen
- **THEN** the game window is minimized before the system suspends the game
- **AND** the system suspends the game 10 seconds after the start of the Pending Suspension
- **AND** the child can use the desktop and the other programs

#### Scenario: Agent does not run
- **WHEN** the Session Agent does not run and Games usage reaches the Category Limit while a game runs
- **THEN** the system suspends the game 10 seconds after the start of the Pending Suspension
- **AND** the game window stays where it is

#### Scenario: Bonus Minutes during a Pending Suspension
- **WHEN** a game is a Pending Suspension and the parent approves 15 Bonus Minutes before the 10 seconds end
- **THEN** the system does not suspend the game

#### Scenario: Taskbar click on a minimized Suspended App
- **WHEN** a Suspended App was minimized before its suspension and the child clicks its taskbar button
- **THEN** the window does not come to the front
- **AND** the child can use the desktop and the other programs

#### Scenario: Browser that ran before the block
- **WHEN** Chrome runs in the session of the child and the parent turns on the block for `chrome.exe`
- **THEN** the system makes each Chrome process a Pending Suspension, and the agent minimizes the Chrome windows
- **AND** the system suspends Chrome 10 seconds after the start of the Pending Suspension and does not close it
