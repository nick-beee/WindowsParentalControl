# Spec Delta

## Purpose

Gives the service the Foreground Window of each session of a Restricted User from a helper that the child cannot stop, read or change, so that no limit depends on data of the Session Agent. A child action that hides the Foreground Window from the service locks the session, after the parent switches the probe locks on.

## ADDED Requirements

### Requirement: Start a Session Probe in each session of a Restricted User
The system SHALL run exactly one Session Probe in each session of a Restricted User that exists, locked or not. It SHALL start the probe at the sign-in and at the unlock of the child, within 60 seconds after the start of the service, and within 10 seconds after an unexpected end of a probe. It SHALL NOT start a Session Probe in the session of an Administrator or of a user who is not restricted.

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
- **AND** the service starts a new probe within 10 seconds

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
A sample SHALL also give the focus window if another process owns it, one visible child window of each other process below the Foreground Window, and each other process of the window under the cursor and of its parent windows, with the `WS_EX_NOACTIVATE` style of their top-level window. If more than 8 other processes have visible windows below the Foreground Window, the sample SHALL be a blind sample.

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

#### Scenario: Game inside a WS_EX_NOACTIVATE System window
- **WHEN** a script of the child puts the window of a game into a window of `notepad.exe` with `SetParent`, sets `WS_EX_NOACTIVATE` on the Notepad window, and the child plays the game with the mouse while another window is the Foreground Window
- **THEN** the sample gives the game process as a process under the cursor, with the style `WS_EX_NOACTIVATE`

### Requirement: The service reads the process of a sample itself
The service SHALL read the exe path, the session and the start time of each process in a sample itself, with `SeDebugPrivilege` enabled, and SHALL check that the thread still belongs to that process. If the thread or the process ended, or the thread belongs to another process, that entry SHALL count no time. If access is denied or the process is in another session, the sample SHALL be a blind sample. The wall clock SHALL NOT decide the outcome.

#### Scenario: Game that denies SYSTEM
- **WHEN** the child removes SYSTEM from the DACL of its game process and plays the game
- **THEN** the service reads the exe of the game and counts its time in Games

#### Scenario: Fake Store app window
- **WHEN** a game of the child registers the window class `ApplicationFrameWindow` and shows a child window of a Study program
- **THEN** the time counts in Games

#### Scenario: Game window on a new thread before each read
- **WHEN** the probe locks are on and the child makes the service slow with a CPU load and again and again re-creates its game window on a new thread and ends the old thread, so that the service finds the thread of the Foreground Window ended
- **THEN** the session is locked within 2 minutes

#### Scenario: Embedded game window on a new thread before each read
- **WHEN** the probe locks are on and the child does the same with a game window inside a window of `notepad.exe`, so that the service finds the thread of the child window ended
- **THEN** the session is locked within 2 minutes

#### Scenario: Clock step
- **WHEN** an Administrator sets the system clock back by one hour while the child plays a game in front
- **THEN** the time still counts in Games
- **AND** no blind sample results

### Requirement: Blind samples and budget items
A blind sample SHALL be a sample whose input desktop is not readable, whose Foreground Window gives no thread, with more than 8 other processes or 4096 windows below the Foreground Window, or with an entry that is denied or in another session. A budget item SHALL be a sample with an ended entry that would count time, or an unexpected end of the probe from the 3rd within 10 minutes. The service SHALL query the session state at each one and ignore it unless the session is active and unlocked.

#### Scenario: Honest window close
- **WHEN** the probe locks are on and the child closes a program window just as the Session Probe samples it
- **THEN** that sample is a budget item, not a blind sample
- **AND** the session is not locked at once, also after an earlier lock of that day

### Requirement: Lock the session after blind samples
At the 6th blind sample or budget item within 10 minutes of unlocked, connected time of a Restricted User, the system SHALL apply the Enforced Workstation Lock and notify the parent through the Alert Channel at most once per day. After that lock, each blind sample of that day SHALL lock; a budget item SHALL NOT. These locks SHALL follow the requirement "Shadow mode for the probe locks". The system SHALL NOT close a program.

#### Scenario: Desktop that denies the probe
- **WHEN** the probe locks are on and the child creates a desktop whose security gives access only to the child, switches to it and starts a game there
- **THEN** the session is locked within 60 seconds
- **AND** the parent gets an alert that names the child
- **AND** the game process still exists

#### Scenario: Short returns to the default desktop
- **WHEN** the probe locks are on and the child plays on a desktop that denies the probe for 25 seconds and then returns to the default desktop for 5 seconds, again and again
- **THEN** the session is locked within 2 minutes

