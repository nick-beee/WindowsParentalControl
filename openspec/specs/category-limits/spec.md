# category-limits Specification

## Purpose
Counts how long a Restricted User uses each App Category per day and pauses the programs of a category when its daily limit ends, without closing them and without locking the session.

## Requirements

### Requirement: Count Category Usage from the Foreground Window
The system SHALL count Category Usage per Restricted User, App Category and day from the Foreground Window samples of the Session Agent. It SHALL add the sample time to the category of the sampled exe, and SHALL store whole minutes. For a Store app, the sampled exe SHALL be the exe of the app, not the window host `ApplicationFrameHost.exe`. It SHALL NOT count System, SHALL NOT count while the session is locked, and SHALL NOT count a sample of an exe that it suspended for this user.

#### Scenario: Three samples of a game
- **WHEN** the agent reports a Games exe as Foreground Window in three samples of 20 seconds each, in an unlocked session
- **THEN** Category Usage for Games increases by 1 minute

#### Scenario: Locked session
- **WHEN** the agent reports a sample while the session is locked
- **THEN** no Category Usage increases

#### Scenario: System program
- **WHEN** the agent reports `C:\Windows\explorer.exe` as Foreground Window
- **THEN** no Category Usage increases

#### Scenario: Suspended game in front
- **WHEN** the system suspended a game and its frozen window stays the Foreground Window
- **THEN** no Category Usage increases

#### Scenario: Store app
- **WHEN** the Windows Calculator from the Microsoft Store is the Foreground Window
- **THEN** the agent reports `CalculatorApp.exe` in `C:\Program Files\WindowsApps`, and its time counts in Unknown

### Requirement: Effective Category Limit of the day
The Category Limit of a day SHALL be the weekend limit on Saturday and Sunday and the weekday limit on the other days, after the local date of the computer, plus today's Bonus Minutes of the Restricted User. A category without a limit SHALL have no Category Limit.

#### Scenario: Saturday
- **WHEN** it is Saturday and Games has the weekday limit 120 and the weekend limit 180
- **THEN** the Games Category Limit today is 180 minutes

#### Scenario: Bonus Minutes
- **WHEN** it is Tuesday, Games has the weekday limit 120, and the parent approves 30 Bonus Minutes today
- **THEN** the Games Category Limit today is 150 minutes

### Requirement: App Limit replaces the Category Limit for its app
For an exe whose rule has an App Limit, the limit of the day SHALL be the App Limit (weekday or weekend, plus Bonus Minutes) instead of the Category Limit. The time of that exe SHALL still count toward its category, and the exe SHALL stop when the Category Usage reaches its App Limit.

#### Scenario: Game with a longer App Limit
- **WHEN** Games has the weekday limit 120, the Minecraft rule has the App Limit 180, and the Games usage reaches 120 minutes on a Tuesday
- **THEN** the other Games programs are suspended
- **AND** Minecraft keeps running

#### Scenario: Longer App Limit reached
- **WHEN** the Games usage then reaches 180 minutes
- **THEN** Minecraft is suspended

#### Scenario: App Limit shorter than the category
- **WHEN** Games has the weekday limit 120, the Roblox rule has the App Limit 60, and the Games usage reaches 60 minutes
- **THEN** Roblox is suspended and the other Games programs keep running

### Requirement: Warnings before a Category Limit ends
The system SHALL send a session message to the child when 5 minutes and when 1 minute of a Category Limit or of an App Limit remain. It SHALL send each warning at most one time per limit and day. An App Limit warning SHALL name the app.

#### Scenario: Five minutes left
- **WHEN** Games usage reaches 115 of 120 minutes
- **THEN** the child gets one message that Games time ends in 5 minutes

### Requirement: Suspend the programs of an exhausted category
When the Category Usage of a category reaches the limit of the day of an exe (its App Limit or else its Category Limit), the system SHALL suspend, within 60 seconds, each process of that exe in the session of that Restricted User, if the exe was a Foreground Window of that user today. The session SHALL stay unlocked. The system SHALL NOT close or kill any of these processes.

#### Scenario: Games limit ends while a game runs
- **WHEN** Games usage reaches the Category Limit while a Steam game and a Study program run
- **THEN** the game process is suspended and still exists
- **AND** the Study program still runs
- **AND** the session is not locked

#### Scenario: Game starts after the limit ended
- **WHEN** Games is exhausted for today and the child starts a Games exe and brings it to the front
- **THEN** the system suspends that process within 60 seconds after the agent reports it

