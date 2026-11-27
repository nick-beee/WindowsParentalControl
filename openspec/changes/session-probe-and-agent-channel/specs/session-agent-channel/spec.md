# Spec Delta

## Purpose

Connects the service and the Session Agent that the service started through a private channel, gives each agent message the user and the session of that agent, keeps one agent per session, and keeps agent data out of each limit.

## ADDED Requirements

### Requirement: A private Agent Channel for each agent that the service starts
When the service starts the Session Agent in a session, it SHALL give that agent one end of a new Agent Channel. No other process SHALL be able to open the channel by a name or create a second end of it. The Session Agent SHALL use only the Agent Channel to talk to the service, and SHALL exit if the other end does not run in session 0.

#### Scenario: Agent started by the service
- **WHEN** the service starts the Session Agent in the session of the child
- **THEN** the widget shows the remaining time from the status of the Agent Channel within 10 seconds

#### Scenario: Fake service of the child
- **WHEN** a process of the child gives the agent exe a channel to its own server
- **THEN** the agent exits and shows nothing

### Requirement: The service starts the agent without runtime variables of the child
The service SHALL start the Session Agent without each environment variable whose name starts with `DOTNET_`, `COMPlus_`, `CORECLR_` or `WEBVIEW2_`, also if the Restricted User set it.

#### Scenario: Startup hook for the agent
- **WHEN** the child sets the user environment variable `DOTNET_STARTUP_HOOKS` to its own file and the service starts the Session Agent
- **THEN** the environment of the agent has no `DOTNET_STARTUP_HOOKS`

#### Scenario: WebView2 arguments of the child
- **WHEN** the child sets the user environment variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` to `--remote-debugging-port=9222` and the service starts the Session Agent
- **THEN** the environment of the agent has no `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`

### Requirement: The Agent Channel resists a flood or a stall
The service SHALL check the length of each Agent Channel frame before it reads the frame, and SHALL close the channel for a frame over the limit. It SHALL read and write the channel without a blocked thread, close it after a stall of 10 seconds, and limit the messages per channel. A flood or a stall on one channel SHALL NOT delay another channel or a limit.

#### Scenario: Huge frame length
- **WHEN** code in the agent of the child sends a frame length of 2 GB
- **THEN** the service closes that channel without a large allocation
- **AND** the service starts a new Session Agent within 60 seconds

#### Scenario: Agent that never reads
- **WHEN** code in the agent of the child sends status polls and never reads the answers
- **THEN** the service closes that channel
- **AND** the limits of the child still apply on time

### Requirement: The service takes the identity of an agent message from the launch
The service SHALL take the user and the session of each Agent Channel message from the agent that it started on that channel: the user SID from the token that started the agent, and the session of that start. It SHALL ignore a user name or a session ID in the message.

#### Scenario: Message names another user
- **WHEN** the agent of the child sends a status poll that names the user `Mama`
- **THEN** the service answers with the status of the child

#### Scenario: Session-end request for another session
- **WHEN** the agent of the child sends a session-end request that names the session of an Administrator
- **THEN** the service handles it only for the session of the child

### Requirement: Prompt answers only for the prompts of the agent user
The service SHALL accept a prompt answer on an Agent Channel only for an active Interactive Session Prompt of the user of that channel. It SHALL refuse an answer for a prompt of another user, an unknown prompt or a prompt with an answer, and SHALL change nothing then.

#### Scenario: Answer for a prompt of another user
- **WHEN** the agent of the child sends an answer for the prompt ID of a prompt to another user
- **THEN** the service refuses the answer
- **AND** the prompt of the other user stays open

### Requirement: The status shows only the data of the agent user
The status answer on an Agent Channel SHALL contain only the prompts, the capture requests, the Suspended Apps and the Pending Suspensions of the user of that channel. A status poll SHALL clear a capture request only of that user.

#### Scenario: Status poll of another process
- **WHEN** the parent asks for a screen capture of the child and a process of the child sends `GET /api/agent/status?user=Kid` before the next poll of the agent
- **THEN** the agent of the child still gets the capture request with its next poll

### Requirement: One Session Agent per session
The service SHALL start one Session Agent per session and SHALL keep a handle to its process. It SHALL start a new agent within 60 seconds after that process ended, and SHALL NOT start one while it runs. A process of the child SHALL NOT be able to stop the start of the agent, also not with a mutex, a pipe or a process with the name or the exe of the agent.

#### Scenario: Mutex of the child
- **WHEN** a process of the child creates the mutex `Global\ParentalControlAgent_Kid` before the sign-in of the child
- **THEN** the Session Agent of the service runs in the session of the child within 60 seconds after the sign-in

#### Scenario: Child starts the agent exe
- **WHEN** the child starts `ParentalControl.Agent.exe` from the install folder
- **THEN** that process exits without a channel and without a widget
- **AND** the Session Agent of the service still runs

#### Scenario: Child ends the agent
- **WHEN** the child ends the Session Agent in Task Manager
- **THEN** the service starts a new Session Agent within 60 seconds

### Requirement: The HTTP agent routes are removed
The web server SHALL NOT serve `/api/agent/status`, `/api/agent/activity`, `/api/agent/prompt/respond`, `/api/agent/session-ending` or `/api/agent/capture`. It SHALL answer 404 to each of them. The routes `/`, `/request` and `/api/request` SHALL stay.

#### Scenario: Old activity route
- **WHEN** a process of the child sends `POST /api/agent/activity` with a Study exe
- **THEN** the web server answers 404
- **AND** no Category Usage and no App Usage changes

### Requirement: No limit depends on data of the Session Agent
The child controls its Session Agent, so the service SHALL NOT use data from the agent for Category Usage, the set of Foreground Window exes, App Usage, the Allowance Time, a Category Limit, a suspension, or a resume because time is available. Agent data SHALL only show texts, mute and minimize windows, answer prompts, add screen captures, and ask for a Session End Hold under its rate limit.

#### Scenario: Agent with injected code
- **WHEN** the child injects code into its Session Agent that reports a Study exe while a game is in front
- **THEN** the Games usage increases as without the injected code
