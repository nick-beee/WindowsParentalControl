# Spec Delta

## MODIFIED Requirements

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

### Requirement: Resume suspended programs
The system SHALL resume a suspended process when its exe has time left again under its limit of the day: on a new day, after new Bonus Minutes, after the parent raises or removes a Category Limit or an App Limit, or after an App Rule change gives the exe a limit with time left. It SHALL resume all processes it suspended when the service stops. It SHALL resume the processes of a session when that session ends, as the requirement "Resume Suspended Apps when the session ends" states. A process that the system suspended because its exe is a Blocked Program SHALL stay suspended while the block applies, also if its exe has time left; `blocked-programs` defines when it resumes.

#### Scenario: Next morning
- **WHEN** a game was suspended yesterday and the date changes
- **THEN** the system resumes the game within 60 seconds of the first tick of the new day

#### Scenario: Bonus Minutes approved
- **WHEN** Games is exhausted and the parent approves 15 Bonus Minutes
- **THEN** the system resumes the Games processes within 60 seconds

#### Scenario: Service stop
- **WHEN** the service stops while games are suspended
- **THEN** the system resumes the suspended games before it exits

#### Scenario: Blocked browser on a new day
- **WHEN** the system suspended a blocked Chrome yesterday, the block is still on, and the date changes
- **THEN** Chrome stays suspended

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

### Requirement: Session End Hold
After the system resumes the Suspended Apps of a session because the session ends, it SHALL NOT suspend a process in that session for the Session End Hold (default 60 seconds). The hold SHALL end when the session ends or when its time is over. When the hold ends and the session still exists, the system SHALL suspend again, within 20 seconds, each process of the session whose exe still has no time left or is still a Blocked Program. A Blocked Start SHALL still be closed during the hold, because `blocked-programs` closes a process only at its start.

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

### Requirement: Minimize a process before its suspension
Before the system suspends a process, also because of a block, it SHALL make the process a Pending Suspension for 10 seconds, and the Session Agent SHALL minimize the visible top-level windows of that process while it still runs. The system SHALL suspend the process at the end of the 10 seconds, also if the agent does not run or does not answer. The system SHALL drop a Pending Suspension without a suspension if the exe has time left again and is not a Blocked Program, if the process ended, or if a Session End Hold of the session started.

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
