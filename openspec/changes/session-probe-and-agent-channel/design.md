# Design

## Context

See proposal.md for the motivation and specs/ for the required behavior.

Current state that shapes the approach:
- The service runs as LocalSystem in session 0. It lists the processes of the child's sessions itself (`ProcessControl.ListProcesses`: pid, session, exe path, start time, thread state) and suspends and resumes them itself. `ListProcesses` leaves out a process whose start time it cannot read (`OpenProcess` fails).
- The Session Agent (`ParentalControl.Agent.exe`) runs as the child. `SessionTracker.EnsureAgentRunning` starts it with `WTSQueryUserToken` + `CreateProcessAsUserW` (`SessionManager.LaunchProcessInSession`) and closes the handles of `PROCESS_INFORMATION` at once. The installer also starts it at each sign-in through the value `ParentalControlAgent` below `HKLM\...\CurrentVersion\Run`.
- The agent talks to the service over HTTP on `127.0.0.1:5050`, with no authentication: `GET /api/agent/status?user=` every 5 s, `POST /api/agent/activity` every 20 s (the Foreground Window sample, the only source of Category Usage and of `ForegroundHistory`), `POST /api/agent/prompt/respond`, `POST /api/agent/capture` (user in the header `X-User`) and `POST /api/agent/session-ending` (session ID in the body).
- The agent keeps one instance per user with the named mutex `Global\ParentalControlAgent_{UserName}`. `EnsureAgentRunning` finds an agent by its image path in the session.
- The default App Rules put the install folder in System (`CategoryDefaults`), so the window of the Session Agent counts no time. `SuspensionPlanner` never suspends the agent exe, an exe with the file name `msedgewebview2.exe`, or an exe below the Windows folder.
- `build.ps1` publishes the service, the admin app and the agent self-contained for `win-x64`. All projects target `net8.0`. .NET 8 support ended on 2026-11-10; .NET 10 (LTS) has support until 2028-11-14.
- Existing holes that this change also closes: any process can poll `/api/agent/status?user=Kid`, which clears the capture request of the parent and shows the active prompt of any user. `/api/agent/prompt/respond` accepts any prompt ID.

Threat model: the child is 10 years old and has a standard account. It can run any program in its session, kill or replace its agent process, read and write the memory of the agent (same user, same integrity level), set its own user environment variables, change the DACL of its own processes and threads (it is their owner), create desktops with its own security descriptor, read the CPU times of each process (`NtQuerySystemInformation` gives them to each user), and send any request to `127.0.0.1:5050`. It cannot run code as SYSTEM or as an administrator.

### What a SYSTEM service in session 0 can see
- **Can see:** each process in each session, its user, its session, its exe path, its start time and the state of its threads (`OpenProcess`, `QueryFullProcessImageNameW`, `GetProcessTimes`, `ProcessIdToSessionId`, `NtQuerySystemInformation`). The lock state of a session (WTS notifications).
- **Cannot see:** the Foreground Window of another session. `GetForegroundWindow`, `GetGUIThreadInfo` and `GetWindowThreadProcessId` work only on the desktop of the calling thread. A thread in session 0 cannot attach to the window station `WinSta0` of session 1, because `OpenWindowStationW` and `OpenDesktopW` resolve names in the session of the caller. The WTS functions give the state of a session, not its windows. We found no documented API that gives the Foreground Window of another session to a process in session 0.
- So the facts about the Foreground Window must come from a process in the session of the child. That process must not run as the child.

## Goals / Non-Goals

