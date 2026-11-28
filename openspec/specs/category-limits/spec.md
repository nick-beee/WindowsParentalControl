# category-limits Specification

## Purpose
Counts how long a Restricted User uses each App Category per day and pauses the programs of a category when its daily limit ends, without closing them and without locking the session.

## Requirements

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

### Requirement: Fallback when a process cannot be suspended
If the system cannot suspend a process that it must suspend (for example, anti-cheat software blocks it), it SHALL lock the session with the Enforced Workstation Lock and SHALL notify the parent through the Alert Channel. It SHALL NOT close or kill the process. A Lock Deferral delays this lock (capability session-lock).

#### Scenario: Suspension refused
- **WHEN** Games is exhausted and the system gets "access denied" when it tries to suspend a game process
- **THEN** the session is locked
- **AND** the parent gets an alert that names the exe
- **AND** the game process still exists

#### Scenario: Unlock while the process still cannot be suspended
- **WHEN** the session is locked because of this fallback and the child unlocks it on the same day while Games is still exhausted
- **THEN** the system tries the suspension again and locks the session again if it fails

### Requirement: Tell the child about a suspension
When the system suspends a program because its exe has no time left, it SHALL send one session message per limit and day. The message SHALL name the limit: the app for an App Limit, the category for a Category Limit. It SHALL say that the programs are paused, not closed, and continue tomorrow or after Bonus Minutes. In the one text that the parent can change, `{name}` is the app or the category and `{category}` is the category.

#### Scenario: Message on suspension
- **WHEN** the system suspends the Games programs for the first time today because of the Games Category Limit
- **THEN** the child gets one message that names Games and says the programs are paused

#### Scenario: App Limit names the app
- **WHEN** Games has the weekday limit 180, the `TestGame.exe` rule has the App Limit 120, and the Games usage reaches 120 minutes on a Tuesday while TestGame runs
- **THEN** TestGame is suspended and the child gets one message that names TestGame and says it is paused
- **AND** the message does not name Games as the program that stops

#### Scenario: Second process of the same App Limit
- **WHEN** the child got the message for TestGame today and then starts TestGame again, and the system suspends it
- **THEN** the child gets no second message for TestGame today

#### Scenario: App Limit and Category Limit on one day
- **WHEN** the child got the message for TestGame at 120 minutes, and the Games usage then reaches the Games limit of 180 minutes on the same day while another game runs
- **THEN** the system suspends the other game and the child gets one message that names Games

### Requirement: Mute suspended programs
The Session Agent SHALL mute the audio sessions of the suspended processes and SHALL restore their previous mute state on resume. If the agent does not run, the suspension SHALL still happen.

#### Scenario: Video player with sound
- **WHEN** the system suspends a Video process that plays sound
- **THEN** the audio of that process is muted

### Requirement: Only the child's processes
The system SHALL suspend only processes in a session of the Restricted User whose limit ended. It SHALL NOT suspend processes of another user or session, processes in System, the Session Agent, the Trusted WebView2 Runtime, an exe below the Windows folder (also without an App Rule for System), or a process whose exe path it cannot read. It SHALL suspend each other `msedgewebview2.exe` as any other exe.

#### Scenario: WebView2 host
- **WHEN** Unknown is exhausted and a Study app uses `msedgewebview2.exe` of the Trusted WebView2 Runtime
- **THEN** `msedgewebview2.exe` keeps running

#### Scenario: Game renamed as WebView2 is suspended
- **WHEN** the probe locks are on or off, Unknown is exhausted, no App Rule covers the Downloads folder, and the child plays a game in front that it copied as `C:\Users\Kid\Downloads\wv\msedgewebview2.exe`
- **THEN** the game is suspended within 70 seconds
- **AND** the session is not locked

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

### Requirement: Session End Guard
When the system refuses a session-end request of a Restricted User, it SHALL start a Session End Guard (60 seconds) for that session if it covers a process. A guard covers each eligible process of the session: a recorded process that the system suspended, or saw suspended while no guard covered it, and did not resume itself since. During the guard it SHALL NOT suspend a covered process again. The guard SHALL end when the session ends or its time is over. A guard SHALL NOT resume a process.

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