#### Scenario: Background helper of an exhausted category
- **WHEN** Unknown is exhausted and an Unknown tray helper runs that was never a Foreground Window today
- **THEN** the helper keeps running

### Requirement: Keep a suspended process suspended
If a recorded process runs again (for example, the child resumes it with a tool), the system SHALL suspend it again within 60 seconds while its exe still has no time left.

#### Scenario: Child resumes a suspended game
- **WHEN** Games is exhausted and the child resumes the suspended game with a tool
- **THEN** the system suspends the game again within 60 seconds

### Requirement: Fallback when a process cannot be suspended
If the system cannot suspend a process that it must suspend (for example, anti-cheat software blocks it), it SHALL lock the session with the Enforced Workstation Lock and SHALL notify the parent through the Alert Channel. It SHALL NOT close or kill the process.

#### Scenario: Suspension refused
- **WHEN** Games is exhausted and the system gets "access denied" when it tries to suspend a game process
- **THEN** the session is locked
- **AND** the parent gets an alert that names the exe
- **AND** the game process still exists

#### Scenario: Unlock while the process still cannot be suspended
- **WHEN** the session is locked because of this fallback and the child unlocks it on the same day while Games is still exhausted
- **THEN** the system tries the suspension again and locks the session again if it fails

### Requirement: Tell the child about a suspension
When the system suspends the programs of a category, it SHALL send one session message per category and day. The message SHALL name the category and SHALL say that the programs are paused, not closed, and continue tomorrow or after Bonus Minutes. The parent SHALL be able to change the text.

#### Scenario: Message on suspension
- **WHEN** the system suspends the Games programs for the first time today
- **THEN** the child gets one message that names Games and says the programs are paused

### Requirement: Mute suspended programs
The Session Agent SHALL mute the audio sessions of the suspended processes and SHALL restore their previous mute state on resume. If the agent does not run, the suspension SHALL still happen.

#### Scenario: Video player with sound
- **WHEN** the system suspends a Video process that plays sound
- **THEN** the audio of that process is muted

### Requirement: Only the child's processes
The system SHALL suspend only processes that run in a session of the Restricted User whose limit ended. It SHALL NOT suspend processes of another user or session, processes in System, the Session Agent, `msedgewebview2.exe`, an exe below the Windows folder (also if no App Rule puts it in System), or a process whose exe path it cannot read.

#### Scenario: WebView2 host
- **WHEN** Unknown is exhausted and a Study app uses `msedgewebview2.exe`
- **THEN** `msedgewebview2.exe` keeps running

#### Scenario: Windows rule deleted
- **WHEN** the parent deletes the default rule for the Windows folder and Unknown is exhausted
- **THEN** `explorer.exe` and Task Manager keep running

#### Scenario: Admin session
- **WHEN** an Administrator runs the same game in another session at the same time
- **THEN** the game of the Administrator keeps running

### Requirement: Resume suspended programs
The system SHALL resume a suspended process when its exe has time left again under its limit of the day: on a new day, after new Bonus Minutes, after the parent raises or removes a Category Limit or an App Limit, or after an App Rule change gives the exe a limit with time left. It SHALL resume all processes it suspended when the service stops.

#### Scenario: Next morning
- **WHEN** a game was suspended yesterday and the date changes
- **THEN** the system resumes the game within 60 seconds of the first tick of the new day

#### Scenario: Bonus Minutes approved
- **WHEN** Games is exhausted and the parent approves 15 Bonus Minutes
- **THEN** the system resumes the Games processes within 60 seconds

#### Scenario: Service stop
- **WHEN** the service stops while games are suspended
- **THEN** the system resumes the suspended games before it exits

### Requirement: Suspensions survive a service restart
The system SHALL record each process it suspends. After a service start, it SHALL resume each recorded process that still exists and whose exe has time left, and SHALL forget records of processes that no longer exist. It SHALL NOT resume a process that it did not suspend.

#### Scenario: Crash while games are suspended
- **WHEN** the service crashes while a game is suspended and starts again on the same day with Games still exhausted
- **THEN** the game stays suspended and the record stays

#### Scenario: Process ID reused
- **WHEN** a recorded process ended and a new process has the same process ID
- **THEN** the system does not resume or suspend the new process because of the old record

### Requirement: Session limits stay unchanged
The Allowance Time of the session, the Schedule Window and the Enforced Workstation Lock SHALL work as before. Category Limits SHALL NOT lock the session, except in the fallback when a process cannot be suspended.

#### Scenario: Allowance Time ends first
- **WHEN** the session Allowance Time ends while Games still has time left
- **THEN** the session is locked as before
