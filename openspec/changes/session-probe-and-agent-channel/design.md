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

The user delegated each open question. Each decision below records the recommended option and the rejected alternatives. The revision of 2026-11-27 answers the security review on PR #43 (B1, B2, S1–S8, N1–N10). The second revision of 2026-11-27 answers "Re-review 2" on PR #43 (B3, B4, S9–S13, N11–N21). Each changed decision names the finding.

### D1. The service gets the Foreground Window from a Session Probe that runs as SYSTEM
*Decided by the orchestrator under the user's delegation (2026-11-26).*

The service starts `ParentalControl.Probe.exe` in each session of a Restricted User, with a SYSTEM token that it moves to that session. The probe has no window. At random intervals with a mean of about 5 seconds (D4) it reads the windows of the input desktop that the child can use (the Foreground Window, the focus, the visible child windows of other processes, the window under the cursor) and sends a sample to the service. Category Usage, `ForegroundHistory`, App Usage and the live activity of the admin page take only these samples and the process list of the service.

Why SYSTEM: a process of the child cannot open a SYSTEM process for `PROCESS_TERMINATE`, `PROCESS_SUSPEND_RESUME`, `PROCESS_VM_READ` or `PROCESS_VM_WRITE`. The explicit security descriptor of the probe process and thread (D2) gives the child no access, and the mandatory label (System integrity) blocks write access from the medium integrity level. UIPI blocks window hooks and window messages from the child into a process at a higher integrity level, and the probe has no window anyway.

Rejected alternatives:
- **A helper that runs as the child.** It has the same weak point as the agent: the child can kill it, inject code into it, or replace its data.
- **Agent samples checked against the process list.** The service can check that the reported pid exists in the session with that exe path. But the child can report a Study exe that runs in the background while a game is in front. The check does not find this.
- **Count from the process list only** (a category counts while one of its processes runs). `add-app-categories` D1 rejected it: launchers and tray helpers count all day. The parent chose Foreground Window counting.
- **Audio, GPU or input heuristics** to detect use. They are a separate topic (idle detection, background video) and are out of scope.

### D2. How the service starts the Session Probe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S3, S4, N5–N8, N10, and for S13, N12, N17, N21).*

The service (`SessionProbeHost`, new) starts the probe for each session of a Restricted User at once at `WTS_SESSION_LOGON` and `WTS_SESSION_UNLOCK` (N10), and in the 60-second tick if no probe of the service runs in that session.

WinAPI calls, in order:
1. `OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID)`.
2. `CreateRestrictedToken(..., DISABLE_MAX_PRIVILEGE, ...)`: the probe keeps only `SeChangeNotifyPrivilege`. It needs no privilege, because it opens no process (D3). The spike (task 1.3) also tries `WRITE_RESTRICTED` with the restricting SID `S-1-5-18` (N7). The probe uses it only if the probe still samples and writes its pipe with it.
3. `DuplicateTokenEx(..., TokenPrimary)`, `SetTokenInformation(TokenSessionId, sessionId)` (needs `SeTcbPrivilege` in the service, which LocalSystem has), and `SetTokenInformation(TokenDefaultDacl)` with a DACL for SYSTEM only. So each object that the probe or its runtime creates is closed to the child.
4. The environment block: `CreateEnvironmentBlock(out env, systemToken, false)` (the environment of SYSTEM, never that of the child). The service removes each variable that starts with `DOTNET_`, `COMPlus_` or `CORECLR_`, and then adds `DOTNET_EnableDiagnostics=0` (no diagnostic IPC pipe, no debugger, no profiler) and `DOTNET_DISABLE_GUI_ERRORS=1` (S4, S3). The child can set `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT`, `COMPlus_*` and `PATH` in `HKCU\Environment`; none of them reaches the probe.
5. The pipe (D3), with an explicit SYSTEM-only security descriptor (N1). The service creates both ends non-inheritable. Directly before step 7 it makes an inheritable duplicate of the write end, and closes that duplicate directly after step 7. Steps 5 to 7 hold the process-wide lock `ProcessLaunchLock` (Core), which `UpdateService` also takes for its `Process.Start`. So no other process start of the service can inherit the handle (N6).
6. `InitializeProcThreadAttributeList` and `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_HANDLE_LIST)` with only the inheritable duplicate.
7. `CreateProcessAsUserW(token, lpProcessAttributes and lpThreadAttributes with a DACL for SYSTEM only (S4), bInheritHandles: true, EXTENDED_STARTUPINFO_PRESENT | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT | HIGH_PRIORITY_CLASS, env, "{app}\probe", STARTUPINFOEX with lpDesktop "winsta0\default")`.
8. The service keeps the process handle. `WaitForSingleObject` on it (through `RegisteredWaitHandle`) tells the service that the probe ended.

**No Job Object (N17).** The first revisions put the probe into a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The remarks of `AssignProcessToJobObject` say that all processes of a job must run in the same session, and the job of the service is in session 0. So that step always fails for the probe. The design drops it, and with it `CREATE_SUSPENDED` and `ResumeThread`. The pipe and the leftover cleanup below end a probe.

