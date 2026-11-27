## MODIFIED Requirements

### Requirement: Count Category Usage from the Foreground Window
The system SHALL count Category Usage per Restricted User, App Category and day from the samples of the Session Probe, never from data of the Session Agent. For each sample it SHALL add the time since the previous sample of the session, measured with the time of the probe and limited by the gap rule, to the category of each counted process, at most once per category, and store whole minutes. It SHALL NOT count System, a locked session, or an exe that it suspended for this user.

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
- **THEN** the only counted process is `CalculatorApp.exe` in `C:\Program Files\WindowsApps`, and its time counts in Unknown

#### Scenario: False Study samples from the child
- **WHEN** a game is the Foreground Window and a process of the child sends Study exe samples to the service, also through its Session Agent
- **THEN** Category Usage for Games increases
- **AND** Category Usage for Study does not increase

#### Scenario: CPU load of the child
- **WHEN** the child plays a game in front for 3 minutes while its programs load each CPU core at the highest priority that a standard user can set
- **THEN** Category Usage for Games increases by 3 minutes, with a tolerance of 1 minute

#### Scenario: Gap between samples
- **WHEN** a Study program is the Foreground Window, the Session Probe gives no sample for 2 minutes, and then sends a Games sample
- **THEN** Category Usage for Games increases by at most 12 seconds for that sample
- **AND** the gap is one budget item

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
The child controls its Session Agent, so a session-end request through the Agent Channel is a claim of the child. The system SHALL grant at most one Session End Hold per Restricted User in 15 minutes that does not end with the end of the session. For a request outside this limit, no hold starts, the system resumes no process, and the requirement "Session End Guard" applies. The system SHALL log each request and each refusal.

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
- **THEN** the system suspends the game again within 20 seconds after the end of the guard, so the game runs for at most 80 seconds

#### Scenario: Sign-out, sign-in and sign-out again
- **WHEN** the child signs out with a suspended game, signs in again, and signs out again 5 minutes later with a suspended game
- **THEN** the system grants the second hold, because the first hold ended with a sign-out

### Requirement: Limit the Session End Guard
A Session End Guard SHALL cover only an eligible process. A covered process SHALL NOT be eligible until the system suspends it again, or sees it suspended while no guard covers it. A request without an eligible process SHALL NOT start a guard. A request during an active guard of the session SHALL NOT extend it. A guard SHALL NOT stop a new suspension and SHALL NOT count against the limit of Session End Holds. The system SHALL log the start of each guard.

#### Scenario: Fake request without a resume
- **WHEN** Games is exhausted, the game is suspended, a hold was refused, and the child makes its Session Agent send the session-end request without a sign-out
- **THEN** the game stays suspended

#### Scenario: Fake request after a granted hold
- **WHEN** the system resumed the game for a hold that ended without a sign-out, and the child makes its Session Agent send the session-end request during the Pending Suspension after the hold
- **THEN** the system refuses the request and the guard does not cover the game
- **AND** the system suspends the game at the end of the Pending Suspension

#### Scenario: Fake request after a guard
- **WHEN** a guard covered the game, the guard ended, and the child makes its Session Agent send the session-end request again during the Pending Suspension of the game
- **THEN** no new guard starts
- **AND** the system suspends the game at the end of the Pending Suspension

#### Scenario: Request without an eligible process
- **WHEN** a hold was refused and no process was eligible, Games then runs out and the system suspends a game, and the child signs out 30 seconds after the request
- **THEN** the system starts a guard that covers the game

## ADDED Requirements

### Requirement: The gap rule
A gap of 12 seconds or less between two samples SHALL count in full. A longer gap SHALL count in full, at most 60 seconds, if neither sample is a blind sample and both have the same counted processes (same process, same category). Else the system SHALL count at most 12 seconds and the gap SHALL be one budget item, never a reason for the lock at each blind sample. A gap over 60 seconds SHALL also be one budget item. The first sample after a sign-in, an unlock or a resume SHALL add nothing.

#### Scenario: Long gap with the same game
- **WHEN** a game is the Foreground Window and the next sample, 34 seconds later, shows the same game process
- **THEN** Category Usage for Games increases by 34 seconds for that sample
- **AND** no budget item results

#### Scenario: Long gap with another program
- **WHEN** a game is the Foreground Window and the next sample, 34 seconds later, shows a Study program
- **THEN** Category Usage for Study increases by at most 12 seconds for that sample
- **AND** the gap is one budget item

