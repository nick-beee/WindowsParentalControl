# Proposal

## Why

Two limits depend on data that any process of the child can send to `127.0.0.1:5050` without authentication (GitHub issue #6). Category Usage counts the Foreground Window samples of `POST /api/agent/activity`, and only an exe from these samples can become a Suspended App (`ForegroundHistory`). A script that sends a Study exe every 20 seconds keeps the Games time at zero, and a game that never shows in a sample is never suspended. This breaks the rule in `CLAUDE.md`: a limit must never depend on data that the Session Agent sends without authentication.

Also, any process of the child can stop the Session Agent: it creates the named mutex `Global\ParentalControlAgent_{UserName}` first, and the real agent exits at its start (GitHub issue #32).

The child runs the Session Agent under its own account. So the child can read the memory of the agent, inject code into it, and kill it. A secret or a check of the agent process cannot make agent data trustworthy. The service must get the facts for a limit from a source that the child cannot change.

## What Changes

- **Session Probe** (new): the service starts a small helper process in each session of a Restricted User. The helper runs as SYSTEM, has no window, and the child cannot stop, read or change it. At random times, about every 5 seconds, it reads the windows of the input desktop that the child can use: the Foreground Window, the focus window, the visible child windows of other processes in the Foreground Window, and the window under the cursor. It sends their window handles, thread IDs and process IDs, with its own time of the sample, to the service through a pipe that only the service and the helper hold. The helper opens no process of the child.
- The service reads the exe path and the start time of each sampled process itself, with `SeDebugPrivilege`, so a DACL that the child puts on its game does not hide the game. Category Usage, the set of exes that were a Foreground Window today, App Usage and the live activity on the admin page come only from these reads. The service measures the time between samples with the time of the helper, so a CPU load of the child that delays the service does not shorten the count.
- The time counts for each process that is not System among the Foreground Window, the focus window, and the child windows of other processes (for example, a game that a script put into a Notepad window). It also counts for a window under the cursor that a click cannot activate (`WS_EX_NOACTIVATE`), so that a game with this style still counts while the child plays it with the mouse. Each of these exes, and the exe of any other window under the cursor, becomes a suspend candidate.
- A WebView2 control of the WebView2 Runtime in Program Files counts with the program that hosts it. Any other file with the name `msedgewebview2.exe` counts like any other exe.
- The Session Agent window counts in Unknown, not in System, except while the parent has an active prompt for the child. If the category of a program that the service never suspends (for example the agent) is exhausted and that program counts for more than 60 seconds in 10 minutes, the service applies the Enforced Workstation Lock.
- If the Session Probe of a session gives no sample (a fault), the service counts no category time for that session, starts the probe again, and alerts the parent after 5 minutes without samples (at most one alert per hour). The session Allowance Time still counts as before.
- A blind sample is a sample that the service cannot attribute to an exe: the input desktop is not readable (the child can create a desktop that denies SYSTEM), the service cannot read a sampled process, or more than 8 other processes have windows in the Foreground Window. A Foreground Window process that ended before the service read it also counts toward the budget. At the 6th blind sample within 10 minutes of unlocked time, the service applies the Enforced Workstation Lock and alerts the parent. After that lock, each blind sample of the day locks again. It closes no program.
- **Agent Channel** (new): the service starts the Session Agent with one end of a pipe that the service created. No other process can open this pipe by name. The agent uses only this channel: the status poll, the prompt answer, the screen capture upload and the session-end request.
- The service treats the Agent Channel as hostile input: it checks the length of a frame before it reads it, uses asynchronous reads and writes with timeouts, and limits the messages per channel. It starts the agent without the variables `DOTNET_*`, `COMPlus_*` and `CORECLR_*`.
- The service takes the user and the session of each agent message from the agent that it started, never from a field in the message. A prompt answer must name an active prompt of that user. A status poll shows only the prompts and the capture requests of that user.
- **BREAKING**: the routes `GET /api/agent/status`, `POST /api/agent/activity`, `POST /api/agent/prompt/respond`, `POST /api/agent/session-ending` and `POST /api/agent/capture` go away in the same release. The installer ships the agent and the service together. `/`, `/request`, `/api/request` and the web admin stay on HTTP.
- **Single instance** (#32): the agent no longer uses a named mutex. The service starts one agent per session and keeps its process handle. It starts a new agent only after that process ended. An agent that the service did not start has no channel and exits at once. The installer removes the `Run` key that starts the agent at sign-in.
- The Session Agent stops the Foreground Window sampling. It gets the pids to mute from the status poll.
- The rate limit of the Session End Hold stays. The child controls its own agent, so a session-end request through the channel is still a claim of the child.

## Out of scope

- A repeated resume of a Suspended App with a tool (GitHub issue #24). The child still owns its processes. A separate change handles it.
- Idle detection. A program in front counts also if the child does not use it, as today.
- A program whose window is not in a sample: not the Foreground Window, not the focus, not a child window of the Foreground Window, and not a `WS_EX_NOACTIVATE` window under the cursor. Examples: a video in a background window, or a `WS_EX_NOACTIVATE` game that the child plays with the keyboard or a gamepad while the cursor rests elsewhere.
- A copied or renamed exe that matches the App Rule of another category. Rules by hash or by signature are a part of stage 4.
- A game in a folder below `C:\Windows` that a standard user can write (GitHub issue #45).
- A System program that the child drives with a script or with injected code, for example a PowerShell web control, an HTA file in `mshta.exe`, or a game drawn in its own `notepad.exe`. This has no bound; a parent can move the script hosts out of System with an App Rule (design.md, residual risks).
- A game copied as `msedgewebview2.exe`: this change locks the session when its category is exhausted; GitHub issue #48 changes the protection of that file name so that it is suspended instead.
- Screen captures from the Session Probe. The agent still makes them, so the child can fake them (see design.md, residual risks).
- The web server on all addresses over HTTP (#7), and `update/apply` (#8).
- A change of how the Allowance Time, the Schedule Window or the Enforced Workstation Lock works.
- Window minimization and mute in the session of another user.

## Capabilities

### New Capabilities
- `session-probe`: the SYSTEM helper in each session of a Restricted User that reports the Foreground Window to the service, its start, its protection against the child, the read of a sampled process by the service, the lock after blind samples, and what the service does when it gives no samples.
- `session-agent-channel`: the private channel between the service and the Session Agent that the service started, the identity of each message, its limits against a flood, the agent environment, the single agent per session, and the end of the HTTP agent routes.

### Modified Capabilities
- `category-limits`: Category Usage and the suspend candidates come from Session Probe samples, not from agent samples; the counted processes of a sample; WebView2 counts with its host; a limit for the Session Agent window and for each program that is never suspended; the set of Foreground Window exes fails closed; the reason of the rate limit of the Session End Hold changes.
- `app-categories`: the untrusted exe data comes from the Session Probe.
- `admin-access`: the capture quota applies to the Agent Channel; `/api/agent/*` is no longer a child-facing route.
- `child-messages`: the service sends the Language Preset in the status of the Agent Channel.

## Impact

- A chore before the implementation moves all projects from .NET 8 (out of support since 2026-11-10) to .NET 10, with CI and the VM snapshot.
- New project `src/ParentalControl.Probe` (`ParentalControl.Probe.exe`, `net10.0-windows`, a console program without a window, self-contained, no reference to another project). The installer puts it in `{app}\probe`.
- `src/ParentalControl.Core/Platform`: new code to start a process in a session with a SYSTEM token, without a console, and with one inherited handle; `SessionManager.LaunchProcessInSession` returns the process handle; the service enables `SeDebugPrivilege` and reads a sampled process itself. The Foreground Window code of the agent `ActivityTracker` moves into the probe project.
- `src/ParentalControl.Service`: new `SessionProbeHost` and `AgentChannelHost`; `SessionTracker.EnsureAgentRunning` tracks the agent that it started; `CategoryEnforcer` and the counting take probe samples; `WebServerHost` loses the five agent routes, and the status, prompt, capture and session-end logic moves behind the channel.
- `src/ParentalControl.Agent`: `Program.cs` (no mutex, channel handle from the command line), `AgentController.cs`, `SessionEndWatcher.cs` (channel instead of HTTP), `ActivityTracker.cs` (removed).
- `src/ParentalControl.Domain`: pure classes for the sample counting, the counted processes (with the trusted WebView2 path and the prompt exemption of the agent), the blind-sample budget, the probe outage, the channel messages and limits, and the agent launch record; the cap of `ForegroundHistory` rises to 60,000. New xUnit tests.
- `installer/ParentalControl.iss`: the probe files; the `Run` key goes; the uninstall stops the probe.
- `GLOSSARY.md`: new terms Session Probe, Agent Channel, Blind Sample and Counted Process; Category Usage and Session End Hold change.
- `docs/adr/0011-session-probe-and-agent-channel.md`: replaces point 1 of ADR 0002.
- No new table and no new column.
