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
The system SHALL send a session message to the child when 5 minutes and when 1 minute of a Category Limit or of an App Limit remain. It SHALL send each warning at most one time per limit and day. An App Limit warning SHALL name the app. It SHALL send an App Limit warning only while a process of an exe that the App Limit covers runs in a session of that Restricted User, as the process list of the service shows it, also if that process starts late.

#### Scenario: Five minutes left
- **WHEN** Games usage reaches 115 of 120 minutes
- **THEN** the child gets one message that Games time ends in 5 minutes

#### Scenario: App not running: no warning
- **WHEN** Games has the weekday limit 180, the Roblox rule has the App Limit 120, the Games usage reaches 115 minutes on a Tuesday, and no Roblox process runs in the session of the child
- **THEN** the child gets no message that Roblox ends

#### Scenario: App starts with 3 minutes left: one warning
- **WHEN** the Roblox rule has the App Limit 120, no Roblox process ran while the Games usage went from 115 to 117 minutes, and the child then starts Roblox
- **THEN** the child gets one message that Roblox ends in 3 minutes within 60 seconds
- **AND** the child gets no second 5-minute message for Roblox today

#### Scenario: App closed before the last minute
- **WHEN** the child got the 5-minute message for Roblox, then closes Roblox, and the Games usage reaches 119 of 120 minutes
- **THEN** the child gets no 1-minute message for Roblox

#### Scenario: Category Limit warning without a running program
- **WHEN** Games usage reaches 115 of 120 minutes and no Games program runs
- **THEN** the child gets one message that Games time ends in 5 minutes

### Requirement: Suspend the programs of an exhausted category
When the Category Usage of a category reaches the limit of the day of an exe (its App Limit or else its Category Limit), the system SHALL suspend, within 70 seconds, each process of that exe in the session of that Restricted User, if the exe was a Foreground Window of that user today. The 70 seconds include the 10 seconds of the Pending Suspension. The requirement "Session End Hold" adds an exception. The session SHALL stay unlocked. The system SHALL NOT close or kill any of these processes.

#### Scenario: Games limit ends while a game runs
- **WHEN** Games usage reaches the Category Limit while a Steam game and a Study program run
- **THEN** the game process is suspended and still exists
- **AND** the Study program still runs
- **AND** the session is not locked

#### Scenario: Game starts after the limit ended
- **WHEN** Games is exhausted for today and the child starts a Games exe and brings it to the front
- **THEN** the system suspends that process within 70 seconds after the agent reports it

#### Scenario: Background helper of an exhausted category
- **WHEN** Unknown is exhausted and an Unknown tray helper runs that was never a Foreground Window today
- **THEN** the helper keeps running

#### Scenario: Limit ends during a sign-out
- **WHEN** a Session End Hold of the session is active and Games usage reaches the Category Limit
- **THEN** the system does not suspend the game during the hold

### Requirement: Keep a suspended process suspended
If a recorded process runs again (for example, the child resumes it with a tool), the system SHALL suspend it again within 70 seconds while its exe still has no time left or while it is still a Blocked Program. The 70 seconds include the 10 seconds of the Pending Suspension. During a Session End Hold of its session, the system SHALL wait until the hold ends, and SHALL then suspend it again within 20 seconds.

#### Scenario: Child resumes a suspended game
- **WHEN** Games is exhausted and the child resumes the suspended game with a tool
- **THEN** the system suspends the game again within 70 seconds

#### Scenario: Recorded game during a hold
- **WHEN** the system resumed a recorded game for a sign-out and the hold is active
- **THEN** the system does not suspend the game until the hold ends
- **AND** the suspension record stays

#### Scenario: Child resumes a suspended blocked browser
- **WHEN** `chrome.exe` is blocked, a Chrome process is suspended because of the block, Unknown has time left, and the child resumes Chrome with a tool
- **THEN** the system suspends Chrome again within 70 seconds

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
The system SHALL resume a suspended process when its exe has time left again under its limit of the day: on a new day, after new Bonus Minutes, after the parent raises or removes a Category Limit or an App Limit, or after an App Rule change gives the exe a limit with time left. It SHALL resume all processes it suspended when the service stops. It SHALL resume the processes of a session when that session ends, as the requirement "Resume Suspended Apps when the session ends" states.

