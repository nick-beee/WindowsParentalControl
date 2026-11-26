# Spec Delta

## Purpose

Gives the service the Foreground Window of each session of a Restricted User from a helper that the child cannot stop, read or change, so that no limit depends on data of the Session Agent.

## ADDED Requirements

### Requirement: Start a Session Probe in each session of a Restricted User
The system SHALL run exactly one Session Probe in each session of a Restricted User that exists, locked or not. It SHALL start the probe within 60 seconds after the sign-in of the child, after the start of the service, and after the end of a probe. It SHALL NOT start a Session Probe in the session of an Administrator or of a user who is not restricted.

#### Scenario: Sign-in of the child
- **WHEN** the Restricted User signs in
- **THEN** one Session Probe runs in the session of the child within 60 seconds

#### Scenario: Service restart
- **WHEN** the service restarts while the child is signed in
- **THEN** the Session Probe of the old service run has ended
- **AND** one new Session Probe runs in the session of the child within 60 seconds

#### Scenario: Session of an Administrator
- **WHEN** an Administrator signs in
- **THEN** no Session Probe runs in that session

### Requirement: The child cannot stop or change the Session Probe
The Session Probe SHALL run as SYSTEM, with no window and no privilege that it does not need. A process of the Restricted User SHALL NOT be able to end it, suspend it, read or write its memory, or write to its channel. The probe SHALL NOT use environment variables, files or folders that the Restricted User can change.

#### Scenario: Task Manager
- **WHEN** the child selects the Session Probe in Task Manager and clicks "End task"
- **THEN** Windows denies the access
- **AND** the Session Probe still runs

#### Scenario: Startup hook of the child
- **WHEN** the child sets the user environment variable `DOTNET_STARTUP_HOOKS` to its own file, signs out and signs in again
- **THEN** the Session Probe does not load that file

#### Scenario: Service stops
- **WHEN** the service stops or ends with an error
- **THEN** the Session Probe ends too

### Requirement: Sample the Foreground Window of the input desktop
The Session Probe SHALL read, every 5 seconds, the Foreground Window of the desktop that gets the input in its session, also if the child created that desktop. It SHALL send the process ID and the start time of the window process, and the window title. For a Store app it SHALL send the process of the app, not `ApplicationFrameHost.exe`. A title that it cannot read or encode SHALL NOT stop the samples. The service SHALL read the exe path itself.

#### Scenario: Game in front
- **WHEN** the child plays a game in a window that has the input focus
- **THEN** the service gets a sample with the process of the game within 5 seconds

#### Scenario: Game on a second desktop
- **WHEN** the child creates a second desktop with a tool, switches to it and starts a game there
- **THEN** the service gets samples with the process of the game

#### Scenario: Store app
- **WHEN** the Windows Calculator from the Microsoft Store is the Foreground Window
- **THEN** the sample names the process of `CalculatorApp.exe` in `C:\Program Files\WindowsApps`

#### Scenario: Strange window title
- **WHEN** a game in front has a window title of 10,000 characters with characters that are not valid text
- **THEN** the service still gets a sample with the process of the game every 5 seconds
- **AND** Category Usage for Games increases

### Requirement: Lock the session when the probe cannot read the input desktop
If the Session Probe of an unlocked session of a Restricted User runs but cannot read the input desktop for 30 seconds, the system SHALL lock the session with the Enforced Workstation Lock and SHALL notify the parent through the Alert Channel, at most once per Restricted User and day. It SHALL NOT close or kill a program. The check SHALL apply again after each unlock.

#### Scenario: Desktop that denies the probe
- **WHEN** the child creates a desktop whose security gives access only to the child, switches to it and starts a game there
- **THEN** the session is locked within 40 seconds
- **AND** the parent gets an alert that names the child
- **AND** the game process still exists

#### Scenario: Unlock and the same desktop again
- **WHEN** the session was locked for this reason and the child unlocks it and switches to that desktop again
- **THEN** the session is locked again within 40 seconds

### Requirement: No category time without Session Probe samples
If the service gets no sample from the Session Probe of an unlocked session for more than 10 seconds (the probe does not run or does not answer), it SHALL add no Category Usage for that time. It SHALL start a new probe within 60 seconds if the probe ended. After 5 minutes without samples it SHALL write an event and send the parent one alert per Restricted User and day. It SHALL NOT use data of the Session Agent instead.

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
