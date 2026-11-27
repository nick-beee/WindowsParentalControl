## MODIFIED Requirements

### Requirement: Count Category Usage from the Foreground Window
The system SHALL count Category Usage per Restricted User, App Category and day from the samples of the Session Probe, never from data of the Session Agent. For each sample it SHALL add the time since the previous sample of the session, at most 12 seconds, to the category of the counted process, and store whole minutes. It SHALL NOT count System, a locked session, or an exe that it suspended for this user.

#### Scenario: Three samples of a game
- **WHEN** the Session Probe reports a Games exe as Foreground Window in three samples, each 5 seconds after the previous sample of the unlocked session
- **THEN** the system adds 15 seconds to Games for the three samples
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
- **THEN** the counted process is `CalculatorApp.exe` in `C:\Program Files\WindowsApps`, and its time counts in Unknown

#### Scenario: False Study samples from the child
- **WHEN** a game is the Foreground Window and a process of the child sends Study exe samples to the service, also through its Session Agent
- **THEN** Category Usage for Games increases
- **AND** Category Usage for Study does not increase

#### Scenario: Gap between samples
- **WHEN** the Session Probe gives no sample for 2 minutes and then sends a Games sample
- **THEN** Category Usage for Games increases by at most 12 seconds for that sample

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

## ADDED Requirements

### Requirement: The counted process of a sample
The counted process of a sample SHALL be the focus process if another process than the Foreground Window owns the focus, else the app process of a Store app, else the Foreground Window process. If the focus process is System or `msedgewebview2.exe` and the Foreground Window process is not System, the Foreground Window process SHALL count. The system SHALL record each process of a sample as a Foreground Window of that user today.

#### Scenario: Game embedded in a System window
- **WHEN** the child puts a game window into a window of `notepad.exe` and plays the game
- **THEN** the time counts in Games
- **AND** the game is suspended when Games is exhausted

#### Scenario: System window embedded in a game
- **WHEN** the child puts a window of `notepad.exe` into a game window and gives it the focus
- **THEN** the time counts in Games

#### Scenario: WebView2 in a Study app
- **WHEN** a Study app is the Foreground Window and its `msedgewebview2.exe` control has the focus
- **THEN** the time counts in Study

### Requirement: A program that is never suspended still has a limit
The system SHALL count the Session Agent in Unknown, whatever the App Rules say. If the category of an exe that the system never suspends (the Session Agent, `msedgewebview2.exe`) is exhausted, and that exe is the counted process for more than 60 seconds within 10 minutes of unlocked time, the system SHALL apply the fallback when a process cannot be suspended.

#### Scenario: Game drawn in the agent window
- **WHEN** Unknown is exhausted and the child plays a game that its code draws in the window of the Session Agent
- **THEN** the session is locked within 2 minutes
- **AND** the parent gets an alert
- **AND** the Session Agent still runs

#### Scenario: Child reads a prompt
- **WHEN** Unknown is exhausted and the child reads a prompt of the parent in the agent window for 30 seconds
- **THEN** the session is not locked

### Requirement: The set of Foreground Window exes fails closed
The system SHALL keep at least 60,000 exes per Restricted User and day in the set of Foreground Window exes. If the set is full, the system SHALL apply the Enforced Workstation Lock and notify the parent, because a new exe would not be a suspend candidate.

#### Scenario: Normal day
- **WHEN** the child uses 500 different programs on one day
- **THEN** each of them is a suspend candidate when its category is exhausted
