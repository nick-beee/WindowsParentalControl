# Spec Delta

## Purpose

Gives the service the Foreground Window of each session of a Restricted User from a helper that the child cannot stop, read or change, so that no limit depends on data of the Session Agent. A child action that hides the Foreground Window from the service locks the session.

## ADDED Requirements

### Requirement: Start a Session Probe in each session of a Restricted User
The system SHALL run exactly one Session Probe in each session of a Restricted User that exists, locked or not. It SHALL start the probe at the sign-in and at the unlock of the child, and within 60 seconds after the start of the service or the end of a probe. It SHALL NOT start a Session Probe in the session of an Administrator or of a user who is not restricted.

#### Scenario: Sign-in of the child
- **WHEN** the Restricted User signs in
- **THEN** one Session Probe runs in the session of the child within 10 seconds

#### Scenario: Service restart
- **WHEN** the service restarts while the child is signed in
- **THEN** the Session Probe of the old service run has ended
- **AND** one new Session Probe runs in the session of the child within 60 seconds

#### Scenario: Session of an Administrator
- **WHEN** an Administrator signs in
- **THEN** no Session Probe runs in that session

### Requirement: The child cannot stop or change the Session Probe
The Session Probe SHALL run as SYSTEM with no privilege that it does not need. A process of the Restricted User SHALL NOT be able to open the probe process, its threads or its token, end or suspend it, read or write its memory, or write to its pipe. The probe SHALL NOT use environment variables, files or folders that the Restricted User can change, and SHALL NOT open a process of the child.

#### Scenario: Task Manager
- **WHEN** the child selects the Session Probe in Task Manager and clicks "End task"
- **THEN** Windows denies the access
- **AND** the Session Probe still runs

#### Scenario: Startup hook of the child
- **WHEN** the child sets the user environment variable `DOTNET_STARTUP_HOOKS` to its own file, signs out and signs in again
- **THEN** the Session Probe does not load that file

#### Scenario: Window hook of the child
- **WHEN** a program of the child sets a global window hook with its own DLL
- **THEN** the DLL is not loaded into the Session Probe

#### Scenario: Service stops
- **WHEN** the service stops or ends with an error
- **THEN** the Session Probe ends within 10 seconds

### Requirement: The Session Probe shows nothing
The Session Probe SHALL create no window of any kind, also no hidden or message-only window, and SHALL show no console and no error dialog, also when it fails to start or crashes. It SHALL run without a console and without a console host.

#### Scenario: No console host
- **WHEN** the Session Probe runs in the session of the child
- **THEN** no `conhost.exe` process belongs to the Session Probe

#### Scenario: Probe crash
- **WHEN** the Session Probe crashes in the session of the child
- **THEN** no error window appears in that session
- **AND** the service starts a new probe within 60 seconds

### Requirement: Sample the Foreground Window of the input desktop
The Session Probe SHALL sample the input desktop of its session at random times, at least 0.2 seconds, about 5 seconds on average and at most 10 seconds apart, also if the child created that desktop. Each sample SHALL carry the time of the probe at the sample. A sample SHALL give the window handle, thread ID and process ID of the Foreground Window. It SHALL NOT give an empty process for a found window.

#### Scenario: Game in front
- **WHEN** the child plays a game in a window that has the input focus
- **THEN** the service gets a sample with the process of the game within 10 seconds

#### Scenario: Game on a second desktop
- **WHEN** the child creates a second desktop with a tool, switches to it and starts a game there
- **THEN** the service gets samples with the process of the game

#### Scenario: Strange window title
- **WHEN** a game in front has a window title of 10,000 characters with characters that are not valid text
- **THEN** the service still gets samples with the process of the game
- **AND** Category Usage for Games increases

#### Scenario: Many desktop switches
- **WHEN** the child switches between two desktops every few seconds for one hour
- **THEN** the handle count of the Session Probe does not grow

### Requirement: Sample the other windows that the child can use
A sample SHALL also give the focus window if another process owns it, one visible child window of each other process below the Foreground Window, and the top-level window under the cursor if another process owns it, with its `WS_EX_NOACTIVATE` style. If more than 8 other processes have visible windows below the Foreground Window, the sample SHALL be a blind sample.

#### Scenario: Store app
- **WHEN** the Windows Calculator from the Microsoft Store is the Foreground Window
- **THEN** the service reads `CalculatorApp.exe` in `C:\Program Files\WindowsApps` for the sample