**Goals:**
- No limit depends on data that a process of the child can write.
- A child action that hides the Foreground Window from the service fails closed (a lock), not open (free time).
- The child cannot stop the start of the Session Agent (#32).
- Each agent message has the user and the session of the agent that the service started.
- The pure logic (sample counting, blind-sample budget, probe outage, channel framing, launch record) runs and is tested on the Mac.

**Non-Goals:**
- Trustworthy data from the Session Agent. The child controls that process. This design does not try to protect it, and it does not need to.
- Protection against an administrator or against code that runs as SYSTEM.

## Decisions

The user delegated each open question. Each decision below records the recommended option and the rejected alternatives. The revision of 2026-11-27 answers the security review on PR #43 (B1, B2, S1–S8, N1–N10); each changed decision names the finding.

### D1. The service gets the Foreground Window from a Session Probe that runs as SYSTEM
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The service starts `ParentalControl.Probe.exe` in each session of a Restricted User, with a SYSTEM token that it moves to that session. The probe has no window. At random intervals with a mean of about 5 seconds (D4) it reads the Foreground Window of the input desktop and sends a sample to the service. Category Usage, `ForegroundHistory`, App Usage and the live activity of the admin page take only these samples and the process list of the service.

Why SYSTEM: a process of the child cannot open a SYSTEM process for `PROCESS_TERMINATE`, `PROCESS_SUSPEND_RESUME`, `PROCESS_VM_READ` or `PROCESS_VM_WRITE`. The explicit security descriptor of the probe process and thread (D2) gives the child no access, and the mandatory label (System integrity) blocks write access from the medium integrity level. UIPI blocks window hooks and window messages from the child into a process at a higher integrity level, and the probe has no window anyway.

Rejected alternatives:
- **A helper that runs as the child.** It has the same weak point as the agent: the child can kill it, inject code into it, or replace its data.
- **Agent samples checked against the process list.** The service can check that the reported pid exists in the session with that exe path. But the child can report a Study exe that runs in the background while a game is in front. The check does not find this.
- **Count from the process list only** (a category counts while one of its processes runs). `add-app-categories` D1 rejected it: launchers and tray helpers count all day. The parent chose Foreground Window counting.
- **Audio, GPU or input heuristics** to detect use. They are a separate topic (idle detection, background video) and are out of scope.

### D2. How the service starts the Session Probe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S3, S4, N5–N8, N10).*

The service (`SessionProbeHost`, new) starts the probe for each session of a Restricted User at once at `WTS_SESSION_LOGON` and `WTS_SESSION_UNLOCK` (N10), and in the 60-second tick if no probe of the service runs in that session.

WinAPI calls, in order:
1. `OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID)`.
2. `CreateRestrictedToken(..., DISABLE_MAX_PRIVILEGE, ...)`: the probe keeps only `SeChangeNotifyPrivilege`. It needs no privilege, because it opens no process (D3). The spike (task 1.3) also tries `WRITE_RESTRICTED` with the restricting SID `S-1-5-18` (N7). The probe uses it only if the probe still samples and writes its pipe with it.
3. `DuplicateTokenEx(..., TokenPrimary)`, `SetTokenInformation(TokenSessionId, sessionId)` (needs `SeTcbPrivilege` in the service, which LocalSystem has), and `SetTokenInformation(TokenDefaultDacl)` with a DACL for SYSTEM only. So each object that the probe or its runtime creates is closed to the child.
4. The environment block: `CreateEnvironmentBlock(out env, systemToken, false)` (the environment of SYSTEM, never that of the child). The service removes each variable that starts with `DOTNET_`, `COMPlus_` or `CORECLR_`, and then adds `DOTNET_EnableDiagnostics=0` (no diagnostic IPC pipe, no debugger, no profiler) and `DOTNET_DISABLE_GUI_ERRORS=1` (S4, S3). The child can set `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT`, `COMPlus_*` and `PATH` in `HKCU\Environment`; none of them reaches the probe.
5. The pipe (D3), with an explicit SYSTEM-only security descriptor (N1). The service creates both ends non-inheritable. Directly before step 8 it makes an inheritable duplicate of the write end, and closes that duplicate directly after step 8. Steps 5 to 8 hold the process-wide lock `ProcessLaunchLock` (Core), which `UpdateService` also takes for its `Process.Start`. So no other process start of the service can inherit the handle (N6).
6. `InitializeProcThreadAttributeList` and `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_HANDLE_LIST)` with only the inheritable duplicate.
7. `CreateJobObjectW` and `SetInformationJobObject(JobObjectExtendedLimitInformation, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)`. The service holds the only job handle. This is defense in depth only: the remarks of `AssignProcessToJobObject` say that the processes of a job must run in the session of the job, and the job of the service is in session 0. Task 1.4 checks it. If the assignment fails, the service logs it once and starts the probe without the job.
8. `CreateProcessAsUserW(token, lpProcessAttributes and lpThreadAttributes with a DACL for SYSTEM only (S4), bInheritHandles: true, EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | HIGH_PRIORITY_CLASS, env, "{app}\probe", STARTUPINFOEX with lpDesktop "winsta0\default")`, then `AssignProcessToJobObject` and `ResumeThread`.
9. The service keeps the process handle. `WaitForSingleObject` on it (through `RegisteredWaitHandle`) tells the service that the probe ended.

`HIGH_PRIORITY_CLASS` (N8): a standard user can raise its own processes to High, but not to Realtime. With the probe at High, a CPU load of the child cannot delay a sample for long. The probe uses a few milliseconds per sample. Task 1.5 measures the gaps under such a load.

The probe program (S3, S4):
- It targets `net10.0-windows` and `build.ps1` publishes it self-contained for `win-x64` into `{app}\probe`, like the other parts. So no runtime search happens, and no `DOTNET_ROOT` applies. Only administrators can change `{app}\probe`. The move of all projects from .NET 8 to .NET 10 is a separate chore that comes before group 3 (task 1.0): .NET 8 is out of support, and one runtime for all parts is simpler than a mixed solution. It changes CI (`setup-dotnet`), the snapshot `test-base` of the VM, and `CLAUDE.md`.
- It is a console program (`OutputType` `Exe`), not `WinExe`. With `CREATE_NO_WINDOW` it gets no console window. A `WinExe` apphost shows a message box when the start fails; a console apphost writes to `stderr`, which goes nowhere.
- At the start of `Main`: `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX)` and `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)`. The assembly has `DefaultDllImportSearchPaths(System32)`. `WerSetFlags` has no documented flag that turns off the error UI, so the design does not rely on it; task 1.6 proves that a forced crash shows no window.
- It references no project of the solution: no Core (no Serilog, no database code, no HTTP code), no Domain. It has its own small set of P/Invoke declarations. It uses no COM, no `Microsoft.Win32.SystemEvents` (it creates a hidden window), no `System.Windows.Forms` and no WPF. Its sampler thread is an MTA thread and creates no window and no hook (S8: `SetThreadDesktop` fails else). It writes no file: its few log lines go through the pipe (D3).

How a probe ends:
- The primary mechanism is the pipe. When the service process ends, also after a crash, the read end closes, the next write of the probe fails with `ERROR_BROKEN_PIPE` or `ERROR_NO_DATA`, and the probe exits within one sample interval (at most 10 seconds).
- The service ends a probe with `TerminateProcess` at the sign-out of its session (`WTS_SESSION_LOGOFF`) and at the service stop.
- At its start, the service ends each leftover probe of an earlier run: a process whose exe path is `{app}\probe\ParentalControl.Probe.exe` and whose token user is `S-1-5-18` (N5). It reads the token user with `OpenProcessToken` + `GetTokenInformation(TokenUser)`. A process with that path and another user is not ended; the service logs it.
- The hard rule covers the programs of the child, not this helper of the service.

Rejected alternatives:
- **Native AOT for the probe** (no .NET runtime in the process at all). It is a stronger option, but AOT cannot cross-compile from the Mac, and `build.ps1` and CI need a separate publish step. A later change can add it.
- **A probe in C or C++.** A second toolchain for one small program.
- **A probe with all SYSTEM privileges.** It needs none, and each privilege makes a bug in the probe worse.
- **A framework-dependent probe** (the first draft). It contradicts `build.ps1` and adds a runtime search.
- **Only the probe on .NET 10, the rest on .NET 8.** It works, but all parts are out of support, CI and the VM need two SDKs anyway, and the chore is small.
- **`PROC_THREAD_ATTRIBUTE_JOB_LIST`** instead of `CREATE_SUSPENDED` + `AssignProcessToJobObject`. It works only on newer Windows 10 builds; the two calls work on each supported version.

### D3. The probe sends window handles and ids through an anonymous pipe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for B2, S7, N1).*

The service creates an anonymous pipe with `CreatePipe` and an explicit security descriptor for SYSTEM only (N1). `CreatePipe` makes a named pipe with a unique name and one instance. Without the explicit descriptor, the DACL comes from the token of the creator; the explicit descriptor removes this dependency. The probe inherits only the write end (D2) and gets its handle value on the command line. The child cannot open the pipe, and it cannot create a second instance.

The probe opens no process (B2). The child owns its game process and can remove SYSTEM from its DACL with no code; a probe without privileges then fails at `OpenProcess`. So the probe sends only what it reads from the window manager, and the service reads the process itself (D3b).

Format: one line of UTF-8 JSON per sample, at most 4 KB. Kinds:
- A window in front: `{ "fg": { "hwnd": 1, "tid": 2, "pid": 3 }, "focus": { ... }, "frame": { ... }, "title": "...", "desktop": "..." }`. `fg` is the Foreground Window. `focus` is the window with the keyboard focus from `GetGUIThreadInfo` (D4), only if its process differs from `fg`. `frame` is the first child window of another process, only if the class of the `fg` window is `ApplicationFrameWindow` (a Store app). Only `fg` is required.
- No window in front (`GetForegroundWindow` returns NULL): `{ "window": false }`.
- A blind sample: `{ "blind": "desktop" }` (the input desktop cannot be opened or set) or `{ "blind": "window" }` (a window is in front, but `GetWindowThreadProcessId` gives no thread after one retry). A found window never gives an empty `fg`.
- A log line: `{ "log": "..." }`, at most 256 characters, at most one per minute.

The window title and the desktop name come from programs of the child. The probe cuts the title at 256 and the name at 64 UTF-16 characters, and replaces each lone surrogate and each control character with U+FFFD before it serializes them. So an honest line is always much shorter than 4 KB.

The service reads the pipe with a fixed buffer of 4 KB per line (S7), not with `StreamReader.ReadLine`: it drops a line that is longer, up to the next line end, without a larger buffer. It ignores a time in the message and uses its own clock (`Environment.TickCount64`) at the receipt. A line over 4 KB, a line that does not parse, or a field with a wrong type drops only that one line or that field, with a log line at most once per probe run. A dropped line is no sample, so its interval counts no time and is not blind; the probe is SYSTEM code, so such a line is a bug, not a child action. Content never ends the probe: else a program of the child could cause a probe outage (D6) with a strange title.

### D3b. The service reads the process of a sample, and fails closed if it cannot (B2)
*Decided by the orchestrator under the user's delegation (2026-11-27).*

At its start the service enables `SeDebugPrivilege` in its own token (`AdjustTokenPrivileges`). The LocalSystem token has the privilege, and Microsoft lists it as enabled, but the service does not rely on that. With it, `OpenProcess` grants the access "regardless of the contents of the security descriptor" (OpenProcess docs), so a DACL that the child sets on its game does not stop the service. If the enable fails, the service logs an error and sends one alert per day. `ProcessControl.ListProcesses`, the suspension and the Blocked Start check use the same token, so this also closes the hole that a game with a hostile DACL is left out of the process list.

For each entry (`fg`, `focus`, `frame`) of a sample the service does:
1. `OpenThread(THREAD_QUERY_LIMITED_INFORMATION, tid)` and `GetProcessIdOfThread` must give `pid`. This ties the window to the process at the time of the sample and guards against pid reuse.
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid)`, `ProcessIdToSessionId`, `QueryFullProcessImageNameW`, `GetProcessTimes`. The session must be the session of the probe, and the start time must be before the receipt.
3. For `frame`: the exe of `fg` must be `%windir%\System32\ApplicationFrameHost.exe`. Else the service ignores `frame`: the child can register a window class with the name `ApplicationFrameWindow`.

Outcomes of an entry:
- **Read:** the service has the exe path and the start time.
- **Ended:** the thread or the process does not exist any more (`ERROR_INVALID_PARAMETER`). The window closed during the sample. Only that entry counts no time; the other entries of the sample count. It is not blind.
- **Unreadable:** each other failure: access denied, a pid of another session, a thread of another process, a start time after the receipt. The sample is a blind sample (D6a), like `{ "blind": ... }`. Only a protected process can deny a SYSTEM caller with `SeDebugPrivilege`, and the child cannot start one; so this outcome means a child action or a bug, and it fails closed.

### D4. Sampling in the probe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S6, S8, N4).*

The sampler thread of the probe (MTA, never a window or a hook) does this for each sample:
1. `OpenInputDesktop(0, false, DESKTOP_READOBJECTS)`. If it fails, the sample is `{ "blind": "desktop" }`.
2. `SetThreadDesktop` to that desktop. So the probe follows a desktop that the child created and switched to. A desktop that a tool such as Sysinternals Desktops creates gets the default DACL of the child's token, which gives SYSTEM full access. A desktop that the child creates with its own security descriptor can deny SYSTEM; then step 1 or 2 fails, and the sample is blind (D6a). `GetUserObjectInformationW(UOI_NAME)` gives the name for the log only. The probe has no rule by desktop name: the child chooses the names of its own desktops.
3. `GetForegroundWindow`. If it gives NULL, the sample is `{ "window": false }`. Else `GetWindowThreadProcessId`; if it gives no thread, the probe tries steps 3 once more, and then sends `{ "blind": "window" }`.
4. `GetGUIThreadInfo(0, ...)` gives `hwndFocus` of the foreground thread (S6). If it is not NULL and `GetWindowThreadProcessId(hwndFocus)` gives another process than the Foreground Window, the sample gets `focus`. A window of another process can have the focus if the child put it into a foreign window with `SetParent`, or with `AttachThreadInput`.
5. If the class of the Foreground Window is `ApplicationFrameWindow`, `EnumChildWindows` finds the first child window of another process (`frame`), as the agent does today. The code moves from the agent `ActivityTracker` into the probe project.
6. `GetWindowTextW`, at most 256 characters. For a window of another process, this reads the stored caption and sends no message, so a hung window cannot block the probe.
7. `SetThreadDesktop` back to the original desktop of the thread (from `GetThreadDesktop` at the start), then `CloseDesktop` on the handle of step 1 (S8). `CloseDesktop` fails while a thread of the process uses the desktop, so the order matters; else the probe leaks one desktop handle per sample. If the restore fails, the probe exits; the service starts a new one (D6).

On the lock screen the session is locked and nothing counts. On the secure desktop of UAC, `consent.exe` is in front, which is below the Windows folder and so in System. Task 1.5 checks that UAC, Ctrl+Alt+Del, the secure screen saver and a fast user switch give no blind sample.

**The sample times are random (N4).** The child can read the CPU time of the probe in Task Manager or with `NtQuerySystemInformation`, so it can see when a sample happens. With a fixed interval it could switch to a hidden desktop or another window right after each sample and come back before the next one. So the probe waits 1 second plus an exponential random time with a mean of 4 seconds, at most 10 seconds in total, before each sample. It takes the random numbers from `RandomNumberGenerator`. After the first second, the time to the next sample does not depend on the time that passed. So the share of samples that see the hidden state is the share of time in that state, and the budget of D6a holds.

### D5. Counting from samples
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S1, S5, S6).*

A pure class `ForegroundSampleCounter` (Domain) keeps, per session, the receipt time of the last sample. For each sample it returns the seconds to add: the time since the last sample of the session, at most 12 seconds (the longest interval of D4 plus a margin), and zero for the first sample after a probe start.

The **counted processes** of a sample (S6) are the read entries `fg`, `focus` and `frame`, minus each one in System, and minus a `focus` with the file name `msedgewebview2.exe` if `fg` is not System. The service adds the interval to the category of each counted process, at most once per category. So a game that the child put into a System window counts; a System window that the child put into a game does not hide the game; a Study window that the child put into a game and gave the focus does not hide the game either (a mouse-driven game still plays; both Games and Study count, which fails closed); and a WebView2 control in a Study app counts as Study only. A Store app gives `fg` = `ApplicationFrameHost.exe` (System) and the app as `frame` and usually as `focus`, so only the app counts. Two non-System processes in one sample are rare in normal use; task 1.2 records each case.

The service records the exe of **each** read entry (`fg`, `focus`, `frame`) in `ForegroundHistory`, so an embedded game is a suspend candidate. Then it does with each counted process what `POST /api/agent/activity` does today: skip a locked session, `CategoryUsageRepository.AddSeconds` (not for System and not for a suspended exe), `AppUsageRepository.AddSeconds` with the file name and the title, and `_liveActivities`. The user is the user of the session of the probe.

**The Session Agent counts in Unknown (S1).** A default App Rule puts the install folder in System, so the agent window counted no time. But the child controls the agent: it can load code into it and draw a game in its window. So the counter puts the agent exe in Unknown, whatever the App Rules say. The agent is never suspended. So if Unknown is exhausted and the agent is a counted process for more than 60 seconds within 10 minutes of unlocked time, the service applies the fallback of a process that cannot be suspended: the Enforced Workstation Lock and one alert. The same rule applies to each exe that `SuspensionPlanner` never suspends and that is not in System: an exe with the file name `msedgewebview2.exe`. The 60 seconds let the child read a prompt or use the widget.

The service also starts the agent without the variables `DOTNET_*`, `COMPlus_*` and `CORECLR_*` in its environment (S1). This removes the injection that needs no code; the child can still inject with a tool, which the counting rule above covers.

**`ForegroundHistory` cap (S5).** With a mean interval of about 5 seconds, the probe gives about 17,280 samples per session and day, and a sample has at most three entries. The cap rises from 1000 to 60,000 paths per user and day (more than 3 × 17,280 for a session that is unlocked for 24 hours). The cap stays as a bound on memory. If it is reached anyway, `Record` returns false, and the service applies the Enforced Workstation Lock and sends one alert (fail closed), because an exe that is not in the set is not a suspend candidate.

### D6. When the probe gives no samples (fault)
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for N10).*

A pure class `ProbeOutageMonitor` (Domain) keeps the time of the last sample per session. If an unlocked, connected session of a Restricted User has no sample for 5 minutes, the service writes the event `PROBE_OUTAGE` (new value of the existing `EventType` enum) and sends the parent a Telegram alert. It sends one alert per outage, and at most one per Restricted User and hour (N10), so a repeated fault stays visible. During the gap no category time counts (D5 caps each gap at 12 seconds). The tick starts a new probe if the old one ended. The session Allowance Time counts as before, because it does not depend on samples.

An outage is a fault, not a child action: the child cannot end, suspend or open the probe (D1, D2), and the content of a window cannot end it (D3). A CPU load of the child cannot stop the probe for long (D2, High priority; task 1.5 measures it).

Rejected alternatives:
- **Agent samples as a fallback.** The child controls them. This is the hole of #6.
- **A conservative count from the process list** (each category with a running process that was in front today counts during the gap). It punishes the child for a fault of the service, and the child cannot stop a SYSTEM process.
- **The Enforced Workstation Lock during a gap**, as in Data Folder Lockdown. Too strong for a fault of a helper. The alert lets the parent act.

### D6a. Blind samples lock the session (B1)
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for B1 and B2).*

A blind sample is a sample that shows that a window or desktop is there, but that the service cannot attribute to an exe: `{ "blind": "desktop" }`, `{ "blind": "window" }`, or an entry that the service cannot read (D3b). The child can cause each of them: a desktop with a security descriptor that denies SYSTEM, a DACL on its game process or thread. So blind samples must fail closed.

The first draft locked after 30 seconds of only blind samples, and one readable sample reset the timer. The review showed the attack: 25 seconds on the hidden desktop, 5 seconds back on the default desktop, again and again, and the timer never runs out. So the rule is a rolling budget (`BlindSampleBudget`, a pure class in Domain, part of `ProbeOutageMonitor`):
- The service keeps the receipt times of the blind samples of each Restricted User for the last 10 minutes of unlocked, connected time. Locked, disconnected and signed-out time do not count in the 10 minutes, as for Continuous Use. The service ignores blind samples from a session that is locked or not connected (for example after a fast user switch). Readable samples do not remove a blind sample.
- At the 6th blind sample within these 10 minutes, the service applies the Enforced Workstation Lock (`SessionManager.LockSession`), writes the event `PROBE_DESKTOP_DENIED` (new value of `EventType`) and sends one Telegram alert per Restricted User and day. 6 samples are about 30 seconds of hidden time (D4).
- After the first such lock of a Restricted User on a day, each blind sample on that day locks at once. A child who tried once does not get a new budget after each unlock.
- The budget and the strike are in memory. A service restart clears them; only an administrator can restart the service.

With the random sample times of D4, a child who hides for a share of the time produces about that share of blind samples. The alternation of 25 s and 5 s gives about 5 blind samples per 30 seconds and a lock within about 40 seconds. A child who hides only for 1 second after each sample gets nothing to play with.

This follows the fallback when a process cannot be suspended (`add-app-categories` D6). No program is closed. At unlock Windows shows the default desktop again.

Rejected alternatives:
- **A timer that a readable sample resets** (the first draft). The alternation attack defeats it.
- **No count while the desktop is not readable.** This is the hole of #6 again, through a different door.
- **A conservative count from the process list.** A game that the child starts on the hidden desktop was never seen in front, so it is not a candidate (D3b of `add-app-categories`), and the count misses it.
- **Give the probe a privilege to bypass the desktop DACL** (`SeTakeOwnershipPrivilege` and a new DACL on the desktop of the child). This changes an object of the child and gives the probe a strong privilege. The lock is simpler and sure.

### D7. The Agent Channel: a pipe that the service owns, with the client end inherited by the agent
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S7, N2, N3, N9).*

For each agent start, the service (`AgentChannelHost`, new):
1. Creates a named pipe `\\.\pipe\ParentalControl.Agent.<32 random hex digits>` with `CreateNamedPipeW(PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, maxInstances: 1)` and a security descriptor that gives access to SYSTEM only. The child cannot open the pipe by name and cannot create an instance.
2. Opens the client end itself with `CreateFileW(GENERIC_READ | GENERIC_WRITE, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION)`, not inheritable. `ConnectNamedPipe` completes at once.
3. Starts the agent with the user token as today (`WTSQueryUserToken`, `DuplicateTokenEx`, `CreateEnvironmentBlock` with the user token, minus `DOTNET_*`, `COMPlus_*` and `CORECLR_*` (D5)), but with `bInheritHandles: true`, `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` with an inheritable duplicate of the client handle, under `ProcessLaunchLock` (D2, N6). The command line is `"ParentalControl.Agent.exe" --channel <handle value>`.
4. Closes its copies of the client handle and keeps the launch record: the server end, the agent process handle, the pid, the start time, the session ID and the user SID. The user SID comes from `GetTokenInformation(TokenUser)` of the token that started the agent (N9), not from a user name.

The agent wraps the handle in a `SafePipeHandle` and a `NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle)`. It checks `GetFileType == FILE_TYPE_PIPE` and `GetNamedPipeServerSessionId == 0`, else it exits. This check is only a guard for the user interface (N2): a fake server of the child can only feed false texts to the agent of the child, which the child controls anyway. No security decision depends on it. If the channel breaks, the agent exits. A new service run starts a new agent (D9).

Framing: each message is a 4-byte little-endian length and a UTF-8 JSON object with `type` and `id`. The agent sends requests; the service answers each one. Types: `status`, `promptAnswer` (`promptId`, `response`, `reason`, `turnaroundSeconds`), `capture` (`trigger`, `width`, `height`, then the JPEG bytes as a second frame), `sessionEnding` (`reason`). No message has a user or a session field; the service ignores such fields (spec "The service takes the identity of an agent message from the launch"). A pure class `AgentChannelCodec` (Domain) does the framing and the limits.

**Limits against a flood or a stall (S7).** The child controls the client end, so the service treats the channel as hostile input:
- The service reads the 4-byte length first and checks it before it allocates: at most 64 KB for a JSON frame, at most `CaptureQuota.MaxBytesPerCapture` for a JPEG frame that follows a `capture` frame. A larger length closes the channel.
- All reads and writes are asynchronous (overlapped). A frame must arrive within 10 seconds after its length; a write of the service must complete within 5 seconds. Else the service closes the channel. A slow or silent agent never blocks a thread of the service or another channel.
- Each channel has a rate limit: at most 5 messages per second and 30 per 10 seconds; a capture counts against `CaptureQuota` as today. Over the limit the service drops the message, and after 10 dropped messages it closes the channel.
- Each channel object holds its own launch record; no handler reads a shared "current user". After a close, the agent exits and the service starts a new one within 60 seconds (D9). The child can only stop its own agent with a flood.

**What this proves, and what it does not.** It proves that a message comes through the handle that the service gave to the agent process that it started, in that session, as that user. It does not prove that the agent code sent it. The child owns the agent process: it can load a DLL into it with `SetWindowsHookEx`, start a thread in it with `CreateRemoteThread`, attach a debugger, or copy the channel handle into another process with `DuplicateHandle`. So the channel gives the identity of the session and the user, not trust in the data. D10 makes sure that no limit needs more.

Rejected alternatives:
- **A named pipe with an ACE for the child's SID, and a check of the client** (`GetNamedPipeClientProcessId`, then the image path below the install folder, `ProcessIdToSessionId`, and pid + start time equal to the launch record). It proves the same as D7 and no more: the child can still inject or duplicate the handle. It needs more parts: an ACE that must not include `FILE_CREATE_PIPE_INSTANCE` (else the child creates a server instance and takes the agent's connection), a pid check with a race against pid reuse, and a reconnect logic. It stays the fallback if the VM check of task 1.1 shows that the handle inheritance across sessions does not work. In the fallback, the service takes the user SID and the session from the client token (`ImpersonateNamedPipeClient`, `OpenThreadToken`, `TokenUser`, `TokenSessionId`), not only from the pid (N3).
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

After this change the Session Agent sends only: status polls, prompt answers, screen captures and session-end requests. The service uses them only to show texts, to mute and minimize windows, to record a prompt answer, to store a capture and to ask for a Session End Hold under its rate limit. Category Usage, `ForegroundHistory`, App Usage, the Allowance Time, Continuous Use, suspensions and resumes do not read agent data (spec "No limit depends on data of the Session Agent").

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
- **Get about 25 seconds of hidden play per 10 minutes** if it stays below the budget of D6a (5 blind samples); the 6th locks, and after that lock each blind sample of the day locks. And about 60 seconds per 10 minutes in its agent window after Unknown is exhausted (D5). Hidden play in pieces of a few seconds, with a desktop switch each time, is of little use for a game.
- **Stop its own widget, warnings in the agent, mute and minimize.** This hurts only the child. The service warnings through `WTSSendMessage` and the suspensions do not depend on the agent.
- **Answer its own prompts with any answer.** The answer is the child's anyway.
- **Use a program without input focus**, for example a video in a background window, a game that reads raw input with `RIDEV_INPUTSINK`, or a game whose windows have `WS_EX_NOACTIVATE` and so never become the Foreground Window or get the focus (S6). Or leave a game in front without use. Out of scope (idle and background use).
- **Copy or rename a game exe** so that a file-name rule of another category matches. Out of scope (stage 4: hash or signature rules).
- **Put a game into a folder below `C:\Windows` that a standard user can write** (S2). The exe counts as System and is never suspended. This is a pre-existing hole of the System rule, not of the probe; GitHub issue #45 handles it.
- **Resume a Suspended App with a tool again and again** (#24). Out of scope.
- None of these items changes Category Usage or the set of Foreground Window exes beyond the bounds above. That holds because the facts come from the Session Probe and the service.

**Other risks:**
- [A bug in the probe is SYSTEM code in the session of the child] → The probe has no window and no input from the child except the window handles, ids and strings that it reads, which it only copies. It opens no process. It has no privileges (D2), the environment of SYSTEM without runtime variables, a SYSTEM-only DACL on its process, thread and default objects, no diagnostics, and files that only administrators can change. Native AOT is a later option.
- [The service opens processes of the child with `SeDebugPrivilege`] → It asks only for `PROCESS_QUERY_LIMITED_INFORMATION` and `THREAD_QUERY_LIMITED_INFORMATION` for a sample, as `ListProcesses` does today; the suspension asks for `PROCESS_SUSPEND_RESUME` as today. The privilege changes which handles the service gets, not what it does with them.
- [Handle inheritance across sessions with `CreateProcessAsUserW` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` does not work as expected] → Task 1.1 checks it on the VM before any other work. If it fails, D7 uses the rejected named-pipe variant with the client check, and D3 uses a named pipe with a SYSTEM-only DACL.
- [A SYSTEM process in the session delays the end of the session or keeps the session alive] → The probe has no window, so it gets no `WM_QUERYENDSESSION`. The service ends it at `WTS_SESSION_LOGOFF`. Task 1.4 checks on the VM that the sign-out completes and that `query session` shows no session left.
- [A legitimate desktop or window gives blind samples, and D6a locks the child] → Task 1.5 checks UAC, Ctrl+Alt+Del, the secure screen saver, a fast user switch, and Sysinternals Desktops. If one of them gives blind samples, run `/opsx:update` before group 4. The strike after the first lock makes a false blind sample costly, so this check is a gate.
- [The rule of D5 counts a legitimate program in a second category] → Task 1.2 logs `fg` and `focus` for common programs (Office, a browser, a WebView2 app, a Store app). If a common case gives a wrong category, run `/opsx:update` before group 4.
- [The Job Object does not accept a process of another session] → The pipe ends the probe with the service (D2), and the service ends leftover probes at its start. Task 1.4 checks both.
- [After a service restart `ForegroundHistory`, the blind budget and the strike are empty] → Unchanged trade-off of `add-app-categories` D3b for the history. With about 5-second samples the set fills fast. Only an administrator can restart the service.
- [The stage 2 changes `usage-breaks` (#31) and `blocked-programs` (#33) are merged and archived] → Continuous Use does not read agent data, and its scenario "Agent samples do not count" stays true (the route answers 404). The Blocked Start reads the exe from the process; it gets the benefit of `SeDebugPrivilege` (D3b).

## Migration Plan

1. Task 1.0 (a chore PR before group 3) moves all projects to .NET 10, CI and the snapshot `test-base`.
2. One release contains the probe, the channel, the new agent and the installer change.
3. The installer stops the service and the agent, removes the `Run` value, installs `{app}\probe`, and starts the service.
4. The service starts a probe and an agent in each existing session of a Restricted User, and an agent in each other session.
5. Rollback: an install of the previous version brings back the HTTP routes, the `Run` value and the old agent. No table and no column change, so the database needs no rollback.

## Open Questions

None that change the specs or the tasks. The VM spike (group 1) measures the constants: the sample interval (1 s + exponential with a mean of 4 s, at most 10 s), the gap cap (12 s), the blind budget (6 in 10 minutes), and the agent allowance (60 s in 10 minutes). It can tune them within the specs.
