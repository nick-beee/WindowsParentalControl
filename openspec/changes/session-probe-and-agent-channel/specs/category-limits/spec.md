## MODIFIED Requirements

### Requirement: Count Category Usage from the Foreground Window
The system SHALL count Category Usage per Restricted User, App Category and day from the Foreground Window samples of the Session Probe, never from data of the Session Agent. For each sample it SHALL add the time since the previous sample of the session, at most 10 seconds, to the category of the sampled exe, and SHALL store whole minutes. For a Store app, the sampled exe SHALL be the exe of the app. It SHALL NOT count System, a locked session, or an exe that it suspended for this user.

#### Scenario: Three samples of a game
- **WHEN** the Session Probe reports a Games exe as Foreground Window in three samples, each 5 seconds after the previous sample of the unlocked session
- **THEN** the system adds 15 seconds to Games
- **AND** after 60 seconds of such samples, Category Usage for Games increases by 1 minute

#### Scenario: Locked session
- **WHEN** the session is locked
- **THEN** no Category Usage increases

#### Scenario: System program
- **WHEN** the Session Probe sees `C:\Windows\explorer.exe` as Foreground Window
- **THEN** no Category Usage increases

#### Scenario: Suspended game in front
- **WHEN** the system suspended a game and its frozen window stays the Foreground Window
- **THEN** no Category Usage increases

#### Scenario: Store app
- **WHEN** the Windows Calculator from the Microsoft Store is the Foreground Window
- **THEN** the Session Probe reports `CalculatorApp.exe` in `C:\Program Files\WindowsApps`, and its time counts in Unknown

#### Scenario: False Study samples from the child
- **WHEN** a game is the Foreground Window and a process of the child sends Study exe samples to the service, also through its Session Agent
- **THEN** Category Usage for Games increases
- **AND** Category Usage for Study does not increase

#### Scenario: Gap between samples
- **WHEN** the Session Probe gives no sample for 2 minutes and then sends a Games sample
- **THEN** Category Usage for Games increases by at most 10 seconds for that sample

### Requirement: Suspend the programs of an exhausted category
When the Category Usage of a category reaches the limit of the day of an exe (its App Limit or else its Category Limit), the system SHALL suspend, within 70 seconds, each process of that exe in the session of that Restricted User, if the exe was a Foreground Window of that user today. The 70 seconds include the 10 seconds of the Pending Suspension. The requirement "Session End Hold" adds an exception. The session SHALL stay unlocked. The system SHALL NOT close or kill any of these processes.

#### Scenario: Games limit ends while a game runs
- **WHEN** Games usage reaches the Category Limit while a Steam game and a Study program run
- **THEN** the game process is suspended and still exists
- **AND** the Study program still runs
- **AND** the session is not locked

#### Scenario: Game starts after the limit ended
- **WHEN** Games is exhausted for today and the child starts a Games exe and brings it to the front
- **THEN** the system suspends that process within 70 seconds after the Session Probe sees it in front

#### Scenario: Background helper of an exhausted category
- **WHEN** Unknown is exhausted and an Unknown tray helper runs that was never a Foreground Window today
- **THEN** the helper keeps running

#### Scenario: Limit ends during a sign-out
- **WHEN** a Session End Hold of the session is active and Games usage reaches the Category Limit
- **THEN** the system does not suspend the game during the hold

#### Scenario: Game hidden from the Session Agent
- **WHEN** Games is exhausted, the child stops its Session Agent, and then plays a game in front
- **THEN** the system suspends the game within 70 seconds after the Session Probe sees it in front

### Requirement: Limit the Session End Hold from the agent
The child controls its Session Agent, so a session-end request through the Agent Channel is a claim of the child. The system SHALL grant at most one Session End Hold per Restricted User in 15 minutes that does not end with the end of the session. For a request outside this limit, no hold starts and the requirement "Keep a suspended process suspended" applies without the hold exception. The system SHALL log each request and each refusal.

#### Scenario: Fake request from the child
- **WHEN** Games is exhausted and the child makes its Session Agent send the session-end request, but does not sign out
- **THEN** the game runs for at most the hold plus 20 seconds
- **AND** the system then suspends the game again

#### Scenario: Repeated fake request
- **WHEN** the child makes its Session Agent send the session-end request again 5 minutes after a hold that did not end with a sign-out
- **THEN** the system refuses the request
- **AND** the game stays suspended

#### Scenario: Fake end-session message to the agent
- **WHEN** a hold was refused and the child makes the Session Agent resume the game without a sign-out
- **THEN** the system suspends the game again within 70 seconds

#### Scenario: Sign-out, sign-in and sign-out again
- **WHEN** the child signs out with a suspended game, signs in again, and signs out again 5 minutes later with a suspended game
- **THEN** the system grants the second hold, because the first hold ended with a sign-out