#### Scenario: Next morning
- **WHEN** a game was suspended yesterday and the date changes
- **THEN** the system resumes the game within 60 seconds of the first tick of the new day

#### Scenario: Bonus Minutes approved
- **WHEN** Games is exhausted and the parent approves 15 Bonus Minutes
- **THEN** the system resumes the Games processes within 60 seconds

#### Scenario: Service stop
- **WHEN** the service stops while games are suspended
- **THEN** the system resumes the suspended games before it exits

### Requirement: Keep a blocked suspension
A process that the system suspended because its exe is a Blocked Program SHALL stay suspended while the block applies, also if its exe has time left, until the session ends or the service stops; `blocked-programs` defines when it resumes. A blocked suspension SHALL go through the Pending Suspension and the Session End Hold like a limit suspension; time left does not drop its Pending Suspension. During a Session End Hold, the system SHALL still close a Blocked Start.

#### Scenario: Blocked browser on a new day
- **WHEN** the system suspended a blocked Chrome yesterday, the block is still on, and the date changes
- **THEN** Chrome stays suspended

#### Scenario: Blocked browser with time left in its category
- **WHEN** a blocked Chrome is a Pending Suspension and Unknown has time left
- **THEN** the system suspends Chrome at the end of the 10 seconds

#### Scenario: Chrome start during a Session End Hold
- **WHEN** a Session End Hold of the child's session runs and the child starts Chrome, and `chrome.exe` is blocked
- **THEN** the system closes Chrome at its start

### Requirement: Suspensions survive a service restart
The system SHALL record each process it suspends, with the reason (a limit or a block). After a service start, it SHALL resume each recorded process that still exists and that has no reason to stay suspended: for a limit, its exe has time left; for a block, its exe is no longer a Blocked Program and has time left. It SHALL forget records of processes that no longer exist. It SHALL NOT resume a process that it did not suspend.

#### Scenario: Crash while games are suspended
- **WHEN** the service crashes while a game is suspended and starts again on the same day with Games still exhausted
- **THEN** the game stays suspended and the record stays

#### Scenario: Process ID reused
- **WHEN** a recorded process ended and a new process has the same process ID
- **THEN** the system does not resume or suspend the new process because of the old record

#### Scenario: Crash while a blocked browser is suspended
- **WHEN** the service crashes while a blocked Firefox is suspended and starts again with the block still on and Unknown with time left
- **THEN** Firefox stays suspended and the record stays

### Requirement: Session limits stay unchanged
The Allowance Time of the session, the Schedule Window and the Enforced Workstation Lock SHALL work as before. Category Limits SHALL NOT lock the session, except in the fallback when a process cannot be suspended.

#### Scenario: Allowance Time ends first
- **WHEN** the session Allowance Time ends while Games still has time left
- **THEN** the session is locked as before

### Requirement: Resume Suspended Apps when the session ends
When Windows starts to end the session of a Restricted User (sign-out, restart or shutdown), the system SHALL resume the Suspended Apps of that session before Windows asks them to close. A Suspended App SHALL NOT cause Windows to show that it prevents the end of the session. The system SHALL NOT block or cancel the end of the session.

#### Scenario: Sign-out with a suspended game
- **WHEN** Games is exhausted, a game is suspended, a Study program runs, and the child signs out
- **THEN** Windows does not show that an app prevents the sign-out
- **AND** the game gets the end-session query while it runs, and closes as in a normal sign-out
- **AND** the sign-out completes

#### Scenario: Shutdown with a game suspended for a long time
- **WHEN** a game was suspended two hours ago and the child shuts down the computer
- **THEN** Windows does not show that an app prevents the shutdown
- **AND** the shutdown completes

#### Scenario: Restart
- **WHEN** a game is suspended and the child restarts the computer
- **THEN** Windows does not show that an app prevents the restart
- **AND** the restart completes

#### Scenario: Unsaved work in another program
- **WHEN** a game is suspended, Notepad has unsaved text, and the child signs out
- **THEN** Notepad asks to save, as in a sign-out without a Suspended App
- **AND** the game is not the program that blocks the sign-out

