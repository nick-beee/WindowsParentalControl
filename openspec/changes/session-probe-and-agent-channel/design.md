# Design

## Context

See proposal.md for the motivation and specs/ for the required behavior.

Current state that shapes the approach:
- The service runs as LocalSystem in session 0. It lists the processes of the child's sessions itself (`ProcessControl.ListProcesses`: pid, session, exe path, start time, thread state) and suspends and resumes them itself.
- The Session Agent (`ParentalControl.Agent.exe`) runs as the child. `SessionTracker.EnsureAgentRunning` starts it with `WTSQueryUserToken` + `CreateProcessAsUserW` (`SessionManager.LaunchProcessInSession`) and closes the handles of `PROCESS_INFORMATION` at once. The installer also starts it at each sign-in through the value `ParentalControlAgent` below `HKLM\...\CurrentVersion\Run`.
- The agent talks to the service over HTTP on `127.0.0.1:5050`, with no authentication: `GET /api/agent/status?user=` every 5 s, `POST /api/agent/activity` every 20 s (the Foreground Window sample, the only source of Category Usage and of `ForegroundHistory`), `POST /api/agent/prompt/respond`, `POST /api/agent/capture` (user in the header `X-User`) and `POST /api/agent/session-ending` (session ID in the body).
- The agent keeps one instance per user with the named mutex `Global\ParentalControlAgent_{UserName}`. `EnsureAgentRunning` finds an agent by its image path in the session.
- Existing holes that this change also closes: any process can poll `/api/agent/status?user=Kid`, which clears the capture request of the parent and shows the active prompt of any user. `/api/agent/prompt/respond` accepts any prompt ID.

Threat model: the child is 10 years old and has a standard account. It can run any program in its session, kill or replace its agent process, read and write the memory of the agent (same user, same integrity level), set its own user environment variables, and send any request to `127.0.0.1:5050`. It cannot run code as SYSTEM or as an administrator.

### What a SYSTEM service in session 0 can see
- **Can see:** each process in each session, its user, its session, its exe path, its start time and the state of its threads (`OpenProcess`, `QueryFullProcessImageNameW`, `GetProcessTimes`, `ProcessIdToSessionId`, `NtQuerySystemInformation`). The lock state of a session (WTS notifications).
- **Cannot see:** the Foreground Window of another session. `GetForegroundWindow`, `GetGUIThreadInfo` and `GetWindowThreadProcessId` work only on the desktop of the calling thread. A thread in session 0 cannot attach to the window station `WinSta0` of session 1, because `OpenWindowStationW` and `OpenDesktopW` resolve names in the session of the caller. The WTS functions give the state of a session, not its windows. We found no documented API that gives the Foreground Window of another session to a process in session 0.
- So the facts about the Foreground Window must come from a process in the session of the child. That process must not run as the child.

## Goals / Non-Goals