#### Scenario: Gap over a probe restart
- **WHEN** the Session Probe crashes while a game is the Foreground Window, and the first sample of the new probe shows the same game process
- **THEN** Category Usage for Games increases by the whole time since the last sample of the old probe, at most 60 seconds

### Requirement: The counted processes of a sample
The counted processes of a sample SHALL be the Foreground Window process, the focus process, each process with a visible child window below the Foreground Window, and each process under the cursor if the top-level window there has `WS_EX_NOACTIVATE`, except each one in System. The system SHALL record the exe of each process of a sample, also each other process under the cursor, as a Foreground Window exe of that user today.

#### Scenario: Game embedded in a System window
- **WHEN** the child puts a game window into a window of `notepad.exe`, the focus stays on Notepad, and the child plays the game with the mouse
- **THEN** the time counts in Games
- **AND** the game is suspended when Games is exhausted

#### Scenario: System window embedded in a game
- **WHEN** the child puts a window of `notepad.exe` into a game window and gives it the focus
- **THEN** the time counts in Games

#### Scenario: Study window embedded in a game
- **WHEN** the child puts a Study window into a game window, gives it the focus and plays the game with the mouse
- **THEN** the time counts in Games and in Study

#### Scenario: Game with WS_EX_NOACTIVATE
- **WHEN** a script of the child sets `WS_EX_NOACTIVATE` on a game window, a Study program is the Foreground Window, and the child plays the game with the mouse
- **THEN** the time counts in Games and in Study

#### Scenario: Game inside a WS_EX_NOACTIVATE System window
- **WHEN** a script of the child puts a game window into a window of `notepad.exe`, sets `WS_EX_NOACTIVATE` on the Notepad window, a Study program is the Foreground Window, and the child plays the game with the mouse
- **THEN** the time counts in Games and in Study
- **AND** the game is suspended when Games is exhausted

#### Scenario: Cursor on a background game
- **WHEN** a Study program is the Foreground Window and the cursor rests on the normal window of a game in the background
- **THEN** the time counts in Study only
- **AND** the game is suspended when Games is exhausted

### Requirement: WebView2 counts with the program that started it
A `msedgewebview2.exe` below `Microsoft\EdgeWebView\Application` in a Program Files folder SHALL count, in each role, as the first process in its chain of parent processes that is not such a file, and that process SHALL be recorded as a Foreground Window exe. If a parent ended or the chain cannot be checked, the entry SHALL count nothing and SHALL be no blind sample and no budget item. Each other `msedgewebview2.exe` SHALL count by its App Rule, like any other exe.

#### Scenario: WebView2 in a Study app
- **WHEN** a Study app is the Foreground Window and its `msedgewebview2.exe` control from the WebView2 Runtime has the focus
- **THEN** the time counts in Study only

#### Scenario: WebView2 in the shell
- **WHEN** the probe locks are on and Unknown is exhausted and the child reads "News and interests" of `explorer.exe` for 5 minutes
- **THEN** no Category Usage increases
- **AND** the session is not locked

#### Scenario: WebView2 popup of a Study app
- **WHEN** the probe locks are on and Unknown is exhausted and a Study app opens a WebView2 popup window that stays in front for 3 minutes
- **THEN** the time counts in Study only
- **AND** the session is not locked

#### Scenario: Second instance of a WebView2 app
- **WHEN** the probe locks are on, Unknown is exhausted, a Study app with a WebView2 control runs twice, the child closes the first instance, and the WebView2 control of the second instance has the focus for 3 minutes
- **THEN** the time counts in Study
- **AND** the session is not locked

#### Scenario: WebView2 control moved into Notepad
- **WHEN** a script of the child moves the WebView2 control of a Games app into a window of `notepad.exe`, and the child plays in it
- **THEN** the time counts in Games
- **AND** the Games app is suspended when Games is exhausted

#### Scenario: Game renamed as WebView2
- **WHEN** the probe locks are on and Unknown is exhausted, no App Rule covers the Downloads folder, and the child copies a game as `msedgewebview2.exe` into its Downloads folder, puts it into a Study app and plays it
- **THEN** the time counts in Unknown
- **AND** the session is locked within 2 minutes

### Requirement: A program that is never suspended still has a limit
The system SHALL count the Session Agent in Unknown as the Foreground Window, focus or child process, whatever the App Rules say, but not during an active prompt of the parent. If the category of a counted exe that the system never suspends and that is not in System is exhausted, and that exe counts for more than 60 seconds in 10 minutes of unlocked time, the system SHALL apply the suspension fallback as a probe lock. Time that the Agent Window Budget exempts SHALL NOT count.