#### Scenario: Installer closes the agent only
- **WHEN** an installer asks only the Session Agent to close, and the session does not end
- **THEN** the Suspended Apps stay suspended

### Requirement: Session End Hold
After the system resumes the Suspended Apps of a session because the session ends, it SHALL NOT suspend a process in that session for the Session End Hold (default 60 seconds). The hold SHALL end when the session ends or when its time is over. When the hold ends and the session still exists, the system SHALL suspend again, within 20 seconds, each process of the session whose exe still has no time left or is still a Blocked Program.

#### Scenario: Tick during the sign-out
- **WHEN** the system resumed a game for a sign-out and its 60-second tick runs 5 seconds later, before Windows closed the game
- **THEN** the system does not suspend the game

#### Scenario: Sign-out cancelled by the child
- **WHEN** the system resumed a game for a sign-out, Notepad asks to save, and the child clicks Cancel
- **THEN** the session stays open
- **AND** Windows closed each program that answered the end-session query before Notepad, as in a sign-out without a Suspended App
- **AND** the hold ends by its time and counts as a hold that did not end with the end of the session
- **AND** the system suspends again, within 20 seconds after the end of the hold, each game process that still exists, while Games is still exhausted

#### Scenario: Sign-out completes
- **WHEN** the system resumed a game for a sign-out and the sign-out completes
- **THEN** the system keeps no suspension record of the processes of that session
- **AND** the next sign-in of the child starts without a hold

#### Scenario: Suspended blocked browser at a cancelled sign-out
- **WHEN** a blocked Chrome is suspended, the system resumes it for a sign-out, and the child cancels the sign-out
- **THEN** the system does not suspend Chrome during the hold
- **AND** the system suspends Chrome again, within 20 seconds after the end of the hold, while `chrome.exe` is still blocked

### Requirement: Limit the Session End Hold from the agent
A request from the Session Agent is not authenticated, so the system SHALL grant at most one Session End Hold per Restricted User in 15 minutes that does not end with the end of the session. For a request outside this limit, no hold starts and the requirement "Keep a suspended process suspended" applies without the hold exception. The system SHALL log each request and each refusal.

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
- **THEN** the system suspends the game again within 70 seconds

#### Scenario: Sign-out, sign-in and sign-out again
- **WHEN** the child signs out with a suspended game, signs in again, and signs out again 5 minutes later with a suspended game
- **THEN** the system grants the second hold, because the first hold ended with a sign-out

### Requirement: Report an unconfirmed Session End Hold
When a Session End Hold ends by its time and the session still exists, the system SHALL write an event to the event log. It SHALL also send the parent a Telegram alert that the sign-out did not complete, at most one alert per Restricted User per day.

#### Scenario: Sign-out does not complete
- **WHEN** the system granted a hold and the hold ends by its timeout while the session still exists
- **THEN** the system writes an event to the event log
- **AND** the parent gets a Telegram alert, if the child had no such alert today

#### Scenario: Second unconfirmed hold on the same day
- **WHEN** the parent got an alert for an unconfirmed hold today, and a later hold of the same child also ends without the end of the session
- **THEN** the system writes an event to the event log
- **AND** the system does not send a second Telegram alert today

### Requirement: Resume before a Forced Logoff
Before the system logs off a session itself (Forced Logoff), it SHALL resume the Suspended Apps of that session and start a Session End Hold for it. The rate limit of agent requests SHALL NOT apply to this hold.

#### Scenario: Parent signs the child out from the admin page
- **WHEN** a game of the child is suspended and the parent uses Forced Logoff on the admin page
- **THEN** the system resumes the game before it logs off the session

### Requirement: Minimize a process before its suspension
Before the system suspends a process, it SHALL make the process a Pending Suspension for 10 seconds, and the Session Agent SHALL minimize the visible top-level windows of that process while it still runs. The system SHALL suspend the process at the end of the 10 seconds, also if the agent does not run or does not answer. The system SHALL drop a Pending Suspension without a suspension if the exe has time left again, if the process ended, or if a Session End Hold of the session started.

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