**Goals:**
- No limit depends on data that a process of the child can write.
- The child cannot stop the start of the Session Agent (#32).
- Each agent message has the user and the session of the agent that the service started.
- The pure logic (sample counting, probe outage, channel framing, launch record) runs and is tested on the Mac.

**Non-Goals:**
- Trustworthy data from the Session Agent. The child controls that process. This design does not try to protect it, and it does not need to.
- Protection against an administrator or against code that runs as SYSTEM.

## Decisions

The user delegated each open question. Each decision below records the recommended option and the rejected alternatives.

### D1. The service gets the Foreground Window from a Session Probe that runs as SYSTEM
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The service starts `ParentalControl.Probe.exe` in each session of a Restricted User, with a SYSTEM token that it moves to that session. The probe has no window. Every 5 seconds it reads the Foreground Window of the input desktop and sends a sample to the service. Category Usage, `ForegroundHistory`, App Usage and the live activity of the admin page take only these samples and the process list of the service.

Why SYSTEM: a process of the child cannot open a SYSTEM process for `PROCESS_TERMINATE`, `PROCESS_SUSPEND_RESUME`, `PROCESS_VM_READ` or `PROCESS_VM_WRITE`. The default DACL of the SYSTEM token gives the child no access, and the mandatory label (System integrity) blocks write access from the medium integrity level. UIPI blocks window hooks and window messages from the child into a process at a higher integrity level, and the probe has no window anyway.

Rejected alternatives:
- **A helper that runs as the child.** It has the same weak point as the agent: the child can kill it, inject code into it, or replace its data.
- **Agent samples checked against the process list.** The service can check that the reported pid exists in the session with that exe path. But the child can report a Study exe that runs in the background while a game is in front. The check does not find this.
- **Count from the process list only** (a category counts while one of its processes runs). `add-app-categories` D1 rejected it: launchers and tray helpers count all day. The parent chose Foreground Window counting.
- **Audio, GPU or input heuristics** to detect use. They are a separate topic (idle detection, background video) and are out of scope.

### D2. How the service starts the Session Probe
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The service (`SessionProbeHost`, new) starts the probe for each session of a Restricted User in the 60-second tick and at each sign-in and unlock event, if no probe of the service runs in that session.

WinAPI calls, in order:
1. `OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID)`.
2. `CreateRestrictedToken(..., DISABLE_MAX_PRIVILEGE, ...)`: the probe keeps only `SeChangeNotifyPrivilege`. It needs no privilege: SYSTEM gets `PROCESS_QUERY_LIMITED_INFORMATION` on the processes of the child from their DACL.
3. `DuplicateTokenEx(..., TokenPrimary)` and `SetTokenInformation(TokenSessionId, sessionId)`. This needs `SeTcbPrivilege` in the service, which LocalSystem has.
4. `CreateEnvironmentBlock(out env, systemToken, false)`: the environment of SYSTEM. The service never uses the environment of the child for the probe. The child can set `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT`, `COMPlus_*` and `PATH` in `HKCU\Environment`, and each of them can load code of the child into a .NET process.
5. `CreatePipe` with an inheritable write end (D3), `InitializeProcThreadAttributeList` and `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_HANDLE_LIST)` with only that handle.
6. `CreateJobObjectW` and `SetInformationJobObject(JobObjectExtendedLimitInformation, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)`. The service holds the only job handle. This is defense in depth only: the remarks of `AssignProcessToJobObject` say that the processes of a job must run in the session of the job, and the job of the service is in session 0. Task 1.4 checks it. If the assignment fails, the service logs it once and starts the probe without the job.
7. `CreateProcessAsUserW(token, ..., bInheritHandles: true, EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, env, "{app}\probe", STARTUPINFOEX with lpDesktop "winsta0\default")`, then `AssignProcessToJobObject` and `ResumeThread`.
8. The service keeps the process handle. `WaitForSingleObject` on it (through `RegisteredWaitHandle`) tells the service that the probe ended.

In the probe: `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)` at the start, and `DefaultDllImportSearchPaths(System32)` on the assembly. The probe is a framework-dependent .NET 8 console program like the other parts, with `OutputType` `WinExe` (no console window). Its files are in `{app}\probe`, which only administrators can change. The .NET host finds the runtime through the machine environment and `C:\Program Files\dotnet`, which only administrators can change.

How a probe ends:
- The primary mechanism is the pipe. When the service process ends, also after a crash, the read end closes, the next write of the probe fails with `ERROR_BROKEN_PIPE` or `ERROR_NO_DATA`, and the probe exits within 5 seconds.
- The service ends a probe with `TerminateProcess` at the sign-out of its session (`WTS_SESSION_LOGOFF`) and at the service stop.
- At its start, the service ends each process that runs as SYSTEM from `{app}\probe\ParentalControl.Probe.exe` and that it did not start in this run. The child cannot start a SYSTEM process, so each such process is a probe of an earlier service run.
- The hard rule covers the programs of the child, not this helper of the service.

Rejected alternatives:
- **Native AOT for the probe** (no .NET host, no runtime search). It is a stronger option, but AOT cannot cross-compile from the Mac, and `build.ps1` and CI need a separate publish step. A later change can add it.
- **A probe in C or C++.** A second toolchain for one small program.
- **A probe with all SYSTEM privileges.** It needs none, and each privilege makes a bug in the probe worse.
- **`PROC_THREAD_ATTRIBUTE_JOB_LIST`** instead of `CREATE_SUSPENDED` + `AssignProcessToJobObject`. It works only on newer Windows 10 builds; the two calls work on each supported version.

### D3. The probe sends its samples through an anonymous pipe
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The service creates an anonymous pipe (`CreatePipe`, through `AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)`). The probe inherits only the write end (D2 step 5) and gets its handle value on the command line. Both ends belong to SYSTEM processes, so the child cannot open, duplicate or write them. The pipe has no name, so the child cannot create it first.

Format: one line of UTF-8 JSON per sample, at most 4 KB. Three kinds:
- A window in front: `{ "pid": 1234, "startUtc": "...", "title": "...", "desktop": "..." }`. Only `pid` and `startUtc` are required.
- No window in front: `{ "pid": 0 }`.
- The input desktop is not readable: `{ "desktopDenied": true }` (D6a).

The window title and the desktop name come from programs of the child. The probe cuts the title at 256 and the name at 64 UTF-16 characters, and replaces each lone surrogate and each control character with U+FFFD before it serializes them. The probe does not send the exe path: a path can be up to 32,767 characters, and the service reads the path itself. So an honest line is always much shorter than 4 KB.

The service ignores a time in the message and uses its own clock (`Environment.TickCount64`) at the receipt. A line over 4 KB, a line that does not parse, or a field with a wrong type drops only that one line or that field, with a log line at most once per probe run. Content never ends the probe: else a program of the child could cause a probe outage (D6) with a strange title.

The service checks each sample against its own process list: the pid must be in the session of the probe, and the start time must match (`ProcessControl.QueryPathAndStart`). The service reads the exe path itself. A sample that does not match counts no time.

### D4. Sampling in the probe
*Decided by the orchestrator under the user's delegation (2026-11-26).*

A dedicated thread of the probe, which never creates a window or a hook, does this every 5 seconds:
1. `OpenInputDesktop(0, false, DESKTOP_READOBJECTS)` and `GetUserObjectInformationW(UOI_NAME)` (the name only for the log). If `OpenInputDesktop` or step 2 fails, the sample is "desktop not readable" (D6a).
2. `SetThreadDesktop` to that desktop. So the probe follows a desktop that the child created and switched to. A desktop that a tool such as Sysinternals Desktops creates gets the default DACL of the child's token, which gives SYSTEM full access. A desktop that the child creates with its own security descriptor can deny SYSTEM; D6a handles it.

The probe has no rule by desktop name: the child chooses the names of its own desktops. On the lock screen the session is locked and nothing counts. On the secure desktop of UAC, `consent.exe` is in front, which is below the Windows folder and so in System.
3. `GetForegroundWindow`, `GetWindowThreadProcessId`.
4. If the window process is `ApplicationFrameHost.exe` (a Store app), `EnumChildWindows` finds the first child window of another process, as the agent does today. The code moves from the agent `ActivityTracker` to a shared class in Core.
5. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW`, `GetProcessTimes`.
6. `GetWindowTextW`, at most 256 characters. For a window of another process, this reads the stored caption and sends no message, so a hung window cannot block the probe.
7. `CloseDesktop`.

The 5-second interval replaces the 20-second interval of the agent. The service attributes the time to the sample that ends the interval, as the spec "Count Category Usage from the Foreground Window" states.

### D5. Counting from samples
*Decided by the orchestrator under the user's delegation (2026-11-26).*

A pure class `ForegroundSampleCounter` (Domain) keeps, per session, the receipt time of the last sample. For each sample it returns the seconds to add: the time since the last sample of the session, at most 10 seconds, and zero for the first sample after a probe start. The service then does what `POST /api/agent/activity` does today: skip a locked session, `ForegroundHistory.Record`, `CategoryUsageRepository.AddSeconds` (not for System and not for a suspended exe), `AppUsageRepository.AddSeconds` with the file name and the title, and `_liveActivities`. The user is the user of the session of the probe.

`ForegroundHistory` stays in memory with its cap of 1000 paths. The cap now protects against a child with many exes, not against false samples.

### D6. When the probe gives no samples
*Decided by the orchestrator under the user's delegation (2026-11-26).*

A pure class `ProbeOutageMonitor` (Domain) keeps the time of the last sample per session. If an unlocked session of a Restricted User has no sample for 5 minutes, the service writes the event `PROBE_OUTAGE` (new value of the existing `EventType` enum) and sends the parent a Telegram alert, at most once per Restricted User and day. During the gap no category time counts (D5 caps each gap at 10 seconds). The tick starts a new probe if the old one ended. The session Allowance Time counts as before, because it does not depend on samples.

Rejected alternatives:
- **Agent samples as a fallback.** The child controls them. This is the hole of #6.
- **A conservative count from the process list** (each category with a running process that was in front today counts during the gap). It punishes the child for a fault of the service, and the child cannot stop a SYSTEM process. So a gap is a fault, not a bypass.
- **The Enforced Workstation Lock during a gap**, as in Data Folder Lockdown. Too strong for a fault of a helper. The alert lets the parent act.

### D6a. An input desktop that the probe cannot read locks the session
*Decided by the orchestrator under the user's delegation (2026-11-26).*

A probe that runs but cannot read the input desktop is not a fault of the service. The child can cause it: `CreateDesktopW` with a security descriptor that gives access only to the child, then `SwitchDesktop`. A game on that desktop is in front, but the probe cannot see it. So this state must fail closed.

If an unlocked session of a Restricted User gets only "desktop not readable" samples for 30 seconds, the service applies the Enforced Workstation Lock (`SessionManager.LockSession`) and sends one Telegram alert per Restricted User and day, with the event `PROBE_DESKTOP_DENIED` (new value of `EventType`). At unlock Windows shows the default desktop again, and the check starts again. This follows the fallback when a process cannot be suspended (`add-app-categories` D6). No program is closed. `ProbeOutageMonitor` (D6) also tracks this state.

Rejected alternatives:
- **No count while the desktop is not readable.** This is the hole of #6 again, through a different door.
- **A conservative count from the process list.** A game that the child starts on the hidden desktop was never seen in front, so it is not a candidate (D3b of `add-app-categories`), and the count misses it.
- **Give the probe a privilege to bypass the desktop DACL** (`SeTakeOwnershipPrivilege` and a new DACL on the desktop of the child). This changes an object of the child and gives the probe a strong privilege. The lock is simpler and sure.

### D7. The Agent Channel: a pipe that the service owns, with the client end inherited by the agent
*Decided by the orchestrator under the user's delegation (2026-11-26).*

For each agent start, the service (`AgentChannelHost`, new):
1. Creates a named pipe `\\.\pipe\ParentalControl.Agent.<32 random hex digits>` with `CreateNamedPipeW(PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, maxInstances: 1)` and a security descriptor that gives access to SYSTEM only. The child cannot open the pipe by name and cannot create an instance.
2. Opens the client end itself with `CreateFileW(GENERIC_READ | GENERIC_WRITE, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION)` and an inheritable handle. `ConnectNamedPipe` completes at once.
3. Starts the agent with the user token as today (`WTSQueryUserToken`, `DuplicateTokenEx`, `CreateEnvironmentBlock` with the user token), but with `bInheritHandles: true`, `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` with only the client handle. The command line is `"ParentalControl.Agent.exe" --channel <handle value>`.
4. Closes its copy of the client handle and keeps the server end, the agent process handle, the pid, the start time, the session ID and the user SID as the launch record.

The agent wraps the handle in a `SafePipeHandle` and a `NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle)`. It checks `GetFileType == FILE_TYPE_PIPE` and `GetNamedPipeServerSessionId == 0`. A process of the child cannot run in session 0, so a channel to a server of the child fails this check. If a check fails or the channel breaks, the agent exits. A new service run starts a new agent (D9).

Framing: each message is a 4-byte little-endian length and a UTF-8 JSON object with `type` and `id`. The agent sends requests; the service answers each one. Types: `status`, `promptAnswer` (`promptId`, `response`, `reason`, `turnaroundSeconds`), `capture` (`trigger`, `width`, `height`, then the JPEG bytes as a second frame), `sessionEnding` (`reason`). No message has a user or a session field; the service ignores such fields (spec "The service takes the identity of an agent message from the launch"). A JSON frame has at most 64 KB, and a JPEG frame at most `CaptureQuota.MaxBytesPerCapture`. A larger frame or a frame that does not parse closes the channel. A pure class `AgentChannelCodec` (Domain) does the framing and the limits.

**What this proves, and what it does not.** It proves that a message comes through the handle that the service gave to the agent process that it started, in that session, as that user. It does not prove that the agent code sent it. The child owns the agent process: it can load a DLL into it with `SetWindowsHookEx`, start a thread in it with `CreateRemoteThread`, attach a debugger, or copy the channel handle into another process with `DuplicateHandle`. So the channel gives the identity of the session and the user, not trust in the data. D10 makes sure that no limit needs more.

Rejected alternatives:
- **A named pipe with an ACE for the child's SID, and a check of the client** (`GetNamedPipeClientProcessId`, then the image path below the install folder, `ProcessIdToSessionId`, and pid + start time equal to the launch record). It proves the same as D7 and no more: the child can still inject or duplicate the handle. It needs more parts: an ACE that must not include `FILE_CREATE_PIPE_INSTANCE` (else the child creates a server instance and takes the agent's connection), a pid check with a race against pid reuse, and a reconnect logic. It stays the fallback if the VM check of task 1.1 shows that the handle inheritance across sessions does not work.
- **A secret token** on the command line, in a file or in the registry. The child reads the command line and the memory of its own agent.
- **HTTP with the owner check of the TCP connection** (`TcpConnectionOwner`, as for the admin login). The same proof as the named pipe check, plus the port 5050 that another program can hold.
- **The agent at High integrity level** (a user token with a raised label through `SetTokenInformation(TokenIntegrityLevel)`), so that the child cannot inject into it or end it. The programs that the agent starts (the request page in the browser) inherit the High level, UIPI blocks drag and drop and some input from the child's programs, and the effect on the tray and the audio APIs is not known. The probe gives the facts, so the agent does not need this protection.
- **A DACL on the agent process** that denies `PROCESS_VM_WRITE` and `PROCESS_CREATE_THREAD` to the child. `SetWindowsHookEx` loads a DLL into each GUI process at the same integrity level on the desktop and does not check the DACL of the process.

### D8. What the service does with agent messages
*Decided by the orchestrator under the user's delegation (2026-11-26).*

- `status`: the same answer as `GET /api/agent/status` today, for the user of the launch record. The capture request and the active prompt are those of that user. A non-restricted user (an Administrator) gets the prompts and `isRestricted: false`, as today.
- `promptAnswer`: accepted only for an active prompt whose user SID is the SID of the launch record. Else the answer is refused and nothing changes.
- `capture`: the quota and the file logic of `POST /api/agent/capture`, with the user of the launch record. The column `admin_ip` of `screen_captures` gets NULL for an agent capture. No new column.
- `sessionEnding`: `CategoryEnforcer.ResumeSessionForEnd(sessionId of the launch record, user, trusted: false, ...)`. The rate limit of the Session End Hold stays: the child can make its agent send this message, and the child can also send `WM_QUERYENDSESSION` to the hidden window of the agent.
- The mute list: the agent takes the pids from `suspendedApps` of the status poll (every 5 s). `ActivityTracker` and its activity timer go away.

### D9. One agent per session, owned by the service (#32)
*Decided by the orchestrator under the user's delegation (2026-11-26).*

`SessionManager.LaunchProcessInSession` returns the process handle, the pid and the start time instead of a bool. A pure class `AgentLaunchRegistry` (Domain) keeps one launch record per session and decides: start an agent if the session has no record or if the process of its record ended; else do nothing. `EnsureAgentRunning` uses it and no longer searches processes by image path. So a process of the child with the agent name or the agent exe cannot make the service think that the agent runs. `IsAgentRunning` (the prompt route of the admin page) also reads the registry.

The agent removes the named mutex. An agent without `--channel`, with a handle that is not a pipe, or with a server outside session 0 exits at once. The installer removes the `Run` value `ParentalControlAgent` (also from an old install), so the service is the only process that starts the agent. An agent that the child ends is started again at the next tick (at most 60 s), as today.

Rejected alternatives:
- **A mutex that the service creates with a DACL for SYSTEM only**, and an agent that checks the owner of the mutex. The child can create the name first while the service is not running (for example during an update), and the check of the owner adds code. The channel already gives one agent per session.
- **Keep the mutex and add a check.** Each named object in a namespace that the child can write can be taken first.

### D10. No limit reads agent data
*Decided by the orchestrator under the user's delegation (2026-11-26).*

After this change the Session Agent sends only: status polls, prompt answers, screen captures and session-end requests. The service uses them only to show texts, to mute and minimize windows, to record a prompt answer, to store a capture and to ask for a Session End Hold under its rate limit. Category Usage, `ForegroundHistory`, App Usage, the Allowance Time, suspensions and resumes do not read agent data (spec "No limit depends on data of the Session Agent").

### D11. Migration: the HTTP agent routes go at once
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The five routes below `/api/agent/` go in the same release as the channel. The web server answers 404 for them.
- The installer ships the agent and the service together. It closes the running agent (`CloseApplications=force` and `taskkill`), and the new service starts a new agent with a channel.
- An old agent that survives the update gets 404 and shows a stale widget until the next sign-in. No limit depends on it.
- To keep the routes for one version keeps #6 open for that whole version. Rejected.
- `/`, `/request`, `/api/request` and the web admin are not agent routes and stay on HTTP. The port-squat warning (`ReportSquatAsync`) no longer names `/api/agent/activity`.

## Risks / Trade-offs

**What the child can still do after this change (residual risk):**
- **Fake its own screen captures.** With code in its agent, the child can upload any image as its capture. Captures are a monitoring help, not a limit. A later change can move the capture into the Session Probe.
- **Get about 80 seconds of play per 15 minutes** with a false session-end request (the hold plus 20 s), as today. The rate limit stays.
- **Stop its own widget, warnings in the agent, mute and minimize.** This hurts only the child. The service warnings through `WTSSendMessage` and the suspensions do not depend on the agent.
- **Answer its own prompts with any answer.** The answer is the child's anyway.
- **Use a program without input focus**, for example a video in a background window, or a game that reads raw input with `RIDEV_INPUTSINK`, or leave a game in front without use. Out of scope (idle and background use).
- **Copy or rename a game exe** so that a file-name rule of another category matches. Out of scope (stage 4: hash or signature rules).
- **Resume a Suspended App with a tool again and again** (#24). Out of scope.
- None of these items changes Category Usage or the set of Foreground Window exes. That holds because the facts come from the Session Probe.

**Other risks:**
- [A bug in the probe is SYSTEM code in the session of the child] → The probe has no window and no input from the child except the strings that it reads (title, path), which it only copies. It has no privileges (D2), the environment of SYSTEM, and files that only administrators can change. Native AOT is a later option.
- [Handle inheritance across sessions with `CreateProcessAsUserW` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` does not work as expected] → Task 1.1 checks it on the VM before any other work. If it fails, D7 uses the rejected named-pipe variant with the client check, and D3 uses a named pipe with a SYSTEM-only DACL.
- [A SYSTEM process in the session delays the end of the session or keeps the session alive] → The probe has no window, so it gets no `WM_QUERYENDSESSION`. The service ends it at `WTS_SESSION_LOGOFF`. Task 1.4 checks on the VM that the sign-out completes and that `query session` shows no session left.
- [The probe cannot open a desktop that the child created with a tool] → Task 1.2 checks it. If even a desktop with the default DACL fails, the D6a lock would hit each use of such a tool; then run `/opsx:update` before group 4.
- [The Job Object does not accept a process of another session] → The pipe ends the probe with the service (D2), and the service ends leftover probes at its start. Task 1.4 checks both.
- [A program that takes input without focus] → A game that reads raw input with `RIDEV_INPUTSINK` gets input while another window is in front. Few games play this way. It is a residual risk.
- [After a service restart `ForegroundHistory` is empty] → Unchanged trade-off of `add-app-categories` D3b. With 5-second samples the set fills faster.
- [The stage 2 drafts (#31 usage breaks, #33 blocked programs) mention `/api/agent/activity` or modify other requirements of `category-limits`] → They modify other requirements. The scenario "Agent samples do not count" of #31 stays true (the route answers 404). Archive the changes in the order of their merge.

## Migration Plan

1. One release contains the probe, the channel, the new agent and the installer change.
2. The installer stops the service and the agent, removes the `Run` value, installs `{app}\probe`, and starts the service.
3. The service starts a probe and an agent in each existing session of a Restricted User, and an agent in each other session.
4. Rollback: an install of the previous version brings back the HTTP routes, the `Run` value and the old agent. No table and no column change, so the database needs no rollback.

## Open Questions

None that change the specs or the tasks. The sample interval (5 s) and the gap cap (10 s) are constants that the VM runs can tune.
