# Spec Delta

## ADDED Requirements

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
After the system resumes the Suspended Apps of a session because the session ends, it SHALL NOT suspend a process in that session for the Session End Hold (default 60 seconds). The hold SHALL end when the session ends or when its time is over. When the hold ends and the session still exists, the system SHALL suspend again, within 20 seconds, each process of the session whose exe still has no time left.

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

## MODIFIED Requirements

### Requirement: Suspend the programs of an exhausted category
When the Category Usage of a category reaches the limit of the day of an exe (its App Limit or else its Category Limit), the system SHALL suspend, within 70 seconds, each process of that exe in the session of that Restricted User, if the exe was a Foreground Window of that user today. The 70 seconds include the 10 seconds of the Pending Suspension. During a Session End Hold of the session, the system SHALL NOT suspend a process of that session. The session SHALL stay unlocked. The system SHALL NOT close or kill any of these processes.

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
If a recorded process runs again (for example, the child resumes it with a tool), the system SHALL suspend it again within 70 seconds while its exe still has no time left. The 70 seconds include the 10 seconds of the Pending Suspension. During a Session End Hold of its session, the system SHALL wait until the hold ends, and SHALL then suspend it again within 20 seconds.

#### Scenario: Child resumes a suspended game
- **WHEN** Games is exhausted and the child resumes the suspended game with a tool
- **THEN** the system suspends the game again within 70 seconds

#### Scenario: Recorded game during a hold
- **WHEN** the system resumed a recorded game for a sign-out and the hold is active
- **THEN** the system does not suspend the game until the hold ends
- **AND** the suspension record stays

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
