using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Service;

/// <summary>
/// Runs one Session Probe in each session of a Restricted User and counts its samples (session-probe-and-agent-channel,
/// design D2 to D6a, D12, D14).
/// <list type="bullet">
/// <item><b>Start</b>: at the sign-in, the unlock and the connect, in the 60-second tick, and after each end that the
/// service did not ask for (after 1, 2 or 5 s, <see cref="ProbeOutageMonitor"/>). Never after <c>WTS_SESSION_LOGOFF</c>
/// of the session and logon, and never in a gone session.</item>
/// <item><b>Read</b>: one blocking reader thread per probe at <c>THREAD_PRIORITY_TIME_CRITICAL</c>
/// (<see cref="ProbeLineReader"/>). The same thread reads the entries of each sample at once
/// (<see cref="ProcessControl.ReadSampleEntry"/>) and then counts it under the lock of the host.</item>
/// <item><b>Count</b>: <see cref="CountedProcesses"/>, <see cref="ForegroundSampleCounter"/> (the gap rule),
/// <see cref="AgentWindowBudget"/>, <see cref="ForegroundHistory"/>, Category Usage through <see cref="UsageLedger"/>,
/// App Usage and <see cref="LiveActivities"/>. No limit reads data of the Session Agent.</item>
/// <item><b>Faults</b>: the watchdog (30 s without an accepted line in an unlocked, connected session), the end wait of
/// 90 s and the classes of an end, the outage after 5 minutes, the gap event, the budget of blind samples and items.</item>
/// <item><b>Probe locks</b>: through <see cref="SessionLocker"/> (the Lock Deferral), the lock before any write, the
/// shadow mode of the setting <c>probe_locks_enforced</c> (<see cref="ProbeLockGate"/>).</item>
/// </list>
/// The probe locks never close or suspend a program. The hard rule covers the programs of the child, not the probe.
/// </summary>
public sealed class SessionProbeHost : BackgroundService
{
#if VM_TEST
    // The test hooks exist only in a build for VM 102 (-p:VmTest=true, compile symbol VM_TEST, design D15). The release
    // build has none of them, so no setting can turn them on.

    /// <summary>Test hook (an administrator setting in the service environment on VM 102): the switches that the
    /// service gives to each probe, for example <c>crash=60</c>. Only the known switches pass.</summary>
    public const string TestProbeArgsEnv = "PARENTAL_CONTROL_TEST_PROBE_ARGS";

    /// <summary>Test hook: "1" stops each probe start (task 4.4, the outage).</summary>
    public const string TestProbeNoStartEnv = "PARENTAL_CONTROL_TEST_PROBE_NO_START";

    /// <summary>Test hook: "1" writes one log line for each sample (the entries, the counted processes, the time).</summary>
    public const string TestProbeTraceEnv = "PARENTAL_CONTROL_TEST_PROBE_TRACE";

    private static readonly Regex TestSwitch = new(@"^((crash|hang|gap|stall)=\d{1,4}|nohung)$", RegexOptions.CultureInvariant);
    private readonly string _testArgs;
    private readonly bool _testNoStart;
    private readonly bool _trace;
#endif

    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EnsureInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettingInterval = TimeSpan.FromSeconds(10);

    private readonly SessionTracker _tracker;
    private readonly CategoryEnforcer _enforcer;
    private readonly SessionLocker _locker;
    private readonly ForegroundHistory _history;
    private readonly ServiceStartup _startup;
    private readonly Serilog.ILogger _logger;
    private readonly StorageHealth _health = StorageHealth.Shared;
    private readonly UsageLedger _ledger = UsageLedger.Shared;
    private readonly TrustedWebView2 _trusted = new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private readonly string _windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly ProtectedPrograms _protected = CategoryRepository.LocalProtectedPrograms();

    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProbeRun> _runs = new();
    private readonly Dictionary<int, long> _logonKeys = new();
    // The Restricted User of each session, for the sign-out and the gone session (they stop the unlocked clock).
    private readonly Dictionary<int, int> _sessionUsers = new();
    private readonly HashSet<(int SessionId, long LogonKey)> _started = [];
    private readonly ProbeOutageMonitor _monitor;
    private readonly ProbeLockGate _lockGate;
    private readonly ForegroundSampleCounter _counter = new();
    // The Agent Window Budget of each Restricted User (by user ID, not by session).
    private readonly Dictionary<int, AgentWindowBudget> _agentBudgets = new();
    private readonly Dictionary<int, NeverSuspendedAllowance> _allowances = new();
    private readonly Dictionary<int, ForegroundProcess?> _lastForeground = new();
    private readonly Dictionary<(int UserId, AppCategory Category), long> _categoryRemainderMs = new();
    private readonly Dictionary<int, long> _appRemainderMs = new();
    private readonly Dictionary<int, HashSet<ProbeLockKind>> _owed = new();
    private readonly Dictionary<int, DateOnly> _fullSetDay = new();
    private readonly ExtraCategoryLog _extraLog = new();
    private readonly DailyOnce _daily = new();
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _stopping;
    private readonly ProbeSleepFlag _sleep = new();
    private TimeSpan _lastEnsure = TimeSpan.FromDays(-1);
    private TimeSpan _lastSetting = TimeSpan.FromDays(-1);
    private readonly Dictionary<int, (IReadOnlyList<string> Exes, TimeSpan At)> _lastOver = new();
    private readonly Dictionary<int, (DateOnly Day, Dictionary<AppCategory, int> Usage)> _lastUsage = new();

    public SessionProbeHost(SessionTracker tracker, CategoryEnforcer enforcer, SessionLocker locker, ForegroundHistory history,
        ServiceStartup startup, Serilog.ILogger logger)
    {
        _tracker = tracker;
        _enforcer = enforcer;
        _locker = locker;
        _history = history;
        _startup = startup;
        _logger = logger;
        _monitor = new ProbeOutageMonitor(probeLocksEnforced: false);
        _lockGate = new ProbeLockGate(enforced: false);
#if VM_TEST
        _testArgs = string.Join(' ', (Environment.GetEnvironmentVariable(TestProbeArgsEnv) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => TestSwitch.IsMatch(a)));
        _testNoStart = Environment.GetEnvironmentVariable(TestProbeNoStartEnv) == "1";
        _trace = Environment.GetEnvironmentVariable(TestProbeTraceEnv) == "1";
#endif
        _locker.OwedLockDue += OnOwedLockDue;
    }