#### Scenario: Unlock and the same desktop again
- **WHEN** the probe locks are on and the session was locked for this reason and the child unlocks it and switches to that desktop again on the same day
- **THEN** the session is locked again within 15 seconds

#### Scenario: Fast user switch
- **WHEN** an Administrator switches to their account while the session of the child stays signed in
- **THEN** samples of the disconnected session of the child count as no blind sample

#### Scenario: Windows secure desktops
- **WHEN** the probe locks are on and the child sees a UAC prompt, presses Ctrl+Alt+Del, or the secure screen saver starts
- **THEN** no blind sample results and the session is not locked for this reason

### Requirement: No category time without Session Probe samples
If the service gets no sample from the Session Probe of an unlocked, connected session for more than 12 seconds (the probe does not run or does not answer), it SHALL add no Category Usage for that time. It SHALL start a new probe within 10 seconds after an unexpected end, and within 60 seconds otherwise. After 5 minutes without samples it SHALL write an event and alert the parent, at most once per Restricted User and hour. It SHALL NOT use data of the Session Agent instead.

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

### Requirement: Restart a silent or crashing Session Probe
If the service accepts no line from a running Session Probe of an unlocked, connected session for 30 seconds, it SHALL end that probe. After each unexpected end of a probe it SHALL start a new probe within 10 seconds and keep the time of the last sample, so that the gap counts up to 12 seconds. It SHALL write an event for each unexpected end and alert the parent at the 3rd within one hour, at most once per Restricted User and hour.

#### Scenario: Silent probe
- **WHEN** the Session Probe of the child still runs but sends no line for 30 seconds while the session is unlocked
- **THEN** the service ends it and a new Session Probe runs within 40 seconds after the last line

#### Scenario: Crash loop
- **WHEN** the Session Probe of the child crashes once a minute
- **THEN** the service starts a new probe within 10 seconds after each crash
- **AND** the parent gets one alert within one hour
- **AND** while the probe locks are on, the session is locked within 10 minutes

### Requirement: Sleep and resume are not a probe fault
Time in sleep or hibernation SHALL NOT count toward the 30 seconds of the watchdog, the 5 minutes of an outage, a gap between samples, or a window of 10 minutes of unlocked time. The first sample of a session after a resume SHALL add no time. An end of a Session Probe within 15 seconds after a resume SHALL count as an expected end.

#### Scenario: Sleep and resume without a sign-in
- **WHEN** the probe locks are on, the child signs in without a password, and the PC sleeps three times within 10 minutes for 5 minutes each and resumes without a sign-in screen
- **THEN** the service does not end the Session Probe for silence
- **AND** no `PROBE_RESTART` event, no `PROBE_OUTAGE` event and no budget item results
- **AND** no Category Usage counts for the sleep time
- **AND** the session is not locked

### Requirement: Shadow mode for the probe locks
The probe locks are the locks after blind samples, for a program that is never suspended, and for a full set of Foreground Window exes. The system SHALL apply them only while the setting `probe_locks_enforced` is on. It SHALL be off until the parent stores it in the web admin; an update SHALL keep it. While it is off, the system SHALL log each probe lock as "would lock", alert the parent once per day for each reason and for each exe that is never suspended, and still count and suspend.

#### Scenario: First release on the child's PC
- **WHEN** the setting is off and the child switches to a desktop that denies the probe for 2 minutes
- **THEN** the session is not locked
- **AND** the system writes a "would lock" event that names the child and the reason
- **AND** the parent gets one alert on that day

#### Scenario: Programs that are never suspended in shadow mode
- **WHEN** the setting is off, the parent deleted the default System rule, Unknown is exhausted, and the child uses `C:\Windows\notepad.exe` and then `C:\Windows\System32\mspaint.exe` in front for 2 minutes each
- **THEN** the session is not locked
- **AND** the parent gets one alert that names `C:\Windows\notepad.exe` and one alert that names `C:\Windows\System32\mspaint.exe` on that day

#### Scenario: Counting in shadow mode
- **WHEN** the setting is off and the child plays a game in front until Games is exhausted
- **THEN** Category Usage for Games increases
- **AND** the game is suspended within 70 seconds

#### Scenario: Later update keeps the setting
- **WHEN** the parent switched the setting on and a later release is installed
- **THEN** the setting is still on

#### Scenario: Parent switches the probe locks on
- **WHEN** the parent switches the setting on during a day with "would lock" events
- **THEN** the session is not locked at the switch
- **AND** a later 6th blind sample within 10 minutes locks the session