### Requirement: Report a repeated Session End Guard
A guard that ends by its time while its session still exists is unconfirmed. When a guard starts after an unconfirmed guard of the same Restricted User in the last 15 minutes, the system SHALL write an event to the event log and SHALL send the parent a Telegram alert, at most one alert per Restricted User per day. A repeated guard SHALL cover the same processes as another guard. A guard SHALL NOT lock the session.

#### Scenario: Sign-out and Cancel again and again
- **WHEN** the child cancelled a sign-out under a guard, the game is suspended again, and the child signs out and cancels again within 15 minutes
- **THEN** the system starts a guard that covers the game, writes an event and alerts the parent
- **AND** the system suspends the game again within 20 seconds after the end of the guard

#### Scenario: Completed sign-out does not count
- **WHEN** an earlier guard of the child ended with the end of its session
- **THEN** the next guard of the child does not alert the parent

### Requirement: The gap rule
A gap of 12 seconds or less between two samples SHALL count in full. A longer gap SHALL count in full, at most 60 seconds, if no sample is blind and both have the same counted exes and categories, without those under the cursor. Else the system SHALL count at most 12 seconds, and the gap SHALL be one budget item that never locks at once. A gap over 60 seconds SHALL also be one item. A gap over a probe end SHALL be no item. The first sample after a sign-in, unlock or resume SHALL add nothing.

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

#### Scenario: Gap over a probe restart with another program
- **WHEN** the Session Probe crashes while a game is the Foreground Window, and the first sample of the new probe, 30 seconds later, shows a Study program
- **THEN** Category Usage for Study increases by at most 12 seconds for that sample
- **AND** the gap is no budget item

#### Scenario: Cursor moves during a long gap
- **WHEN** a game is the Foreground Window, and the next sample, 34 seconds later, shows the same game and another program under the cursor in a window with `WS_EX_NOACTIVATE`
- **THEN** Category Usage for Games increases by 34 seconds for that sample
- **AND** no budget item results

#### Scenario: Game restarted during a long gap
- **WHEN** a game is the Foreground Window, the child ends it and starts the same game exe again, and the next sample, 34 seconds later, shows the new game process
- **THEN** Category Usage for Games increases by 34 seconds for that sample

### Requirement: Long samples and gap alerts
A sample that took more than 12 seconds SHALL count at most 12 seconds for the gap that ends at it, and that gap SHALL be one budget item. The system SHALL write a `PROBE_GAP` event for each gap over 12 seconds and each such sample, except a gap over an end of the probe, and SHALL alert the parent through the Alert Channel at most once per Restricted User and day. A gap SHALL NOT lock the session by itself.

#### Scenario: Long sample
- **WHEN** a game is the Foreground Window and the next sample, 20 seconds later, shows the same game but took 15 seconds itself
- **THEN** Category Usage for Games increases by at most 12 seconds for that sample
- **AND** the gap is one budget item

#### Scenario: Gap alert
- **WHEN** the child plays under a CPU load that gives three gaps of 30 seconds on one day
- **THEN** the system writes three `PROBE_GAP` events
- **AND** the parent gets one alert on that day
- **AND** the session is not locked because of the gaps alone

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

### Requirement: The counted processes of a sample without a Foreground Window
In a sample without a Foreground Window, the counted processes SHALL be the processes under the cursor if the top-level window there has `WS_EX_NOACTIVATE`, except each one in System. A process under the cursor of such a sample that the service cannot read or that ended SHALL NOT make a blind sample or a budget item.

#### Scenario: Game with WS_EX_NOACTIVATE and no Foreground Window
- **WHEN** a script of the child sets `WS_EX_NOACTIVATE` on a game window and keeps the Foreground Window empty, and the child plays the game with the mouse
- **THEN** the time counts in Games
- **AND** the game is suspended when Games is exhausted

#### Scenario: Unreadable window under the cursor without a Foreground Window
- **WHEN** the probe locks are on, no window is the Foreground Window, and the cursor rests on a `WS_EX_NOACTIVATE` window of a process that the service cannot read
- **THEN** the sample is no blind sample and no budget item
- **AND** no time counts for that sample