    private sealed class ProbeRun
    {
        public required int SessionId;
        public required int UserId;
        public required string UserSid;
        public required string Username;
        public required long LogonKey;
        public required LaunchedProcess Process;
        public required SafeFileHandle Pipe;
        public readonly ProbeLineReader Reader = new();
        public ProcessExitWait? ExitEvent;
        public RegisteredWaitHandle? Wait;
        public TimeSpan LastLine;
        public bool AskedEnd;
        public bool WatchdogKill;
        public bool LoggedTerminateFailure;
        public bool Ended;
        public bool LoggedProblem;
        public bool LoggedLate;
        public bool LoggedNotAttributed;
        public bool LoggedMissingDuration;
    }

    // A wait on the process handle of the launch. The base constructor of WaitHandle creates no kernel object (unlike
    // ManualResetEvent), and the handle stays owned by LaunchedProcess.
    private sealed class ProcessExitWait : WaitHandle
    {
        public ProcessExitWait(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    // ------------------------------------------------------------------ lifetime

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _startup.WaitReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The leftovers of an earlier service run go before _ready: after _ready, a sign-in event can start a probe of this
        // run, which is also a SYSTEM process at the probe path, and EndLeftovers would end it.
        EndLeftovers();

        var enforced = Safe(SettingsRepository.IsProbeLocksEnforced, false);
        lock (_gate)
        {
            _monitor.SetProbeLocksEnforced(enforced);
            _lockGate.SetEnforced(enforced);
            _ready = true;
        }
        _logger.Information("Session Probe host started (probe locks {State}){Test}", enforced ? "on" : "off (shadow mode)", TestHooksText());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in the Session Probe tick");
            }
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopAll("service stop");
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _locker.OwedLockDue -= OnOwedLockDue;
        base.Dispose();
    }