#### Scenario: Game inside a System window
- **WHEN** a script of the child puts the window of a game into a window of `notepad.exe` with `SetParent`, and the focus stays on the edit control of Notepad while the child plays the game with the mouse
- **THEN** the sample gives the game process as a child window process

#### Scenario: Game behind decoy windows
- **WHEN** the child puts windows of 8 other processes and a game into a window of `notepad.exe`
- **THEN** the sample is a blind sample

#### Scenario: Game that never becomes the Foreground Window
- **WHEN** a script of the child sets `WS_EX_NOACTIVATE` on the window of a game and the child plays it with the mouse
- **THEN** the sample gives the game process as the window under the cursor, with the style `WS_EX_NOACTIVATE`

### Requirement: The service reads the process of a sample itself
The service SHALL read the exe path, the session and the start time of each process in a sample itself, with `SeDebugPrivilege` enabled, and SHALL check that the thread belongs to that process. If the process ended, that process SHALL count no time; an ended Foreground Window process SHALL also count in the budget of blind samples, but SHALL NOT lock at once. If the service cannot read it for another reason, the sample SHALL be a blind sample.

#### Scenario: Game that denies SYSTEM
- **WHEN** the child removes SYSTEM from the DACL of its game process and plays the game
- **THEN** the service reads the exe of the game and counts its time in Games

#### Scenario: Fake Store app window
- **WHEN** a game of the child registers the window class `ApplicationFrameWindow` and shows a child window of a Study program
- **THEN** the time counts in Games

#### Scenario: Game window on a new thread before each read
- **WHEN** the child makes the service slow with a CPU load and again and again re-creates its game window on a new thread and ends the old thread, so that the service finds the thread of the Foreground Window ended
- **THEN** the session is locked within 2 minutes

### Requirement: Lock the session after blind samples
A blind sample is one that the service cannot attribute to an exe: the input desktop is not readable, or the service cannot read a process of the sample. At the 6th blind sample within 10 minutes of unlocked, connected time of a Restricted User, the system SHALL apply the Enforced Workstation Lock and notify the parent through the Alert Channel at most once per day. After that lock, each blind sample of that day SHALL lock. It SHALL NOT close a program.

#### Scenario: Desktop that denies the probe
- **WHEN** the child creates a desktop whose security gives access only to the child, switches to it and starts a game there
- **THEN** the session is locked within 60 seconds
- **AND** the parent gets an alert that names the child
- **AND** the game process still exists

#### Scenario: Short returns to the default desktop
- **WHEN** the child plays on a desktop that denies the probe for 25 seconds and then returns to the default desktop for 5 seconds, again and again
- **THEN** the session is locked within 2 minutes

#### Scenario: Unlock and the same desktop again
- **WHEN** the session was locked for this reason and the child unlocks it and switches to that desktop again on the same day
- **THEN** the session is locked again within 15 seconds

#### Scenario: Fast user switch
- **WHEN** an Administrator switches to their account while the session of the child stays signed in
- **THEN** samples of the disconnected session of the child count as no blind sample

#### Scenario: Windows secure desktops
- **WHEN** the child sees a UAC prompt, presses Ctrl+Alt+Del, or the secure screen saver starts
- **THEN** no blind sample results and the session is not locked for this reason

### Requirement: No category time without Session Probe samples
If the service gets no sample from the Session Probe of an unlocked, connected session for more than 12 seconds (the probe does not run or does not answer), it SHALL add no Category Usage for that time. It SHALL start a new probe within 60 seconds if the probe ended. After 5 minutes without samples it SHALL write an event and alert the parent, at most once per Restricted User and hour. It SHALL NOT use data of the Session Agent instead.

#### Scenario: Probe cannot start
- **WHEN** the Session Probe of the child cannot start for 5 minutes in an unlocked session
- **THEN** the system writes an event to the event log
- **AND** the parent gets one Telegram alert that names the child
- **AND** the session Allowance Time still counts

#### Scenario: Agent samples during a probe outage
- **WHEN** the Session Probe gives no samples and a process of the child sends Foreground Window data to the service
- **THEN** no Category Usage increases because of that data

### Requirement: The Session Probe ends with its session
The system SHALL end the Session Probe of a session when the session ends, and the probe SHALL NOT delay or block the end of the session. The Session Probe SHALL NOT close, suspend or resume a program.

#### Scenario: Sign-out
- **WHEN** the child signs out
- **THEN** the sign-out completes as without the Session Probe
- **AND** no Session Probe of that session runs after the end of the session
