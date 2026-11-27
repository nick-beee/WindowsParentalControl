# 11. A SYSTEM Session Probe Gives the Facts, and a Private Channel Connects the Session Agent

## Status
Proposed (change `session-probe-and-agent-channel`). Replaces point 1 of [ADR 0002](0002-session-agent-for-app-tracking-and-tray.md).

## Context
ADR 0002 made the Session Agent the source of the Foreground Window, because a service in session 0 cannot read the windows of another session. Since `add-app-categories`, two limits depend on the agent samples: Category Usage and the set of exes that the service may suspend. The agent sends them over HTTP to `127.0.0.1:5050` without authentication, so any process of the child can send false samples (issue #6). Any process of the child can also stop the agent with its named mutex (issue #32).

The agent runs as the child. The child can kill it, read its memory, inject code into it (`SetWindowsHookEx`, `CreateRemoteThread`, a debugger, `DOTNET_STARTUP_HOOKS` in its own environment), and copy its handles with `DuplicateHandle`. So no secret and no check of the agent process can make agent data trustworthy. The rule of `CLAUDE.md` says that a limit must never depend on such data.

## Decision
1. **A Session Probe gives the facts.** The service starts `ParentalControl.Probe.exe` in each session of a Restricted User with a SYSTEM token that it moves to that session (`SetTokenInformation(TokenSessionId)`), without privileges, with the environment of SYSTEM minus the .NET runtime variables, with a SYSTEM-only DACL on its process and thread, at High priority. The probe is a self-contained .NET 10 console program with no window and no reference to the other projects. At random times, about every 5 seconds, it reads the Foreground Window and the focus window of the input desktop and sends their window handles, thread IDs and process IDs through an anonymous pipe that only the service and the probe hold. The probe opens no process of the child.
2. **The service reads the process.** The service enables `SeDebugPrivilege` and reads the exe path, the session and the start time of each sampled process itself, so that a DACL of the child on its game does not hide the game. Category Usage, the Foreground Window history and App Usage take only these reads. If the focus window belongs to another process, the time counts for each of the two that is not System, and both exes become suspend candidates. The window of the Session Agent counts in Unknown.
3. **No samples, no category time; blind samples lock the session.** If the probe gives no samples (a fault), no category time counts, the service starts a new probe, and the parent gets an alert after 5 minutes. A blind sample is one that the service cannot attribute to an exe: the child can create a desktop that denies SYSTEM, and only a child action or a bug makes a sampled process unreadable. At the 6th blind sample within 10 minutes of unlocked time, and after that lock at each blind sample of that day, the service applies the Enforced Workstation Lock and alerts the parent. The random sample times keep the child from timing a switch between two samples. The service never uses agent data instead. Content of a sample (a title, a desktop name) never ends the probe.
4. **A private Agent Channel.** For each agent start, the service creates a named pipe that only SYSTEM can open, opens the client end itself, and gives that handle to the agent by inheritance (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`). The agent checks that the server runs in session 0. The service takes the user and the session of each message from the launch, never from the message. It treats the channel as hostile input: a length check before each read, asynchronous I/O with timeouts, and a rate limit.
5. **The agent gets no trust.** The channel proves the session and the user, not that the agent code sent the data. The agent only shows texts, mutes and minimizes windows, answers prompts, uploads captures, and asks for a Session End Hold under its rate limit.
6. **The service owns the single instance.** It starts one agent per session and keeps its process handle. The agent has no mutex, and an agent without a channel exits. The installer removes the `Run` key of the agent.
7. **The HTTP agent routes go at once.** `/api/agent/*` answers 404 in the same release. The child request page and the web admin stay on HTTP.

### Rejected alternatives
- **A helper that runs as the child**: the same weak point as the agent.
- **Agent samples checked against the process list**: the child reports a Study exe that runs in the background while a game is in front.
- **Count from the process list only**: launchers and tray helpers count all day (`add-app-categories` D1).
- **A named pipe with an ACE for the child and a check of the client process** (`GetNamedPipeClientProcessId`, image path, session, pid and start time): it proves no more than the inherited handle, and it needs a correct ACE without `FILE_CREATE_PIPE_INSTANCE`, a pid check with a race, and a reconnect logic. It stays the fallback if the VM shows that the inheritance across sessions fails.
- **A secret token for the agent**: the child reads the memory and the command line of its own agent.
- **The agent at High integrity level**: its child programs inherit the level, and the side effects on the shell, the tray and the audio APIs are not known. The probe makes this protection unnecessary.
- **A mutex that only SYSTEM can create**: the child can create the name first while the service is not running. The channel already gives one agent per session.
- **Keep the HTTP routes for one version**: this keeps #6 open for that version, and the installer ships the agent and the service together.
- **A probe that opens the window process itself**: the child can remove SYSTEM from the DACL of its game, and a probe without privileges then sees nothing.
- **A lock after 30 seconds of only unreadable samples**: a short return to the default desktop resets the timer, so the child alternates and is never locked.

## Consequences
- No limit reads data of a process that the child controls. The child can still fake its own screen captures, answer its own prompts, get about 80 seconds of play per 15 minutes with a false session-end request, and stop its own widget.
- A SYSTEM process runs in the session of the child. A bug in it is a way to SYSTEM, so the probe has no window, no privileges, no environment of the child, and files that only administrators can change. Native AOT is a later option.
- A probe fault stops the category counting until the probe runs again. The parent gets an alert. The session Allowance Time is not affected.
- A child that hides on a desktop that denies SYSTEM, or behind a DACL that the service cannot pass, gets the Enforced Workstation Lock, not free time. A false blind sample from Windows would lock the child, so the VM spike must show none for UAC, Ctrl+Alt+Del, the secure screen saver and a fast user switch.
- All projects move from .NET 8, which is out of support, to .NET 10 before this change.
- A Job Object may not accept a probe in another session. The pipe ends the probe with the service, and the service ends leftover probes at its start.
- The installer must ship and stop the probe. An old agent that survives an update gets 404 and shows a stale widget until the next sign-in.
- A later change can move the screen capture into the probe, so that the child cannot fake it.