**`DETACHED_PROCESS`, not `CREATE_NO_WINDOW` (S13).** With `CREATE_NO_WINDOW`, the new console process gets no visible console window, but the console allocation design of Windows Terminal (spec #7335) says that a console host (`conhost.exe`) still starts for it with an invisible window. That is an extra SYSTEM process with a window in the session of the child. With `DETACHED_PROCESS`, a console process gets no console and no console host. The probe needs no console. The spec is a design document, not a guarantee, so task 1.3 proves it on the VM: the probe has no `conhost.exe` child, and no window belongs to the probe or to such a child.

**Priority (N8, N12).** A standard user can raise its own processes to High, but not to Realtime. The probe runs at `HIGH_PRIORITY_CLASS`, and its sampler thread at `THREAD_PRIORITY_TIME_CRITICAL` (base priority 15). A thread of the child can reach 15 too (High class and time-critical), but not more. So a CPU load of the child shares the CPU with the sampler in turns and cannot stop it. The probe uses a few milliseconds per sample. Task 1.5 measures the gaps under a load of time-critical threads of `Kid`. The service sample time comes from the probe (D3), so a delay of the service reader does not change the counted time (S9).

The probe program (S3, S4):
- It targets `net10.0-windows` and `build.ps1` publishes it self-contained for `win-x64` into `{app}\probe`, like the other parts. So no runtime search happens, and no `DOTNET_ROOT` applies. Only administrators can change `{app}\probe`. The move of all projects from .NET 8 to .NET 10 is a separate chore that comes before group 3 (task 1.0): .NET 8 is out of support, and one runtime for all parts is simpler than a mixed solution. It changes CI (`setup-dotnet`), the snapshot `test-base` of the VM, and `CLAUDE.md`.
- It is a console program (`OutputType` `Exe`), not `WinExe`. With `DETACHED_PROCESS` it gets no console and no console host (S13). A `WinExe` apphost shows a message box when the start fails; a console apphost writes to `stderr`, which goes nowhere.
- The project sets `<StartupHookSupport>false</StartupHookSupport>` (N21). The runtime then ignores `DOTNET_STARTUP_HOOKS`, also if the environment strip of step 4 has a bug.
- At the start of `Main`: `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX)` and `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)`. The assembly has `DefaultDllImportSearchPaths(System32)`. `WerSetFlags` has no documented flag that turns off the error UI, so the design does not rely on it; task 1.6 proves that a forced crash shows no window.
- It references no project of the solution: no Core (no Serilog, no database code, no HTTP code), no Domain. It has its own small set of P/Invoke declarations. It uses no COM, no `Microsoft.Win32.SystemEvents` (it creates a hidden window), no `System.Windows.Forms` and no WPF. Its sampler thread is an MTA thread at `THREAD_PRIORITY_TIME_CRITICAL` and creates no window and no hook (S8: `SetThreadDesktop` fails else). It writes no file: its few log lines go through the pipe (D3).

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
- **A Job Object with kill-on-close** (the first revisions, N17). A job in session 0 cannot hold a process of another session, so the step always fails.
- **`CREATE_NO_WINDOW`** (the first revisions, S13). It still starts a hidden console host.
- **`REALTIME_PRIORITY_CLASS` for the probe** (N12). The service could set it, but a bug in a realtime SYSTEM process can freeze the PC of the child. Time-critical in the High class is enough to get a turn.

### D3. The probe sends window handles and ids through an anonymous pipe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for B2, S7, N1, and for B3, S9, N15, N19).*

The service creates an anonymous pipe with `CreatePipe` and an explicit security descriptor for SYSTEM only (N1). `CreatePipe` makes a named pipe with a unique name and one instance. Without the explicit descriptor, the DACL comes from the token of the creator; the explicit descriptor removes this dependency. The probe inherits only the write end (D2) and gets its handle value on the command line. The child cannot open the pipe, and it cannot create a second instance.

The probe opens no process (B2). The child owns its game process and can remove SYSTEM from its DACL with no code; a probe without privileges then fails at `OpenProcess`. So the probe sends only what it reads from the window manager, and the service reads the process itself (D3b).

Format: one line of UTF-8 JSON per sample, at most 4 KB. Each sample line has `t`, the `GetTickCount64` value of the probe at the sample (S9). Kinds:
- A window in front: `{ "t": 123, "fg": { "hwnd": 1, "tid": 2, "pid": 3 }, "focus": { ... }, "children": [ { ... } ], "cursor": { ..., "noActivate": true }, "title": "...", "desktop": "..." }`. Each entry has `hwnd`, `tid` and `pid`. Only `fg` is required. The entries (D4):
  - `fg`: the Foreground Window.
  - `focus`: the window with the keyboard focus from `GetGUIThreadInfo`, only if its process differs from `fg`.
  - `children`: one visible child window of each other process below the `fg` window, at most 8 processes (B3). This replaces the `frame` entry of the first revisions (the first child of an `ApplicationFrameWindow`).
  - `cursor`: the top-level window under the cursor, only if its process differs from `fg` and from each entry above, with the flag `noActivate` if it has the style `WS_EX_NOACTIVATE` (B3).
- No window in front (`GetForegroundWindow` returns NULL): `{ "t": 123, "window": false }`.
- A blind sample: `{ "t": 123, "blind": "desktop" }` (the input desktop cannot be opened or set), `{ "t": 123, "blind": "window" }` (a window is in front, but `GetWindowThreadProcessId` gives no thread after one retry), or `{ "t": 123, "blind": "children" }` (more than 8 other processes have visible windows below `fg`). A found window never gives an empty `fg`.
- A log line: `{ "log": "..." }`, at most 256 characters, at most one per minute.

The window title and the desktop name come from programs of the child. The probe cuts the title at 256 and the name at 64 UTF-16 characters, and replaces each lone surrogate and each control character with U+FFFD before it serializes them. So an honest line is always much shorter than 4 KB.

The service reads the pipe with a fixed buffer of 4 KB per line (S7), not with `StreamReader.ReadLine`: it drops a line that is longer, up to the next line end, without a larger buffer. A line over 4 KB, a line that does not parse, a sample line without a valid `t`, or a field with a wrong type drops only that one line or that field, with a log line at most once per probe run. Content never ends the probe: else a program of the child could cause a probe outage (D6) with a strange title.

**One blocking reader thread per probe (N19, S9).** Anonymous pipes do not support overlapped I/O (Microsoft Learn, "Anonymous Pipe Operations"), so the service cannot reuse the asynchronous path of the Agent Channel (D7). It reads each probe pipe on its own dedicated thread with a blocking `ReadFile`, at `THREAD_PRIORITY_TIME_CRITICAL`. The same thread reads the entries of the sample (D3b) at once, so the window has little time to go away, and then gives the result to the counting. The thread ends when the pipe breaks.

**The sample time is the time of the probe (S9).** The first revisions used the receipt time of the service. A child that loads all cores delays the service reader, and the queued lines then got receipt times close together, so the counter added less than the real time. Now the service uses `t`. `GetTickCount64` is a system-wide counter, so the probe and the service read the same clock. The probe is SYSTEM code, so its clock is not a child input. The service checks `t`: it must be larger than the `t` of the previous line of the same probe run, and not larger than the current `Environment.TickCount64` of the service. Else the service drops the line as a bug.

**Which lines move the last-sample time (N15).** Each sample line that the service accepts (a window in front, `window: false`, a blind sample) moves the last-sample time of the session to its `t`. Only read entries (D3b) add time. A dropped line moves nothing: it is no sample, so the next accepted sample gets the time since the last accepted sample, at most 12 seconds (D5). The probe is SYSTEM code, so a dropped line is a bug, not a child action, and the 12-second cap bounds its effect.

### D3b. The service reads the process of a sample, and fails closed if it cannot (B2)
*Decided by the orchestrator under the user's delegation (2026-11-27; revised 2026-11-27 for B3, S9, N15).*

At its start the service enables `SeDebugPrivilege` in its own token (`AdjustTokenPrivileges`). The LocalSystem token has the privilege, and Microsoft lists it as enabled, but the service does not rely on that. With it, `OpenProcess` grants the access "regardless of the contents of the security descriptor" (OpenProcess docs), so a DACL that the child sets on its game does not stop the service. If the enable fails, the service logs an error and sends one alert per day. `ProcessControl.ListProcesses`, the suspension and the Blocked Start check use the same token, so this also closes the hole that a game with a hostile DACL is left out of the process list.

For each entry (`fg`, `focus`, each of `children`, `cursor`) of a sample the service does:
1. `OpenThread(THREAD_QUERY_LIMITED_INFORMATION, tid)` and `GetProcessIdOfThread` must give `pid`. This ties the window to the process at the time of the sample and guards against pid reuse.
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid)`, `ProcessIdToSessionId`, `QueryFullProcessImageNameW`, `GetProcessTimes`. The session must be the session of the probe, and the start time must be before the sample time `t` (S9).

The first revisions also checked that the exe of `fg` is `System32\ApplicationFrameHost.exe` before they used `frame`, because the child can register a window class with the name `ApplicationFrameWindow`. That check goes: each visible child window of another process is an entry now (B3), whatever the class of `fg` is. A fake `ApplicationFrameWindow` of a game makes the game `fg`, so the game counts, and a Study child window in it counts too (fail closed).

Outcomes of an entry:
- **Read:** the service has the exe path and the start time.
- **Ended:** the thread or the process does not exist any more (`ERROR_INVALID_PARAMETER`). The window closed between the sample and the read. That entry counts no time; the other entries of the sample count. An Ended `fg` entry also goes into the blind budget of D6a, but it does not trigger the strike (S9): a child who delays the service and closes its game window just before the read gets no free time, and an honest window close, which the time-critical reader makes rare, cannot lock the child at once. An Ended `focus`, child or `cursor` entry is not blind, because windows of normal programs close often.
- **Unreadable:** each other failure: access denied, a pid of another session, a thread of another process, a start time after `t`. The sample is a blind sample (D6a), like `{ "blind": ... }`. Only a protected process can deny a SYSTEM caller with `SeDebugPrivilege`, and the child cannot start one; so this outcome means a child action or a bug, and it fails closed.

If one entry is Unreadable and another entry is Read, the Read entries still count and the sample is also blind (N15). Both sides fail closed.

### D4. Sampling in the probe
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S6, S8, N4, and for B3, N11, N12).*

The sampler thread of the probe (MTA, never a window or a hook) does this for each sample:
1. `OpenInputDesktop(0, false, DESKTOP_READOBJECTS)`. If it fails, the sample is `{ "blind": "desktop" }`.
2. `SetThreadDesktop` to that desktop. So the probe follows a desktop that the child created and switched to. A desktop that a tool such as Sysinternals Desktops creates gets the default DACL of the child's token, which gives SYSTEM full access. A desktop that the child creates with its own security descriptor can deny SYSTEM; then step 1 or 2 fails, and the sample is blind (D6a). `GetUserObjectInformationW(UOI_NAME)` gives the name for the log only. The probe has no rule by desktop name: the child chooses the names of its own desktops.
3. `GetForegroundWindow`. If it gives NULL, the sample is `{ "window": false }`. Else `GetWindowThreadProcessId`; if it gives no thread, the probe tries step 3 once more, and then sends `{ "blind": "window" }`.
4. `GetGUIThreadInfo(0, ...)` gives `hwndFocus` of the foreground thread (S6). If it is not NULL and `GetWindowThreadProcessId(hwndFocus)` gives another process than the Foreground Window, the sample gets `focus`. A window of another process can have the focus if the child put it into a foreign window with `SetParent`, or with `AttachThreadInput`.
5. `EnumChildWindows(fg)` visits all descendant windows of the Foreground Window (B3). For each window that `IsWindowVisible` accepts and that has a non-empty rectangle, the probe takes the process from `GetWindowThreadProcessId`. Each process other than the `fg` process gives one entry in `children` (the first visible window of that process). If more than 8 other processes have such windows, the sample is `{ "blind": "children" }`: a cut list would let the child hide a game behind 8 decoy windows. The probe stops the walk after 4096 windows and then also sends `{ "blind": "children" }`. A Store app is the normal case: `fg` is `ApplicationFrameHost.exe` and the app window is a child. The Store-app code of the agent `ActivityTracker` moves into the probe project in this form.
6. `GetCursorPos`, `WindowFromPoint` and `GetAncestor(GA_ROOT)` give the top-level window under the cursor (B3). If its process differs from `fg` and from each entry of steps 4 and 5, the sample gets `cursor`, with `noActivate: true` if `GetWindowLongPtrW(GWL_EXSTYLE)` has `WS_EX_NOACTIVATE`. `GetCursorPos` needs the input desktop as the thread desktop, which step 2 sets. `WindowFromPoint` skips hidden and disabled windows; a game in such a window gets no mouse input.
7. `GetWindowTextW` of `fg`, at most 256 characters. For a window of another process, this reads the stored caption and sends no message, so a hung window cannot block the probe. Steps 5 and 6 send no message either.
8. `SetThreadDesktop` back to the original desktop of the thread (from `GetThreadDesktop` at the start), then `CloseDesktop` on the handle of step 1 (S8). `CloseDesktop` fails while a thread of the process uses the desktop, so the order matters; else the probe leaks one desktop handle per sample. If the restore fails, the probe exits; the service starts a new one (D6).

On the lock screen the session is locked and nothing counts. On the secure desktop of UAC, `consent.exe` is in front, which is below the Windows folder and so in System. Task 1.5 checks that UAC, Ctrl+Alt+Del, the secure screen saver and a fast user switch give no blind sample.

**Why the children and the cursor (B3).** The first revisions counted `fg`, `focus` and the first foreign child of a Store app frame. Re-review 2 showed that a script can put an unchanged game into `notepad.exe` with `SetParent` (AutoHotkey `DllCall` or PowerShell `Add-Type` is enough). A click on the embedded game activates Notepad, and Notepad gives the focus back to its edit control, so `fg` and `focus` are both Notepad (System) and nothing counted. Step 5 closes this: each visible window of another process below `fg` is an entry. The script can also set `WS_EX_NOACTIVATE` on a top-level game window from outside (`SetWindowLongPtrW(GWL_EXSTYLE)` works across processes at the same integrity level). Then a click on the game does not make it the Foreground Window. Step 6 closes this for a mouse-driven game: the cursor is over the game while the child plays. D5 decides which `cursor` entries count.

**The sample times are random (N4, N11).** The child can read the CPU time of the probe in Task Manager or with `NtQuerySystemInformation`, so it can see when a sample happens. With a fixed interval it could switch to a hidden desktop or another window right after each sample and come back before the next one. So the probe waits 0.2 seconds plus an exponential random time with a mean of 4.8 seconds, at most 10 seconds in total, before each sample. It takes the random numbers from `RandomNumberGenerator`. After the first 0.2 seconds, the time to the next sample does not depend on the time that passed. So the share of samples that see the hidden state is the share of time in that state, and the budget of D6a holds. The first revisions used a minimum of 1 second. Re-review 2 showed that a child who sees each sample could then show the game for about 0.9 seconds after each one without a blind sample (about 20 % of the time). With 0.2 seconds this safe window is about 4 % of the time, and the extra CPU cost is small.

### D5. Counting from samples
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S1, S5, S6, and for B3, B4, S9, S10, N14, N16).*

A pure class `ForegroundSampleCounter` (Domain) keeps, per session, the sample time `t` of the last accepted sample (D3). For each sample it returns the seconds to add: the time since the last sample of the session, at most 12 seconds (the longest interval of D4 plus a margin), and zero for the first sample after a probe start. D3 says which lines move the last-sample time (N15).

**The counted processes of a sample.** A pure class `CountedProcesses` (Domain) takes the Read entries (D3b) and decides:
1. `fg`, `focus` and each entry of `children` count, except each one in System (S6, B3).
2. `cursor` counts only if it has `noActivate` and is not in System (B3). A `cursor` entry without `noActivate` counts no time, but its exe becomes a suspend candidate (step 6).
3. A trusted WebView2 process counts no time of its own as `focus`, as a child or as `cursor` (B4). Its host is the Foreground Window, which counts by step 1; if the host is System, nothing counts. A trusted WebView2 process is an exe with the file name `msedgewebview2.exe` below `%ProgramFiles(x86)%\Microsoft\EdgeWebView\Application\` or `%ProgramFiles%\Microsoft\EdgeWebView\Application\`, the folders of the Evergreen WebView2 Runtime, which only administrators can change. As `fg` it counts as itself, by its App Rule.
4. Each other `msedgewebview2.exe` is an ordinary exe: it counts by its App Rule like any other exe (B4).
5. The Session Agent counts in Unknown, whatever the App Rules say, but only as `fg` or `focus` (S1). As a child or `cursor` entry it counts nothing, so a cursor over the widget costs nothing. While the service has an active Interactive Session Prompt for that user, the agent counts nothing at all (S10).
6. The service adds the interval to the category of each counted process, at most once per category. It records the exe of each Read entry, also of a `cursor` entry without `noActivate`, in `ForegroundHistory`, so each sampled exe is a suspend candidate.

Then it does with each counted process what `POST /api/agent/activity` does today: skip a locked session, `CategoryUsageRepository.AddSeconds` (not for System and not for a suspended exe), `AppUsageRepository.AddSeconds` with the file name and the title, and `_liveActivities`. The user is the user of the session of the probe.

What this gives:
- A game that the child put into a System window with `SetParent` counts as a child entry, also if the focus stays on the host (B3).
- A System window that the child put into a game does not hide the game, because the game is `fg`.
- A Study window that the child put into a game does not hide the game either: both Games and Study count, which fails closed.
- A game window with `WS_EX_NOACTIVATE` counts while the cursor is over it, so a mouse-driven game counts (B3).
- A WebView2 control in a Study app counts as Study only. A WebView2 flyout of `explorer.exe` (Windows 10 "News and interests") counts nothing and cannot lock the child (B4).
- A game that the child copies as `msedgewebview2.exe` to a folder that it can write counts as itself. The rule of a program that is never suspended (below) locks the session if its category is exhausted (B4).
- A Store app gives `fg` = `ApplicationFrameHost.exe` (System) and the app as a child and usually as `focus`, so only the app counts.

**Why a `cursor` entry counts only with `WS_EX_NOACTIVATE` (B3).** The window under the cursor is often not the window that the child uses: the cursor rests on a background window while the child types or reads in the Foreground Window. To count each such window would charge the wrong category often. A `WS_EX_NOACTIVATE` window does not become the Foreground Window when the child clicks it. So a window with this style under the cursor is a strong sign of use without activation, and normal programs rarely show such a window under a resting cursor. Each other `cursor` exe is only a suspend candidate. False-count risk: an on-screen keyboard, a toast or an overlay of a non-System program with this style under the cursor counts in its category. Task 1.2 logs each `cursor` entry in normal use and is a gate.

**A program that is never suspended still has a limit (S1, N16).** The rule is generic: each counted exe that `SuspensionPlanner` never suspends and that is not in System. Today these are the Session Agent, each exe with the file name `msedgewebview2.exe` (trusted or not, because `ProtectedPrograms` tests the file name), an exe below the install folder, and an exe below the Windows folder after the parent deleted the default System rule. If the category of such an exe is exhausted and the exe is a counted process for more than 60 seconds within 10 minutes of unlocked time, the service applies the fallback of a process that cannot be suspended: the Enforced Workstation Lock and one alert. The 60 seconds let the child use the widget for a short time. A prompt of the parent needs no part of the 60 seconds, because the agent counts nothing during an active prompt (S10).

**Prompts of the parent (S10).** `PromptDialogWindow` is `Topmost` and calls `Activate()`, so it becomes the Foreground Window. Unknown has a default limit, so an exhausted Unknown is a normal state, and an urgent prompt that stays in front for more than 60 seconds would lock the session and use up Unknown time. So while the service has an active prompt for the user of the session, the agent counts nothing and adds nothing to the 60 seconds. This depends on service state (the parent sent the prompt), not on agent data. The service expires a prompt after 5 minutes (`SessionPromptRepository`), so the exemption is bounded and the parent starts each one. A capture request gives no exemption: a capture shows no agent window, and the watch mode lasts 10 minutes, which would give the child a free window.

**Fixed Version and per-user WebView2 (B4 trade-off).** An app can ship its own WebView2 runtime (Fixed Version), or a user without admin rights can get a per-user runtime below `%LOCALAPPDATA%`. Such a `msedgewebview2.exe` is not trusted. In a Study app with the focus in the WebView2 control, it counts by its own App Rule. If no rule covers its folder, it counts in Unknown, and an exhausted Unknown then locks after 60 seconds, because `ProtectedPrograms` never suspends the file name. The parent fixes this with an App Rule for the folder of the app. Task 1.2 lists each `msedgewebview2.exe` path in normal use. GitHub issue #48 changes `ProtectedPrograms` and `SuspensionPlanner` to the same path test, so that an untrusted copy is suspended instead of the lock; that changes the specs `blocked-programs` and `category-limits` outside this change.

The service also starts the agent without the variables `DOTNET_*`, `COMPlus_*` and `CORECLR_*` in its environment (S1). This removes the injection that needs no code; the child can still inject with a tool, which the counting rule above covers for the agent. Code that the child injects into another program of its own that is in System is a residual risk without a bound (S11, see Risks).

**`ForegroundHistory` cap (S5, N14).** `ForegroundHistory` is a set of distinct exe paths per user and day, not a list of samples. The cap rises from 1000 to 60,000 paths. A child would need 60,000 different exe files, each seen in a sample, to fill it; at one sample about every 5 seconds that takes more than 80 hours. The cap stays as a bound on memory. If it is reached anyway, `Record` returns false, and the service applies the Enforced Workstation Lock and sends one alert (fail closed), because an exe that is not in the set is not a suspend candidate.

### D6. When the probe gives no samples (fault)
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for N10).*

A pure class `ProbeOutageMonitor` (Domain) keeps the time of the last sample per session. If an unlocked, connected session of a Restricted User has no sample for 5 minutes, the service writes the event `PROBE_OUTAGE` (new value of the existing `EventType` enum) and sends the parent a Telegram alert. It sends one alert per outage, and at most one per Restricted User and hour (N10), so a repeated fault stays visible. During the gap no category time counts (D5 caps each gap at 12 seconds). The tick starts a new probe if the old one ended. The session Allowance Time counts as before, because it does not depend on samples.

An outage is a fault, not a child action: the child cannot end, suspend or open the probe (D1, D2), and the content of a window cannot end it (D3). A CPU load of the child cannot stop the probe for long (D2, a time-critical thread in the High class; task 1.5 measures it).

Rejected alternatives:
- **Agent samples as a fallback.** The child controls them. This is the hole of #6.
- **A conservative count from the process list** (each category with a running process that was in front today counts during the gap). It punishes the child for a fault of the service, and the child cannot stop a SYSTEM process.
- **The Enforced Workstation Lock during a gap**, as in Data Folder Lockdown. Too strong for a fault of a helper. The alert lets the parent act.

### D6a. Blind samples lock the session (B1)
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for B1 and B2, and for B3, S9, N11, N13, N18, N20).*

A blind sample is a sample that shows that a window or desktop is there, but that the service cannot attribute to an exe: `{ "blind": "desktop" }`, `{ "blind": "window" }`, `{ "blind": "children" }` (more than 8 other processes below `fg`, B3), or an entry that the service cannot read (D3b). The child can cause each of them: a desktop with a security descriptor that denies SYSTEM, a DACL on its game process or thread, decoy child windows. So blind samples must fail closed. An Ended `fg` entry (D3b, S9) also goes into the budget below, but not into the strike.

The first draft locked after 30 seconds of only blind samples, and one readable sample reset the timer. The review showed the attack: 25 seconds on the hidden desktop, 5 seconds back on the default desktop, again and again, and the timer never runs out. So the rule is a rolling budget (`BlindSampleBudget`, a pure class in Domain, part of `ProbeOutageMonitor`):
- The service keeps the sample times `t` of the blind samples of each Restricted User for the last 10 minutes of unlocked, connected time. Locked, disconnected and signed-out time do not count in the 10 minutes, as for Continuous Use. The service ignores blind samples from a session that is locked or not connected (for example after a fast user switch). Readable samples do not remove a blind sample.
- At the 6th blind sample within these 10 minutes, the service applies the Enforced Workstation Lock (`SessionManager.LockSession`), writes the event `PROBE_BLIND` (new value of `EventType`; the first revisions named it `PROBE_DESKTOP_DENIED`, but it also covers unreadable processes, N18) and sends one Telegram alert per Restricted User and day. 6 samples are about 30 seconds of hidden time (D4).
- After the first such lock of a Restricted User on a day, each blind sample on that day locks at once (the strike). A child who tried once does not get a new budget after each unlock. An Ended `fg` entry does not trigger the strike.
- The budget and the strike are in memory. A service restart clears them; only an administrator can restart the service.

With the random sample times of D4, a child who hides for a share of the time produces about that share of blind samples. The alternation of 25 s and 5 s gives about 5 blind samples per 30 seconds and a lock within about 40 seconds. A direct switch to a hidden desktop needs 6 blind samples; with 0.2 s plus a random time with a mean of 4.8 s, at most 10 s, they take about 27 ± 7 seconds, so the spec allows 60 seconds (N13). A child who sees each sample can still show a game for about 0.2 seconds after each one without a blind sample (N11). That is about 4 % of the time, in pieces too short for a game.

This follows the fallback when a process cannot be suspended (`add-app-categories` D6). No program is closed.

**The desktop after the unlock (N20).** The Enforced Workstation Lock disconnects the session (`WTSDisconnectSession`). Which desktop Windows shows when the child signs in again is not measured yet. If Windows shows the hostile desktop of the child again, the strike locks again at the next blind sample, a lock loop for the rest of the day. The child can leave the loop with Ctrl+Alt+Del and Task Manager (Task Manager opens on the default desktop), and the parent has the alert. Task 1.5 records the desktop after the unlock and is a gate: if Windows returns to the hostile desktop, run `/opsx:update` before group 4. The named fallback is: after each unlock, the service ignores blind samples until the first readable sample, for at most 15 seconds, so that the child can switch back. Each such grace needs a full sign-in of the child, so it gives little play.

Rejected alternatives:
- **A timer that a readable sample resets** (the first draft). The alternation attack defeats it.
- **No count while the desktop is not readable.** This is the hole of #6 again, through a different door.
- **A conservative count from the process list.** A game that the child starts on the hidden desktop was never seen in front, so it is not a candidate (D3b of `add-app-categories`), and the count misses it.
- **Give the probe a privilege to bypass the desktop DACL** (`SeTakeOwnershipPrivilege` and a new DACL on the desktop of the child). This changes an object of the child and gives the probe a strong privilege. The lock is simpler and sure.

### D7. The Agent Channel: a pipe that the service owns, with the client end inherited by the agent
*Decided by the orchestrator under the user's delegation (2026-11-26; revised 2026-11-27 for S7, N2, N3, N9, and for S12).*

For each agent start, the service (`AgentChannelHost`, new):
1. Creates a named pipe `\\.\pipe\ParentalControl.Agent.<32 random hex digits>` with `CreateNamedPipeW(PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, maxInstances: 1)` and a security descriptor that gives access to SYSTEM only. The child cannot open the pipe by name and cannot create an instance.
2. Opens the client end itself with `CreateFileW(GENERIC_READ | GENERIC_WRITE, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION)`, not inheritable. `ConnectNamedPipe` completes at once. `FILE_FLAG_OVERLAPPED` is required (S12): the agent uses the handle for asynchronous I/O, and `ThreadPoolBoundHandle.BindHandle` throws `ArgumentException` for a handle that was not opened for overlapped I/O. The flag belongs to the file object, so the inherited duplicate has it too.
3. Starts the agent with the user token as today (`WTSQueryUserToken`, `DuplicateTokenEx`, `CreateEnvironmentBlock` with the user token, minus `DOTNET_*`, `COMPlus_*` and `CORECLR_*` (D5)), but with `bInheritHandles: true`, `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` with an inheritable duplicate of the client handle, under `ProcessLaunchLock` (D2, N6). The command line is `"ParentalControl.Agent.exe" --channel <handle value>`.
4. Closes its copies of the client handle and keeps the launch record: the server end, the agent process handle, the pid, the start time, the session ID and the user SID. The user SID comes from `GetTokenInformation(TokenUser)` of the token that started the agent (N9), not from a user name.

The agent wraps the handle in a `SafePipeHandle` and a `NamedPipeClientStream(PipeDirection.InOut, isAsync: true, handle)`. The overload with `isConnected` is obsolete in .NET 10 (`SYSLIB0063`), and its `isConnected` has no effect. It checks `GetFileType == FILE_TYPE_PIPE` and `GetNamedPipeServerSessionId == 0`, else it exits. This check is only a guard for the user interface (N2): a fake server of the child can only feed false texts to the agent of the child, which the child controls anyway. No security decision depends on it. If the channel breaks, the agent exits. A new service run starts a new agent (D9).

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
- **Get about 25 seconds of hidden play per 10 minutes** if it stays below the budget of D6a (5 blind samples); the 6th locks, and after that lock each blind sample of the day locks. And about 60 seconds per 10 minutes in a program that is never suspended after its category is exhausted (D5), and about 4 % of the time in pieces of 0.2 seconds after each sample (D4). Hidden play in pieces of a few seconds, with a desktop switch each time, is of little use for a game.
- **Drive a System program with a script or with injected code (S11). No bound.** The child can load code into its own `notepad.exe`, `mspaint.exe` or `explorer.exe` (`SetWindowsHookEx`, `CreateRemoteThread`) and draw a game in a System window. Without injection, a script host in System can show a game: a PowerShell script with a web control, an HTA file in `mshta.exe`, or a WebView2 control of the child's own app that a script puts into a System window (a trusted WebView2 in a System host counts nothing, D5). Code in a game can also refuse its own activation (`MA_NOACTIVATE`) so that it is never `fg`; the `cursor` rule still counts it if the window has `WS_EX_NOACTIVATE`, but not if only the window procedure refuses. Each of these counts no time and is never suspended. The special rule for the Session Agent (D5) does not close this class. A parent can reduce it with a file App Rule that moves `powershell.exe`, `mshta.exe`, `wscript.exe` and `cscript.exe` out of System; then the rule of a program that is never suspended locks the session when that category is exhausted. Code signing or a block of script hosts is a later change.
- **Stop its own widget, warnings in the agent, mute and minimize.** This hurts only the child. The service warnings through `WTSSendMessage` and the suspensions do not depend on the agent.
- **Answer its own prompts with any answer.** The answer is the child's anyway.
- **Use a program that is not in a sample**: its window is not the Foreground Window, has no focus, is not a visible child of the Foreground Window, and is not a `WS_EX_NOACTIVATE` window under the cursor (B3). Examples: a video in a background window, a game with `WS_EX_NOACTIVATE` that the child plays with the keyboard, a gamepad or raw input (`RIDEV_INPUTSINK`) while the cursor rests on another window. Such a game is still a suspend candidate when the cursor passes over it. Or leave a game in front without use. Out of scope (idle and background use). No bound for the game case, but it needs a game that takes input without activation.
- **Copy or rename a game exe** so that a file-name rule of another category matches. Out of scope (stage 4: hash or signature rules).
- **Put a game into a folder below `C:\Windows` that a standard user can write** (S2). The exe counts as System and is never suspended. This is a pre-existing hole of the System rule, not of the probe; GitHub issue #45 handles it.
- **Resume a Suspended App with a tool again and again** (#24). Out of scope.

**Other risks:**
- [A bug in the probe is SYSTEM code in the session of the child] → The probe has no window and no input from the child except the window handles, ids and strings that it reads, which it only copies. It opens no process. It has no privileges (D2), the environment of SYSTEM without runtime variables, a SYSTEM-only DACL on its process, thread and default objects, no diagnostics, and files that only administrators can change. Native AOT is a later option.
- [The service opens processes of the child with `SeDebugPrivilege`] → It asks only for `PROCESS_QUERY_LIMITED_INFORMATION` and `THREAD_QUERY_LIMITED_INFORMATION` for a sample, as `ListProcesses` does today; the suspension asks for `PROCESS_SUSPEND_RESUME` as today. The privilege changes which handles the service gets, not what it does with them.
- [Handle inheritance across sessions with `CreateProcessAsUserW` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` does not work as expected] → Task 1.1 checks it on the VM before any other work. If it fails, D7 uses the rejected named-pipe variant with the client check, and D3 uses a named pipe with a SYSTEM-only DACL.
- [A SYSTEM process in the session delays the end of the session or keeps the session alive] → The probe has no window, so it gets no `WM_QUERYENDSESSION`. The service ends it at `WTS_SESSION_LOGOFF`. Task 1.4 checks on the VM that the sign-out completes and that `query session` shows no session left.
- [A legitimate desktop or window gives blind samples, and D6a locks the child] → Task 1.5 checks UAC, Ctrl+Alt+Del, the secure screen saver, a fast user switch, Sysinternals Desktops, and the largest number of foreign children in normal use (`{ "blind": "children" }`). If one of them gives blind samples, run `/opsx:update` before group 4. The strike after the first lock makes a false blind sample costly, so this check is a gate.
- [The rule of D5 counts a legitimate program in a second category] → `children` and `cursor` with `WS_EX_NOACTIVATE` add entries that normal use rarely has (B3), but a program that hosts a window of another process (an OLE server, a plug-in host, a launcher) would also charge that process. Task 1.2 logs `fg`, `focus`, `children` and `cursor` for common programs (Office, a browser, a WebView2 app, a Store app, a game launcher) and for one hour of normal use, with the largest number of foreign children per program. If a common case gives a wrong category, run `/opsx:update` before group 4. The named fallback for `cursor` is a suspend candidate only, without time.
- [An untrusted WebView2 locks a Study app user] → A Fixed Version or per-user `msedgewebview2.exe` counts by its own App Rule, and the file name is never suspended, so an exhausted Unknown can lock (D5, B4 trade-off). Task 1.2 lists each WebView2 path; the parent can add an App Rule; GitHub issue #48 moves the protection to the path test.
- [A suspended exe is not counted, but a sampled exe is a suspend candidate] → A `cursor` exe without `WS_EX_NOACTIVATE` becomes a suspend candidate, so a background program that the cursor passed over is suspended when its category is exhausted. This only suspends, as the hard rule allows, and the child gets it back with time of that category.
- [After a service restart `ForegroundHistory`, the blind budget and the strike are empty] → Unchanged trade-off of `add-app-categories` D3b for the history. With about 5-second samples the set fills fast. Only an administrator can restart the service.
- [The stage 2 changes `usage-breaks` (#31) and `blocked-programs` (#33) are merged and archived] → Continuous Use does not read agent data, and its scenario "Agent samples do not count" stays true (the route answers 404). The Blocked Start reads the exe from the process; it gets the benefit of `SeDebugPrivilege` (D3b).

## Migration Plan

1. Task 1.0 (a chore PR before group 3) moves all projects to .NET 10, CI and the snapshot `test-base`.
2. One release contains the probe, the channel, the new agent and the installer change.
3. The installer stops the service and the agent, removes the `Run` value, installs `{app}\probe`, and starts the service.
4. The service starts a probe and an agent in each existing session of a Restricted User, and an agent in each other session.
5. Rollback: an install of the previous version brings back the HTTP routes, the `Run` value and the old agent. No table and no column change, so the database needs no rollback.

## Open Questions

None that change the specs or the tasks. The VM spike (group 1) measures the constants: the sample interval (0.2 s + exponential with a mean of 4.8 s, at most 10 s), the bound of 8 foreign child processes, the gap cap (12 s), the blind budget (6 in 10 minutes), and the agent allowance (60 s in 10 minutes). It can tune them within the specs.