### Requirement: WebView2 counts with the program that started it
A process of the Trusted WebView2 Runtime SHALL count, in each role, as the first process in its chain of parent processes that is not such a process, and that process SHALL be recorded as a Foreground Window exe. If a parent ended or the chain cannot be checked, the entry SHALL count nothing and SHALL be no blind sample and no budget item. Each other `msedgewebview2.exe` (a copy, a Fixed Version or per-user runtime) SHALL count as itself by its App Rule.

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
- **WHEN** the probe locks are on or off, Unknown is exhausted, no App Rule covers the Downloads folder, and the child copies a game as `msedgewebview2.exe` into its Downloads folder, puts it into a Study app and plays it
- **THEN** the time counts in Unknown
- **AND** the game is suspended within 70 seconds
- **AND** the session is not locked

#### Scenario: Fixed Version runtime with an App Rule
- **WHEN** a folder rule maps `C:\Program Files\StudyApp\` to Study, and the WebView2 control of that Study app runs its own `msedgewebview2.exe` below that folder and has the focus
- **THEN** the time counts in Study only

#### Scenario: Fixed Version runtime without an App Rule
- **WHEN** Unknown is exhausted, a Study app runs its own `msedgewebview2.exe` below `C:\Program Files\StudyApp\`, no App Rule covers that folder, and its WebView2 control has the focus
- **THEN** the time counts in Unknown
- **AND** that `msedgewebview2.exe` is suspended within 70 seconds
- **AND** the session is not locked

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
The system SHALL keep at least 60,000 exes per Restricted User and day in the set of Foreground Window exes. If the set is full, the system SHALL apply the Enforced Workstation Lock as a probe lock and notify the parent, because a new exe would not be a suspend candidate. An exe whose path is not an absolute Windows path SHALL NOT give this lock. Such an exe is never suspended, so the requirement "A program that is never suspended still has a limit" applies to it.

#### Scenario: Normal day
- **WHEN** the child uses 500 different programs on one day
- **THEN** each of them is a suspend candidate when its category is exhausted

#### Scenario: Exe path that is not absolute
- **WHEN** the probe locks are on, Unknown is exhausted, and a game in Unknown runs from a path without a drive letter, for example from a shadow copy
- **THEN** the system applies no lock for a full set
- **AND** the session is locked within 2 minutes under the rule of a program that is never suspended

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

#### Scenario: Only System programs after the budget
- **WHEN** the probe locks are on, the Session Agent was exempt for 5 minutes from 10:00, and from 10:05 to 11:10 only System programs are in front of the unlocked session
- **THEN** at 11:10 the budget has 5 minutes again

#### Scenario: Locked time does not refill the budget
- **WHEN** the probe locks are on, the Session Agent was exempt for 5 minutes from 10:00, and the session is locked from 10:05 to 12:00
- **THEN** the budget has no exempt time at the unlock at 12:00

### Requirement: The Agent Window Budget exempts only the Session Agent
The Agent Window Budget SHALL exempt only the time that the Session Agent that the service started adds. A process with the agent exe that the service did not start SHALL count in Unknown without the budget. If another counted process of the same sample is in the same App Category, that category SHALL still get the interval of the sample. The budget SHALL NOT change the Allowance Time, Continuous Use, a Break or the Schedule Window.

#### Scenario: Game moved into the agent window
- **WHEN** the probe locks are on and the Session Agent and a game in Unknown are both counted processes of the same Session Probe samples for 3 minutes
- **THEN** Category Usage for Unknown increases by 3 minutes

#### Scenario: Agent exe started by the child
- **WHEN** the probe locks are on, Unknown is exhausted, and the child starts `ParentalControl.Agent.exe` itself with a startup hook that draws a game in its window
- **THEN** that process gets no time from the Agent Window Budget
- **AND** the session is locked within 2 minutes

#### Scenario: Allowance Time
- **WHEN** the probe locks are on and the child types a request in the Request Window for 3 minutes while the Agent Window Budget exempts the Session Agent
- **THEN** the 3 minutes count against the Allowance Time of the child