#### Scenario: Game drawn in the agent window
- **WHEN** the probe locks are on and Unknown is exhausted and the child plays a game that its code draws in the window of the Session Agent
- **THEN** the session is locked within 2 minutes after the Agent Window Budget is used up
- **AND** the parent gets an alert
- **AND** the Session Agent still runs

#### Scenario: Agent window moved into Notepad
- **WHEN** the probe locks are on and Unknown is exhausted and a script of the child puts the window of the Session Agent, with a game drawn in it, into a window of `notepad.exe`
- **THEN** the session is locked within 2 minutes after the Agent Window Budget is used up

#### Scenario: Child reads a prompt
- **WHEN** the probe locks are on and Unknown is exhausted and the prompt of the parent stays in front for 3 minutes before the child answers it
- **THEN** the session is not locked
- **AND** Category Usage for Unknown does not increase

#### Scenario: Cursor on the widget
- **WHEN** the probe locks are on and Unknown is exhausted and the cursor rests on the widget of the Session Agent for 5 minutes
- **THEN** the session is not locked

### Requirement: The widget of the Session Agent does not take the activation
A click or a drag on the widget of the Session Agent SHALL NOT make the Session Agent the Foreground Window, and the widget SHALL take no keyboard focus. The buttons of the widget and its move SHALL still work.

#### Scenario: Child clicks the widget
- **WHEN** the probe locks are on, Unknown is exhausted, a video player of a category with time left is the Foreground Window, and the child drags the widget aside, clicks its collapse button, and then watches the video for 3 minutes without a click
- **THEN** the session is not locked
- **AND** Category Usage for Unknown does not increase
- **AND** the video player is still the Foreground Window

#### Scenario: Widget buttons
- **WHEN** the child clicks the expand button of the collapsed widget
- **THEN** the widget expands
- **AND** the Session Agent does not become the Foreground Window

### Requirement: The set of Foreground Window exes fails closed
The system SHALL keep at least 60,000 exes per Restricted User and day in the set of Foreground Window exes. If the set is full, the system SHALL apply the Enforced Workstation Lock as a probe lock and notify the parent, because a new exe would not be a suspend candidate.

#### Scenario: Normal day
- **WHEN** the child uses 500 different programs on one day
- **THEN** each of them is a suspend candidate when its category is exhausted

### Requirement: Agent Window Budget
In a sample of the Session Probe, the system SHALL exempt the Session Agent as a counted process, also as the creator of a trusted WebView2 process, for at most 5 minutes in any 60 minutes of unlocked time. Exempt time SHALL add no Category Usage for the Session Agent and SHALL NOT count toward the lock of a program that is never suspended. The system SHALL NOT start, extend or end the budget because of data that the Session Agent sends.

#### Scenario: Request while Unknown is exhausted
- **WHEN** the probe locks are on, the Session Probe counts the Session Agent in Unknown, Unknown is exhausted, and the child types a request in the Request Window for 3 minutes
- **THEN** the session is not locked
- **AND** Category Usage for Unknown does not increase

#### Scenario: Budget used up
- **WHEN** the probe locks are on, the Session Probe counts the Session Agent in Unknown, Unknown is exhausted, and the Session Agent is the counted process for 7 minutes within 60 minutes of unlocked time
- **THEN** the last 2 minutes count under the rules of `category-limits`

#### Scenario: Sliding window
- **WHEN** the probe locks are on, the Session Agent was exempt for 5 minutes from 10:00, and the session stays unlocked
- **THEN** the Session Agent is not exempt again before 11:00
- **AND** from 11:05 at the latest the budget has 5 minutes again

### Requirement: The Agent Window Budget exempts only the Session Agent
The Agent Window Budget SHALL exempt only the time that the Session Agent itself adds. If another counted process of the same sample is in the same App Category, that category SHALL still get the interval of the sample. The budget SHALL NOT change the Allowance Time, Continuous Use, a Break or the Schedule Window.

#### Scenario: Game moved into the agent window
- **WHEN** the probe locks are on and the Session Agent and a game in Unknown are both counted processes of the same Session Probe samples for 3 minutes
- **THEN** Category Usage for Unknown increases by 3 minutes

#### Scenario: Allowance Time
- **WHEN** the probe locks are on and the child types a request in the Request Window for 3 minutes while the Agent Window Budget exempts the Session Agent
- **THEN** the 3 minutes count against the Allowance Time of the child