    /// <summary>The service stops or the system shuts down: end each probe; each pending end is a Session-End End.</summary>
    public void StopAll(string reason)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _monitor.ServiceStopping();
            _logger.Information("Session Probe host: {Reason}, ending {Count} probe(s)", reason, _runs.Count);
            foreach (var run in _runs.Values.ToList()) EndRun(run, reason);
        }
    }

    // At the start: end each probe of an earlier service run (a SYSTEM process with the probe path, N5).
    private void EndLeftovers()
    {
        var exe = ProbeExecutablePath();
        if (exe is null) return;
        try
        {
            var (system, others) = SessionProbePlatform.FindLeftovers(exe);
            foreach (var (pid, start) in system)
            {
                var result = ProcessControl.Terminate(pid, start, out var detail);
                _logger.Information("Ended a leftover Session Probe (pid {Pid}): {Result} {Detail}", pid, result, detail);
            }
            foreach (var (pid, sid) in others)
                _logger.Warning("A process with the probe path runs as {Sid} (pid {Pid}); it is not ended", sid ?? "an unknown user", pid);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not check for leftover Session Probes");
        }
    }

    // ------------------------------------------------------------------ session and power events

    /// <summary><c>WTS_SESSION_LOGON</c>: a new logon clears the sign-out state; start the probe.</summary>
    public void OnLogon(int sessionId) => Guard("logon", () =>
    {
        lock (_gate)
        {
            if (!_ready) return;
            var key = SessionProbePlatform.QueryLogonTime(sessionId) ?? 0;
            _logonKeys[sessionId] = key;
            _monitor.SignedIn(sessionId, key);
            Observe(sessionId, unlocked: true);
            StartProbe(sessionId, "sign-in");
        }
    });

    /// <summary><c>WTS_SESSION_LOGOFF</c>: each pending end of the session is a Session-End End; end the probe; no new
    /// probe in this session and logon.</summary>
    public void OnLogoff(int sessionId) => Guard("logoff", () =>
    {
        lock (_gate)
        {
            if (!_ready) return;
            var key = _runs.TryGetValue(sessionId, out var run) ? run.LogonKey : _logonKeys.GetValueOrDefault(sessionId);
            _monitor.SignedOut(UserOf(sessionId), sessionId, key, SessionProbePlatform.UnbiasedNow());
            _sessionUsers.Remove(sessionId);
            _logger.Information("Session Probe: sign-out of session {SessionId} (logon {Key})", sessionId, key);
            if (run is not null) EndRun(run, "sign-out");
            _counter.SessionEnded(sessionId);
            _lastForeground.Remove(sessionId);
            _owed.Remove(sessionId);
        }
    });

    /// <summary>A lock or a disconnect: the watchdog and the unlocked clock stop.</summary>
    public void OnLock(int sessionId) => Guard("lock", () =>
    {
        lock (_gate)
        {
            if (!_ready) return;
            Observe(sessionId, unlocked: false);
        }
    });

    /// <summary>An unlock or a connect: the watchdog starts again, the next sample adds zero, and a probe starts if none
    /// runs.</summary>
    public void OnUnlock(int sessionId, string what) => Guard(what, () =>
    {
        lock (_gate)
        {
            if (!_ready) return;
            var now = SessionProbePlatform.UnbiasedNow();
            if (_runs.TryGetValue(sessionId, out var run)) run.LastLine = now;
            _counter.PlannedStart(sessionId);
            Observe(sessionId, unlocked: !IsLocked(sessionId));
            StartProbe(sessionId, what);
        }
    });

    /// <summary><c>PBT_APMSUSPEND</c>: the watchdog and the outage timer stop. The tick clears the flag if no resume
    /// event comes within 30 s of unbiased time (<see cref="ProbeSleepFlag"/>).</summary>
    public void OnSuspend() => Guard("suspend", () =>
    {
        lock (_gate)
        {
            _sleep.Suspend(SessionProbePlatform.UnbiasedNow());
            _logger.Information("Session Probe: system suspend, the watchdog and the outage timer stop");
        }
    });

    /// <summary><c>PBT_APMRESUMEAUTOMATIC</c> or <c>PBT_APMRESUMESUSPEND</c>: like an unlock (S20).</summary>
    public void OnResume() => Guard("resume", () =>
    {
        lock (_gate)
        {
            var wasSleeping = _sleep.Resume();
            Resumed(SessionProbePlatform.UnbiasedNow());
            if (wasSleeping)
                _logger.Information("Session Probe: system resume, the watchdog and the outage timer start again");
        }
    });

    // Under _gate: the resume event, or the stale clear of the sleep flag in the tick.
    private void Resumed(TimeSpan now)
    {
        foreach (var run in _runs.Values) run.LastLine = now;
        _monitor.Resumed(now);
        _counter.Resumed();
    }

    // ------------------------------------------------------------------ start and end of a probe

    private void StartProbe(int sessionId, string why)
    {
        if (!_ready || _stopping) return;
        if (_runs.TryGetValue(sessionId, out var existing) && !existing.Ended) return;
        if (!_tracker.ActiveSessions.TryGetValue(sessionId, out var session)) return;
        var user = UserRepository.GetBySid(session.UserSid);
        if (user is null || !user.IsRestricted) return;
        _sessionUsers[sessionId] = user.Id;

        if (IsGone(sessionId)) return;
        var key = SessionProbePlatform.QueryLogonTime(sessionId) ?? _logonKeys.GetValueOrDefault(sessionId);
        _logonKeys[sessionId] = key;
        if (!_monitor.MayStart(sessionId, key))
        {
            LogOnce($"signedout:{sessionId}:{key}", () =>
                _logger.Information("Session Probe: no start in session {SessionId} after its sign-out ({Why})", sessionId, why));
            return;
        }

        var exe = ProbeExecutablePath();
        if (exe is null)
        {
            LogOnce("noexe", () => _logger.Error("Session Probe: ParentalControl.Probe.exe not found; no category time counts"));
            return;
        }
#if VM_TEST
        if (_testNoStart)
        {
            LogOnce($"nostart:{sessionId}", () => _logger.Warning("Session Probe test hook: no start in session {SessionId}", sessionId));
            return;
        }
#endif

        SafeFileHandle? read = null;
        try
        {
            var (pipeRead, pipeWrite) = SessionProbePlatform.CreatePipe();
            read = pipeRead;
            LaunchedProcess launched;
            using (pipeWrite)
            {
                var args = "--pipe " + ProcessLauncherPlaceholder;
#if VM_TEST
                if (_testArgs.Length > 0) args += " " + _testArgs;
#endif
                launched = SessionSystemProcess.Start(sessionId, exe, args, pipeWrite.DangerousGetHandle(), envError =>
                {
                    if (_daily.Take("probe-environment", ControlTime.Current.Today))
                        _logger.Warning("Session Probe: {Error}; the probe of session {SessionId} gets the environment of the service",
                            envError, sessionId);
                });
            }
            // The service copy of the write end is closed now: the read breaks when the probe ends.

            var planned = _started.Add((sessionId, key));
            if (planned) _counter.PlannedStart(sessionId);
            var run = new ProbeRun
            {
                SessionId = sessionId,
                UserId = user.Id,
                UserSid = user.Sid,
                Username = user.Username,
                LogonKey = key,
                Process = launched,
                Pipe = pipeRead,
                LastLine = SessionProbePlatform.UnbiasedNow(),
            };
            read = null;
            _runs[sessionId] = run;

            var reader = new Thread(() => ReadLoop(run)) { IsBackground = true, Name = $"probe-reader-{sessionId}" };
            reader.Start();
            run.ExitEvent = new ProcessExitWait(launched.Handle);
            run.Wait = ThreadPool.RegisterWaitForSingleObject(run.ExitEvent, (_, _) => OnProbeExited(run), null, Timeout.Infinite, executeOnlyOnce: true);

            _logger.Information("Session Probe started in session {SessionId} for {Username} (pid {Pid}, {Why}{Planned})",
                sessionId, user.Username, launched.Pid, why, planned ? ", planned start" : ", restart");
        }
        catch (Exception ex)
        {
            read?.Dispose();
            LogOnce($"startfail:{sessionId}:{ex.Message}", () => _logger.Error(ex, "Session Probe: start in session {SessionId} failed", sessionId));
        }
    }

    private const string ProcessLauncherPlaceholder = "{handle}";

    // The service ends the probe (sign-out, service stop): not classified.
    private void EndRun(ProbeRun run, string reason)
    {
        if (run.Ended) return;
        run.AskedEnd = true;
        Terminate(run, reason);
    }

    // True if TerminateProcess succeeded. A failure is logged once per probe run.
    private bool Terminate(ProbeRun run, string reason)
    {
        try
        {
            if (run.Process.Handle.IsClosed || run.Process.Handle.IsInvalid) return false;
            if (!NativeTerminate(run, out var error))
            {
                if (!run.LoggedTerminateFailure)
                {
                    run.LoggedTerminateFailure = true;
                    _logger.Warning("Could not end the Session Probe of session {SessionId} (pid {Pid}): TerminateProcess error {Error} ({Reason})",
                        run.SessionId, run.Process.Pid, error, reason);
                }
                return false;
            }
            _logger.Information("Session Probe of session {SessionId} (pid {Pid}) ended by the service: {Reason}", run.SessionId, run.Process.Pid, reason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not end the Session Probe of session {SessionId}", run.SessionId);
            return false;
        }
    }

    // The kept handle of the launch: no pid reuse is possible. Exit code 0xC0DE0001 marks an end by the service.
    private static bool NativeTerminate(ProbeRun run, out int error)
    {
        var ok = SessionProbePlatform.Terminate(run.Process.Handle, 0xC0DE0001);
        error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return ok;
    }

    private void OnProbeExited(ProbeRun run) => Guard("probe end", () =>
    {
        var exitCode = SessionProbePlatform.ExitCode(run.Process.Handle); // only for the log

        lock (_gate)
        {
            run.Ended = true;
            run.Wait?.Unregister(null);
            run.ExitEvent?.Dispose();
            run.Process.Dispose();
            if (_runs.TryGetValue(run.SessionId, out var current) && current == run) _runs.Remove(run.SessionId);

            var cause = run.AskedEnd ? ProbeEndCause.Service : run.WatchdogKill ? ProbeEndCause.Watchdog : ProbeEndCause.Other;
            if (cause == ProbeEndCause.Service || _stopping)
            {
                _logger.Information("Session Probe of session {SessionId} (pid {Pid}) ended (asked by the service), exit code {Code}",
                    run.SessionId, run.Process.Pid, Hex(exitCode));
                return;
            }

            var now = SessionProbePlatform.UnbiasedNow();
            var activeAndUnlocked = SessionProbePlatform.IsActive(run.SessionId) && !IsLocked(run.SessionId);
            var outcome = _monitor.ProbeEnded(run.UserId, run.SessionId, run.LogonKey, now, cause, activeAndUnlocked);
            _counter.EndNotAsked(run.SessionId);
            var what = outcome.Benign ? "benign end (resume window)"
                : outcome.Restart ? $"end wait of {ProbeOutageMonitor.EndWait.TotalSeconds:0} s, restart in {outcome.Delay.TotalSeconds:0} s"
                : "session signed out: no restart";
            _logger.Warning("Session Probe of session {SessionId} (pid {Pid}) ended without a request ({Cause}), exit code {Code}: {What}",
                run.SessionId, run.Process.Pid, cause, Hex(exitCode), what);

            if (outcome.Restart)
            {
                var sessionId = run.SessionId;
                if (outcome.Delay <= TimeSpan.Zero)
                    StartProbe(sessionId, "restart");
                else
                    _ = Task.Delay(outcome.Delay).ContinueWith(_ => Guard("restart", () =>
                    {
                        lock (_gate) StartProbe(sessionId, "restart");
                    }));
            }
        }
    });

    private static string Hex(int? code) => code is { } c ? $"0x{c:X8}" : "unknown";

    // ------------------------------------------------------------------ the reader thread

    private void ReadLoop(ProbeRun run)
    {
        SessionProbePlatform.SetCurrentThreadTimeCritical();
        var buffer = new byte[ProbeLineReader.MaxLineBytes];
        var output = new List<object>();
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = SessionProbePlatform.Read(run.Pipe, buffer);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Session Probe pipe of session {SessionId}: read failed", run.SessionId);
                    break;
                }
                if (read == 0) break;

                output.Clear();
                run.Reader.Feed(buffer.AsSpan(0, read), Environment.TickCount64, output);
                if (!run.LoggedProblem && run.Reader.FirstProblem is { } problem && problem != "sample line without dur")
                {
                    run.LoggedProblem = true;
                    _logger.Warning("Session Probe of session {SessionId}: {Problem} (dropped lines {Lines}, fields {Fields})",
                        run.SessionId, problem, run.Reader.DroppedLines, run.Reader.DroppedFields);
                }
                if (!run.LoggedMissingDuration && run.Reader.MissingDurations > 0)
                {
                    run.LoggedMissingDuration = true;
                    _logger.Warning("Session Probe of session {SessionId}: a sample line without dur (counts as 0)", run.SessionId);
                }
                foreach (var item in output)
                {
                    switch (item)
                    {
                        case ProbeLogLine log:
                            _logger.Information("Session Probe of session {SessionId}: {Log}", run.SessionId, log.Text);
                            break;
                        case ProbeSample sample:
                            try
                            {
                                HandleSample(run, sample);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "Session Probe sample of session {SessionId} failed", run.SessionId);
                            }
                            break;
                    }
                }
            }
        }
        finally
        {
            run.Pipe.Dispose();
        }
    }

    // ------------------------------------------------------------------ one sample

    private void HandleSample(ProbeRun run, ProbeSample sample)
    {
        var receiptTick = Environment.TickCount64;
        var receiptUnbiased = SessionProbePlatform.UnbiasedNow();
        var sampleUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, receiptTick - sample.T));

        // Read the entries at once, before the lock, so the windows have little time to go away (D3b, S9).
        var entries = new List<SampleEntry>();
        SampleEntry? hung = null;
        if (sample.Kind == ProbeSampleKind.Window && sample.Foreground is { } fg)
        {
            entries.Add(Read(run, ProbeRole.Foreground, fg, sampleUtc));
            if (sample.Focus is { } focus) entries.Add(Read(run, ProbeRole.Focus, focus, sampleUtc));
            foreach (var child in sample.ChildEntries) entries.Add(Read(run, ProbeRole.Child, child, sampleUtc));
            if (sample.Cursor is { } cursor)
                foreach (var entry in cursor.Entries) entries.Add(Read(run, ProbeRole.Cursor, entry, sampleUtc));
            if (sample.Hung is { } hungWindow) hung = Read(run, ProbeRole.Foreground, hungWindow, sampleUtc);
        }
        var reading = new SampleReading(entries, sample.Cursor?.NoActivate ?? false, hung, ProbeBlind: sample.Kind == ProbeSampleKind.Blind);

        lock (_gate)
        {
            if (!_ready || _stopping || run.Ended) return;
            run.LastLine = receiptUnbiased;
            Count(run, sample, reading, receiptTick, receiptUnbiased);
        }
    }

    private SampleEntry Read(ProbeRun run, ProbeRole role, ProbeWindow window, DateTime sampleUtc)
    {
        var read = ProcessControl.ReadSampleEntry(window.Pid, window.Tid, run.SessionId, sampleUtc);
        if (read.StartAfterSample && !run.LoggedLate)
        {
            run.LoggedLate = true;
            _logger.Warning("Session Probe of session {SessionId}: a start time after the sample time (pid {Pid}); a clock step?", run.SessionId, window.Pid);
        }
        if (read.Outcome != EntryOutcome.Read)
            return new SampleEntry(role, read.Outcome);
        WebView2Creator? creator = null;
        if (_trusted.IsTrusted(read.ExePath))
            creator = ProcessControl.ResolveWebView2Creator(window.Pid, _trusted.IsTrusted);
        return new SampleEntry(role, EntryOutcome.Read, read.Process, read.ExePath, creator);
    }

    // Under _gate.
    private void Count(ProbeRun run, ProbeSample sample, SampleReading reading, long receiptTick, TimeSpan unbiased)
    {
        var sessionId = run.SessionId;
        var userId = run.UserId;
        var today = ControlTime.Current.Today;
        var config = CategoryConfigCache.Current;
        var resolver = config.Resolver;
        var locked = IsLocked(sessionId);

        // The agent by its exe path (D5 rule 5): also as the creator of a trusted WebView2 when no window of the agent is
        // in the sample. Task 5.1 may add the launch record of the agent (AgentLaunchRegistry), but as an OR with this
        // path test: a test of the launch record only brings the moved WebView2 of the agent back to its App Rule.
        var agentPath = WindowsPath.Canonicalize(SessionTracker.GetAgentExecutablePath());
        bool IsAgent(ProcessIdentity _, string exePath) =>
            agentPath is not null && string.Equals(WindowsPath.Canonicalize(exePath), agentPath, StringComparison.OrdinalIgnoreCase);
        var suspended = Safe(() => SuspensionRepository.GetForUser(userId), []).Select(r => new ProcessIdentity(r.Pid, r.StartTimeUtc)).ToHashSet();
        var promptActive = Safe(() => SessionPromptRepository.GetActivePrompt(run.Username) is not null, false);

        var context = new SelectionContext(
            path => resolver.Resolve(path).Category,
            _windowsFolder,
            IsAgent,
            promptActive,
            suspended.Contains,
            p => ProcessControl.QueryPathAndStart(p.Pid).StartUtc == p.StartUtc,
            _lastForeground.GetValueOrDefault(sessionId));
        var selection = CountedProcesses.Select(reading, context, _trusted);
        _lastForeground[sessionId] = selection.NextLastForeground;

        var credit = _counter.Accept(sessionId, sample.T, sample.Duration, receiptTick, unbiased, selection.Blind, selection.Counted);
#if VM_TEST
        if (_trace) Trace(run, sample, reading, selection, credit, receiptTick);
#endif
        if (reading.Hung is { } hungEntry && LogOnce($"ghost:{sessionId}:{hungEntry.Process.Pid}"))
            _logger.Information("Session Probe of session {SessionId}: ghost window in front, hung window of {Exe} (pid {Pid}, {Outcome})",
                sessionId, hungEntry.ExePath ?? "-", hungEntry.Process.Pid, hungEntry.Outcome);

        // The locked state comes from Windows (D4): a locked session counts nothing and gives no item.
        if (locked || _sleep.IsSet)
        {
            _monitor.Observe(userId, unbiased, activeAndUnlocked: false);
            return;
        }

        if (!run.LoggedNotAttributed && selection.NotAttributed.Count > 0)
        {
            run.LoggedNotAttributed = true;
            _logger.Information("Session Probe of session {SessionId}: a trusted WebView2 without an attributed creator ({Exe}) counts nothing",
                sessionId, selection.NotAttributed[0]);
        }

        // The set of Foreground Window exes of the day (the suspend candidates). In memory only.
        var fullSet = false;
        foreach (var exe in selection.ExesToRecord)
        {
            var canonical = resolver.Resolve(exe).CanonicalPath ?? exe;
            if (!_history.Record(userId, today, canonical)) fullSet = true;
        }

        // Blind samples and budget items (D6a), with the session state queried at the item (N23).
        var item = selection.EndedItem || credit.GapItem;
        var activeAndUnlocked = !(selection.Blind || item) || SessionProbePlatform.IsActive(sessionId) && !IsLocked(sessionId);
        var decision = _monitor.Sample(userId, unbiased, activeAndUnlocked, today, selection.Blind, item);

        // D14: each probe lock comes before any write of this sample (events, log lines, alerts, Category Usage, App
        // Usage). The inputs of the locks are in memory or read through Safe, so a failed database read never stops a lock.
        if (decision.Action != ProbeLockAction.None)
            ApplyBudgetDecision(sessionId, userId, run.UserSid, run.Username, decision, today);

        // The Agent Window Budget (D5, #54): only the entry of the agent takes the exempt part of the interval. One budget
        // per Restricted User, so a new sign-in does not give a new budget.
        var agentCounted = selection.Counted.Any(c => c.IsAgent);
        var intervalSeconds = (int)Math.Round(credit.Time.TotalSeconds);
        if (!_agentBudgets.TryGetValue(userId, out var budget)) _agentBudgets[userId] = budget = new AgentWindowBudget();
        var exemptSeconds = budget.Sample(TimeSpan.FromMilliseconds(sample.T), intervalSeconds, agentCounted, unlocked: true);

        var dayLimits = config.Calculator.Evaluate(today, CategoryUsageForLocks(userId, today),
            Safe(() => UsageRepository.GetUsage(userId, today)?.BonusMinutes ?? 0, 0));
        bool IsExhausted(AppCategory category) => dayLimits.CategoryLimit(category)?.IsReached ?? false;

        if (!_allowances.TryGetValue(userId, out var allowance))
            _allowances[userId] = allowance = new NeverSuspendedAllowance(_protected.IsProtected);
        var over = allowance.Sample(_monitor.UnlockedTime(userId, unbiased), unlocked: true, selection, credit.Time, IsExhausted,
            TimeSpan.FromSeconds(exemptSeconds));
        if (over.Count > 0) _lastOver[userId] = (over, unbiased);
        foreach (var exe in over)
            ApplyOtherProbeLock(sessionId, userId, run.UserSid, run.Username, ProbeLockKind.NeverSuspended, exe, today, unbiased);

        if (fullSet)
        {
            _fullSetDay[userId] = today;
            ApplyOtherProbeLock(sessionId, userId, run.UserSid, run.Username, ProbeLockKind.FullSet, null, today, unbiased);
        }

        // The writes, after the locks.
        if (selection.Blind || item)
            _logger.Information("Session Probe of session {SessionId}: {Kind} ({Detail}){Ignored}", sessionId,
                selection.Blind ? "blind sample" : "budget item", Describe(sample, selection, credit),
                activeAndUnlocked ? "" : " ignored: the session is not active and unlocked");

        if (credit.LongGap) ReportGap(run, sample, credit, today);

        var tickNeeded = false;
        foreach (var (category, time) in ProbeCategoryTime.PerCategory(selection, credit.Time, TimeSpan.FromSeconds(exemptSeconds)))
        {
            var ms = (long)time.TotalMilliseconds;
            var key = (userId, category);
            var total = _categoryRemainderMs.GetValueOrDefault(key) + ms;
            var seconds = (int)(total / 1000);
            _categoryRemainderMs[key] = total % 1000;
            if (seconds <= 0) continue;
            var minutes = CategoryUsageRepository.CollectMinutes(userId, today, category, seconds);
            if (minutes > 0)
            {
                _ledger.AddCategory(userId, today, category, minutes);
                tickNeeded = true;
            }
        }

        // App Usage (the Activity Timeline) and the live activity: the Foreground Window, else the first counted process.
        var shown = selection.Counted.FirstOrDefault(c => c.Role == ProbeRole.Foreground) ?? selection.Counted.FirstOrDefault();
        var fgRead = reading.Entries.FirstOrDefault(e => e.Role == ProbeRole.Foreground && e.Outcome == EntryOutcome.Read);
        var title = sample.Title ?? "";
        if (shown is not null && credit.Time > TimeSpan.Zero)
        {
            var total = _appRemainderMs.GetValueOrDefault(userId) + (long)credit.Time.TotalMilliseconds;
            var seconds = (int)(total / 1000);
            _appRemainderMs[userId] = total % 1000;
            var name = WindowsPath.GetFileName(shown.ExePath);
            if (seconds > 0)
                _health.TryWrite(() => AppUsageRepository.AddSeconds(userId, today, name, title, seconds), "app usage", _logger);
        }
        var liveExe = shown?.ExePath ?? fgRead?.ExePath;
        if (liveExe is not null)
            LiveActivities.Shared[run.Username] = (WindowsPath.GetFileName(liveExe), title, DateTime.Now);

        foreach (var extra in _extraLog.Due(selection.Counted, unbiased))
            _logger.Information("Session Probe of session {SessionId}: {Role} entry {Exe} adds {Category}, which the Foreground Window does not add",
                sessionId, extra.Role, extra.ExePath, extra.Category);

        if (tickNeeded) _enforcer.RequestTick();
    }

    // The Category Usage of the day for the probe locks (D14). If the read fails, the last good read of the same user and
    // day is used, so an exhausted category stays exhausted during a database failure and the lock of a program that is
    // never suspended still applies. Without a good read of the day, nothing is exhausted (the usage is unknown).
    private Dictionary<AppCategory, int> CategoryUsageForLocks(int userId, DateOnly today)
    {
        try
        {
            var usage = _ledger.CategoryUsage(userId, today);
            _lastUsage[userId] = (today, usage);
            return usage;
        }
        catch (Exception ex)
        {
            LogOnce("usage-read:" + ex.GetType().Name, () =>
                _logger.Warning(ex, "Session Probe host: the Category Usage read failed; the probe locks use the last good read"));
            return _lastUsage.TryGetValue(userId, out var last) && last.Day == today ? last.Usage : new Dictionary<AppCategory, int>();
        }
    }

    private static string Describe(ProbeSample sample, SampleSelection selection, SampleCredit credit)
    {
        var parts = new List<string>();
        if (sample.Kind == ProbeSampleKind.Blind) parts.Add($"blind {sample.BlindReason}");
        else if (selection.Blind) parts.Add("an entry is unreadable");
        if (selection.EndedItem) parts.Add("an entry ended");
        if (credit.GapItem) parts.Add($"gap {credit.Gap.TotalSeconds:0.0} s, dur {sample.Duration.TotalSeconds:0.0} s");
        if (sample.Desktop is { Length: > 0 } desktop) parts.Add($"desktop {desktop}");
        return string.Join(", ", parts);
    }

    private void ReportGap(ProbeRun run, ProbeSample sample, SampleCredit credit, DateOnly today)
    {
        var detail = $"Gap {credit.Gap.TotalSeconds:0.0} s, dur {sample.Duration.TotalSeconds:0.0} s, counted {credit.Time.TotalSeconds:0.0} s" +
                     (credit.GapItem ? " (budget item)" : " (in full)");
        _logger.Warning("Session Probe of session {SessionId}: {Detail}", run.SessionId, detail);
        _health.TryWrite(() => EventRepository.LogEvent(run.UserSid, EventType.PROBE_GAP, detail), "probe gap event", _logger);
        if (_daily.Take($"gap:{run.UserId}", today))
            Alert($"Parental Control: the Session Probe of {run.Username} on {Environment.MachineName} " +
                                      $"had a long gap between two samples ({detail}). The time counts under the gap rule; no lock.");
    }

    // ------------------------------------------------------------------ the tick

    private void Tick()
    {
        lock (_gate)
        {
            if (!_ready || _stopping) return;
            var now = SessionProbePlatform.UnbiasedNow();
            var today = ControlTime.Current.Today;

            if (now - _lastSetting >= SettingInterval)
            {
                _lastSetting = now;
                var enforced = Safe(SettingsRepository.IsProbeLocksEnforced, _lockGate.Enforced);
                if (_lockGate.SetEnforced(enforced))
                {
                    _monitor.SetProbeLocksEnforced(enforced);
                    _owed.Clear();
                    _logger.Warning("Probe locks switched {State}", enforced ? "on: the budget and the strike start empty" : "off (shadow mode)");
                }
            }

            // The unlocked clock of each Restricted User.
            foreach (var (sessionId, session) in _tracker.ActiveSessions)
            {
                // A signed-out session that the tracker still lists must not start the unlocked clock again (S5).
                if (!_monitor.MayStart(sessionId, _logonKeys.GetValueOrDefault(sessionId))) continue;
                var user = Safe(() => UserRepository.GetBySid(session.UserSid), null);
                if (user is { IsRestricted: true }) _monitor.Observe(user.Id, now, !session.IsLocked && !_sleep.IsSet);
            }

            if (_sleep.ClearIfStale(now))
            {
                _logger.Warning("Session Probe: no resume event within {Seconds} s of unbiased time after the suspend event; " +
                                "the watchdog and the outage timer start again", ProbeSleepFlag.StaleAfter.TotalSeconds);
                Resumed(now);
            }

            if (!_sleep.IsSet)
            {
                // The watchdog (S15): no accepted line for 30 s in an unlocked, connected session.
                foreach (var run in _runs.Values.ToList())
                {
                    if (run.Ended || run.AskedEnd || run.WatchdogKill || IsLocked(run.SessionId)) continue;
                    if (now - run.LastLine < WatchdogTimeout) continue;
                    run.WatchdogKill = true;
                    // If TerminateProcess fails, the next tick tries again (the failure is logged once per run). The
                    // exit wait classifies the end only when the probe ends.
                    if (!Terminate(run, $"watchdog: no line for {(now - run.LastLine).TotalSeconds:0} s"))
                        run.WatchdogKill = false;
                }

                foreach (var outage in _monitor.CheckOutages(now))
                    ReportOutage(outage, today);
            }

            foreach (var end in _monitor.Classify(now, IsGone, today))
                ReportUnexpectedEnd(end, today);

            if (now - _lastEnsure >= EnsureInterval)
            {
                _lastEnsure = now;
                foreach (var sessionId in _tracker.ActiveSessions.Keys)
                    StartProbe(sessionId, "tick");
                foreach (var sessionId in _logonKeys.Keys.ToList())
                {
                    if (_runs.ContainsKey(sessionId) || !IsGone(sessionId)) continue;
                    _monitor.SessionGone(UserOf(sessionId), sessionId, now);
                    _sessionUsers.Remove(sessionId);
                    _counter.SessionEnded(sessionId);
                    _lastForeground.Remove(sessionId);
                }
            }
        }
    }

    private void ReportOutage(ProbeOutage outage, DateOnly today)
    {
        var user = Safe(() => UserRepository.GetById(outage.UserId), null);
        var name = user?.Username ?? $"user {outage.UserId}";
        _logger.Error("Session Probe outage: no sample of {Username} for {Minutes} minutes; no category time counts", name,
            ProbeOutageMonitor.OutageAfter.TotalMinutes);
        if (user is not null)
            _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.PROBE_OUTAGE, "No Session Probe sample for 5 minutes"),
                "probe outage event", _logger);
        if (outage.Alert)
            Alert($"Parental Control: the Session Probe of {name} on {Environment.MachineName} gave no sample " +
                                      "for 5 minutes. No category time counts during the outage; the daily time still counts.");
    }

    private void ReportUnexpectedEnd(UnexpectedProbeEnd end, DateOnly today)
    {
        var user = Safe(() => UserRepository.GetById(end.UserId), null);
        var name = user?.Username ?? $"user {end.UserId}";
        _logger.Warning("Session Probe of session {SessionId} ({Username}): unexpected end after the end wait{Item}",
            end.SessionId, name, end.BudgetItem ? ", a budget item" : "");

        // The lock first, then the writes (D14). A failed user read does not stop the lock: the SID comes from the
        // session tracker then (the event write needs it only).
        if (end.Lock.Action != ProbeLockAction.None)
        {
            var sid = user?.Sid ?? (_tracker.ActiveSessions.TryGetValue(end.SessionId, out var session) ? session.UserSid : "");
            ApplyBudgetDecision(end.SessionId, end.UserId, sid, name, end.Lock, today);
        }

        if (user is not null)
            _health.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.PROBE_RESTART, $"Unexpected end of the Session Probe in session {end.SessionId}"),
                "probe restart event", _logger);
        if (end.Alert)
            Alert($"Parental Control: the Session Probe of {name} on {Environment.MachineName} ended " +
                                      $"{ProbeOutageMonitor.RestartAlertAt} times within one hour. The service starts it again each time.");
    }

    // ------------------------------------------------------------------ probe locks

    private void ApplyBudgetDecision(int sessionId, int userId, string userSid, string username, ProbeLockDecision decision, DateOnly today)
    {
        var kind = decision.Reason == ProbeLockReason.Strike ? ProbeLockKind.Strike : ProbeLockKind.Budget;
        var reason = kind == ProbeLockKind.Strike
            ? "blind sample after the first probe lock of the day"
            : $"{BlindSampleBudget.LockAtItems} blind samples or budget items within 10 minutes";

        if (decision.Action == ProbeLockAction.WouldLock)
        {
            ReportWouldLock(userId, userSid, username, kind, null, reason, today);
            return;
        }

        switch (LockSession(sessionId, $"Session Probe: {reason}"))
        {
            case LockOutcome.Owed:
                Owe(sessionId, kind);
                return;
            case LockOutcome.AlreadyLocked:
                // Windows locked the session already. This call applied no lock, so it gives no strike, no event and
                // no alert.
                _logger.Information("Probe lock of session {SessionId} ({Username}) not needed, the session is locked: {Reason}",
                    sessionId, username, reason);
                return;
        }
        _monitor.LockApplied(userId, today);
        _logger.Warning("Probe lock of session {SessionId} ({Username}): {Reason}", sessionId, username, reason);
        _health.TryWrite(() => EventRepository.LogEvent(userSid, EventType.PROBE_BLIND, $"Session locked: {reason}"), "probe blind event", _logger);
        if (_daily.Take($"blind:{userId}", today))
            Alert($"Parental Control: the session of {username} on {Environment.MachineName} is locked: {reason}. " +
                                      "The service could not see which program was in front. No program was closed.");
    }

    private void ApplyOtherProbeLock(int sessionId, int userId, string userSid, string username, ProbeLockKind kind, string? exe,
        DateOnly today, TimeSpan unbiased)
    {
        var reason = kind == ProbeLockKind.NeverSuspended
            ? $"a program that is never suspended ({exe}) counted more than {NeverSuspendedAllowance.AllowedSeconds} s in an exhausted category"
            : "the set of Foreground Window exes of the day is full";
        if (_lockGate.Decide() == ProbeLockAction.WouldLock)
        {
            ReportWouldLock(userId, userSid, username, kind, exe, reason, today);
            return;
        }
        switch (LockSession(sessionId, $"Session Probe: {reason}"))
        {
            case LockOutcome.Owed:
                Owe(sessionId, kind);
                return;
            case LockOutcome.AlreadyLocked:
                return;
        }
        _logger.Warning("Probe lock of session {SessionId} ({Username}): {Reason}", sessionId, username, reason);
        _health.TryWrite(() => EventRepository.LogEvent(userSid, EventType.SECURITY_ALERT, $"Session locked: {reason}"), "probe lock event", _logger);
        if (_daily.Take($"lock:{userId}:{kind}:{exe}", today))
            Alert($"Parental Control: the session of {username} on {Environment.MachineName} is locked: {reason}. No program was closed.");
    }

    private void ReportWouldLock(int userId, string userSid, string username, ProbeLockKind kind, string? exe, string reason, DateOnly today)
    {
        var report = _lockGate.WouldLock(userId, today, kind, exe, SessionProbePlatform.UnbiasedNow());
        if (report.WriteEvent)
        {
            _logger.Warning("Probe lock off (shadow mode): would lock the session of {Username}: {Reason} ({Decisions} decision(s))",
                username, reason, report.Decisions);
            _health.TryWrite(() => EventRepository.LogEvent(userSid, EventType.PROBE_WOULD_LOCK,
                $"Would lock ({kind}): {reason}; decisions: {report.Decisions}"), "probe would-lock event", _logger);
        }
        if (report.Alert)
            Alert($"Parental Control (probe locks off): the service would lock the session of {username} on " +
                                      $"{Environment.MachineName}: {reason}." + (exe is null ? "" : $"\nPath: {exe}"));
    }

    private enum LockOutcome
    {
        /// <summary>This call locked the session.</summary>
        Locked,
        /// <summary>The session was locked already: no new lock, no event, no alert.</summary>
        AlreadyLocked,
        /// <summary>A Lock Deferral runs: the lock is owed.</summary>
        Owed,
    }

    // The lock through the Lock Deferral (D14).
    private LockOutcome LockSession(int sessionId, string reason)
    {
        if (IsLocked(sessionId)) return LockOutcome.AlreadyLocked;
        if (!_locker.Lock(sessionId, reason)) return LockOutcome.Owed;
        _tracker.OnSessionLock(sessionId, $"{reason} - workstation locked");
        return LockOutcome.Locked;
    }

    // A parent alert never stops a lock or a count: a failed settings read in SendParentAlert is only logged.
    private void Alert(string text)
    {
        try
        {
            _enforcer.SendParentAlert(text);
        }
        catch (Exception ex)
        {
            LogOnce("alert:" + ex.GetType().Name, () => _logger.Warning(ex, "Session Probe host: a parent alert failed"));
        }
    }

    private void Owe(int sessionId, ProbeLockKind kind)
    {
        if (!_owed.TryGetValue(sessionId, out var set)) _owed[sessionId] = set = [];
        if (set.Add(kind))
            _logger.Information("Probe lock ({Kind}) of session {SessionId} is owed: a Lock Deferral runs", kind, sessionId);
    }

    // The end of a Lock Deferral that owed a lock: lock if a probe lock is still due (D14).
    private void OnOwedLockDue(int sessionId) => Guard("owed lock", () =>
    {
        lock (_gate)
        {
            if (!_ready || !_owed.Remove(sessionId, out var kinds)) return;
            if (!_lockGate.Enforced || !_tracker.ActiveSessions.TryGetValue(sessionId, out var session)) return;
            // A failed user read does not stop an owed lock: the Restricted User of the session is known in memory.
            var user = Safe(() => UserRepository.GetBySid(session.UserSid), null);
            if (user is not null && !user.IsRestricted) return;
            if ((user?.Id ?? UserOf(sessionId)) is not { } userId) return;
            var username = user?.Username ?? $"user {userId}";
            var today = ControlTime.Current.Today;
            var now = SessionProbePlatform.UnbiasedNow();

            var due = new List<string>();
            if ((kinds.Contains(ProbeLockKind.Budget) || kinds.Contains(ProbeLockKind.Strike)) && _monitor.IsLockDue(userId, now, today))
                due.Add("blind samples");
            if (kinds.Contains(ProbeLockKind.FullSet) && _fullSetDay.GetValueOrDefault(userId) == today)
                due.Add("full set");
            // A program that is never suspended: still due if it was over the allowance in the last 15 s and its category is
            // still exhausted.
            if (kinds.Contains(ProbeLockKind.NeverSuspended) && _lastOver.TryGetValue(userId, out var last)
                && now - last.At <= TimeSpan.FromSeconds(15))
            {
                var config = CategoryConfigCache.Current;
                var dayLimits = config.Calculator.Evaluate(today, CategoryUsageForLocks(userId, today),
                    Safe(() => UsageRepository.GetUsage(userId, today)?.BonusMinutes ?? 0, 0));
                if (last.Exes.Any(exe => dayLimits.CategoryLimit(config.Resolver.Resolve(exe).Category)?.IsReached ?? false))
                    due.Add("a program that is never suspended");
            }

            if (due.Count == 0)
            {
                _logger.Information("Owed probe lock of session {SessionId}: no longer due", sessionId);
                return;
            }
            var reason = "Session Probe (owed): " + string.Join(", ", due);
            switch (LockSession(sessionId, reason))
            {
                case LockOutcome.Owed:
                    foreach (var kind in kinds) Owe(sessionId, kind);
                    return;
                case LockOutcome.AlreadyLocked:
                    _logger.Information("Owed probe lock of session {SessionId}: the session is locked already", sessionId);
                    return;
            }
            if (due.Contains("blind samples")) _monitor.LockApplied(userId, today);
            _logger.Warning("Owed probe lock of session {SessionId} ({Username}) applied: {Reason}", sessionId, username, reason);
            _health.TryWrite(() => EventRepository.LogEvent(session.UserSid, EventType.PROBE_BLIND, $"Session locked after a Lock Deferral: {reason}"),
                "probe blind event", _logger);
        }
    });

    // ------------------------------------------------------------------ helpers

    private int? UserOf(int sessionId) =>
        _runs.TryGetValue(sessionId, out var run) ? run.UserId
        : _sessionUsers.TryGetValue(sessionId, out var userId) ? userId : null;

    private bool IsLocked(int sessionId) => !_tracker.ActiveSessions.TryGetValue(sessionId, out var session) || session.IsLocked;

    private void Observe(int sessionId, bool unlocked)
    {
        if (!_tracker.ActiveSessions.TryGetValue(sessionId, out var session)) return;
        var user = Safe(() => UserRepository.GetBySid(session.UserSid), null);
        if (user is { IsRestricted: true }) _monitor.Observe(user.Id, SessionProbePlatform.UnbiasedNow(), unlocked && !_sleep.IsSet);
    }

    private static bool IsGone(int sessionId) =>
        SessionPresence.IsGone(SessionProbePlatform.QueryFails(sessionId), SessionProbePlatform.IsListed(sessionId));

    private void LogOnce(string key, Action log)
    {
        if (_loggedOnce.Add(key)) log();
    }

    private bool LogOnce(string key) => _loggedOnce.Add(key);

    private string TestHooksText()
    {
#if VM_TEST
        return $" (VM_TEST build, test hooks [{_testArgs}] noStart={_testNoStart} trace={_trace})";
#else
        return "";
#endif
    }

#if VM_TEST
    private void Trace(ProbeRun run, ProbeSample sample, SampleReading reading, SampleSelection selection, SampleCredit credit, long receiptTick)
    {
        static string Entry(SampleEntry e) => $"{e.Role}:{(e.Outcome == EntryOutcome.Read ? WindowsPath.GetFileName(e.ExePath ?? "?") : e.Outcome.ToString())}";
        var entries = string.Join(" ", reading.Entries.Select(Entry));
        if (reading.Hung is { } hung) entries += " hung:" + Entry(hung);
        var counted = string.Join(" ", selection.Counted.Select(c => $"{WindowsPath.GetFileName(c.ExePath)}={c.Category}/{c.Role}"));
        _logger.Information("Probe trace s{SessionId} t={T} delay={Delay}ms dur={Dur}ms kind={Kind}{Blind} desk={Desk} [{Entries}] noAct={NoAct} counted=[{Counted}] add={Add:0.0}s gap={Gap:0.0}s item={Item} longGap={LongGap} blind={IsBlind} ended={Ended}",
            run.SessionId, sample.T, receiptTick - sample.T, (long)sample.Duration.TotalMilliseconds, sample.Kind,
            sample.BlindReason is { } r ? $"({r})" : "", sample.Desktop ?? "", entries, reading.CursorNoActivate, counted,
            credit.Time.TotalSeconds, credit.Gap.TotalSeconds, credit.GapItem, credit.LongGap, selection.Blind, selection.EndedItem);
    }
#endif

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Session Probe host: error at {What}", what);
        }
    }

    private T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            LogOnce("read:" + ex.GetType().Name, () => _logger.Warning(ex, "Session Probe host: a read failed; using a fallback"));
            return fallback;
        }
    }

    /// <summary>The probe exe of this installation: <c>{app}\probe\ParentalControl.Probe.exe</c>.</summary>
    internal static string? ProbeExecutablePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var installed = Path.GetFullPath(Path.Combine(baseDir, "..", "probe", "ParentalControl.Probe.exe"));
        if (File.Exists(installed)) return installed;
        var flat = Path.Combine(baseDir, "ParentalControl.Probe.exe");
        return File.Exists(flat) ? flat : null;
    }
}
