using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Service;

/// <summary>
/// Enforces the blocks of the App Rules (add-blocked-programs). It reads the process starts of
/// <see cref="ProcessStartWatcher"/> and closes a Blocked Start (<see cref="BlockDecision.Close"/>).
/// It is the only caller of <see cref="ProcessControl.Terminate"/>. For each other blocked process it asks the
/// category enforcer for a tick, and the planner suspends the process with the reason Blocked.
/// It also writes the parent report, the child message and the Telegram notice of a blocked program.
/// <see cref="CategoryEnforcer"/> creates it, calls <see cref="Decide"/> and <see cref="Close"/> in each tick,
/// and stops it before it resumes the suspended processes at a service stop.
/// </summary>
internal sealed class BlockEnforcer : IDisposable
{
    /// <summary>
    /// Test hook for the VM: an exe file name. A close of this exe fails as "access denied", so the fallback
    /// (suspend) can be checked. Set it in the service environment only on a test machine.
    /// </summary>
    public const string TestTerminateFailEnv = "PARENTAL_CONTROL_TEST_TERMINATE_FAIL";

    private static readonly TimeSpan HandledLifetime = TimeSpan.FromMinutes(2);

    private readonly CategoryEnforcer _owner;
    private readonly SessionTracker _sessionTracker;
    private readonly Serilog.ILogger _logger;
    private readonly ProtectedPrograms _protected;
    private readonly ProcessStartWatcher _watcher;
    private readonly DateTime _startUtc;
    private readonly string? _testTerminateFailExe;

    private readonly object _gate = new();
    // (pid, start) of each process that this enforcer closed or tried to close, with the time of the try.
    private readonly Dictionary<(int Pid, DateTime Start), DateTime> _handled = new();
    // (pid, start) of each process whose close failed. The decision for it is Suspend from then on.
    private readonly HashSet<(int Pid, DateTime Start)> _closeFailed = new();
    private bool _stopped;
    private Task? _consumer;

    public BlockEnforcer(CategoryEnforcer owner, SessionTracker sessionTracker, Serilog.ILogger logger)
    {
        _owner = owner;
        _sessionTracker = sessionTracker;
        _logger = logger;
        _protected = CategoryRepository.LocalProtectedPrograms();
        _startUtc = DateTime.UtcNow;
        _watcher = new ProcessStartWatcher(sessionId => _sessionTracker.ActiveSessions.ContainsKey(sessionId), logger);
        _testTerminateFailExe = Environment.GetEnvironmentVariable(TestTerminateFailEnv);
        if (!string.IsNullOrWhiteSpace(_testTerminateFailExe))
            _logger.Warning("Block test hook active: a close of {Exe} fails", _testTerminateFailExe);
    }

    public ProtectedPrograms ProtectedPrograms => _protected;

    /// <summary>
    /// The block epoch: the later of the start of this enforcer and the build time of the rule snapshot.
    /// A snapshot is built after the commit of each rule change that it contains, so a process that started
    /// before a change is never "after the block".
    /// </summary>
    private DateTime EpochFor(CategoryConfigCache.Snapshot config) => config.BuiltUtc > _startUtc ? config.BuiltUtc : _startUtc;

    /// <summary>Starts the watcher and its consumer if they do not run. The category enforcer calls it in each tick.</summary>
    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _consumer ??= Task.Run(ConsumeAsync);
        }
        _watcher.EnsureRunning();
    }

    /// <summary>Stops the watcher and every further close. The service calls it before it resumes all suspended processes.</summary>
    public void Stop()
    {
        lock (_gate)
            _stopped = true;
        _watcher.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>True if the exe is a Blocked Program for the block (not protected, not System).</summary>
    public bool IsBlockedProgram(CategoryResolution resolution) => BlockedStartDecider.IsBlockedProgram(resolution, _protected);

    /// <summary>
    /// The block decision for a process of the tick. It reads the parent process only for a Blocked Program.
    /// A process whose close failed before gets Suspend.
    /// </summary>
    public BlockDecision Decide(SessionProcess process, bool isRestrictedSession, CategoryConfigCache.Snapshot config)
    {
        var resolution = config.Resolver.Resolve(process.ExePath);
        if (!isRestrictedSession || !IsBlockedProgram(resolution))
            return BlockDecision.Ignore;
        return Decide(process.Pid, process.StartTimeUtc, ProcessControl.QueryParentPid(process.Pid), resolution, config);
    }

    private BlockDecision Decide(int pid, DateTime startUtc, int? parentPid, CategoryResolution resolution, CategoryConfigCache.Snapshot config)
    {
        lock (_gate)
        {
            if (_closeFailed.Contains((pid, startUtc)))
                return BlockDecision.Suspend;
        }

        var parent = ReadParent(parentPid, config);
        // The detection time is the time of the decision, so a delay in the queue moves the result to Suspend.
        return BlockedStartDecider.Decide(
            new BlockedStartInput(resolution, true, startUtc, DateTime.UtcNow, EpochFor(config), parent), _protected);
    }

    private BlockParent? ReadParent(int? parentPid, CategoryConfigCache.Snapshot config)
    {
        if (parentPid is not { } ppid || ppid is 0 or 4)
            return null;
        var (path, start) = ProcessControl.QueryPathAndStart(ppid);
        if (start is null)
            return null;
        return new BlockParent(ppid, start.Value, IsBlockedProgram(config.Resolver.Resolve(path)));
    }

    /// <summary>
    /// Closes a Blocked Start. Only a <see cref="BlockDecision.Close"/> leads here. If Windows refuses the close,
    /// the process gets Suspend from then on and the category enforcer runs a tick at once (the fallback).
    /// </summary>
    public void Close(int pid, DateTime startUtc, string exePath, User user)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            PruneHandled();
            if (!_handled.TryAdd((pid, startUtc), DateTime.UtcNow))
                return; // the watcher and the tick both found it
        }

        // Read the command line before the close: it selects the silent path and the report count only.
        var kind = SilentStartClassifier.Classify(ProcessControl.QueryCommandLine(pid));

        ProcessOpResult result;
        string detail;
        if (!string.IsNullOrWhiteSpace(_testTerminateFailExe) &&
            string.Equals(WindowsPath.GetFileName(exePath), _testTerminateFailExe.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            result = ProcessOpResult.AccessDenied;
            detail = $"forced by {TestTerminateFailEnv}";
        }
        else
        {
            result = ProcessControl.Terminate(pid, startUtc, out detail);
        }

        switch (result)
        {
            case ProcessOpResult.Ok:
                _logger.Information("Closed Blocked Start {Exe} (pid {Pid}) of {Username}{Silent}",
                    exePath, pid, user.Username, kind.IsSilent ? " (silent)" : "");
                Report(user, exePath, closed: true, kind);
                break;

            case ProcessOpResult.NotFound:
                break; // the process ended already

            default:
                lock (_gate)
                    _closeFailed.Add((pid, startUtc));
                _logger.Warning("Could not close Blocked Start {Exe} (pid {Pid}) of {Username}: {Result} {Detail}. It is suspended instead",
                    exePath, pid, user.Username, result, detail);
                _owner.RequestTick();
                break;
        }
    }

    /// <summary>The category enforcer suspended a process because of a block (a new record).</summary>
    public void OnBlockedSuspended(int pid, string exePath, User user)
    {
        var kind = SilentStartClassifier.Classify(ProcessControl.QueryCommandLine(pid));
        Report(user, exePath, closed: false, kind);
    }

    /// <summary>The parent report, the event log entry, the child message and the Telegram notice.</summary>
    private void Report(User user, string exePath, bool closed, StartKind kind)
    {
        try
        {
            var today = ControlTime.Current.Today;
            var fileName = WindowsPath.GetFileName(exePath);
            // A failed write counts for the Storage Lockdown (storage-failure); the close or the suspension stays.
            var isNew = false;
            if (!StorageHealth.Shared.TryWrite(
                    () => isNew = BlockedStartRepository.Record(user.Id, today, fileName, exePath, closed, counted: !kind.IsSubProcess, DateTime.UtcNow),
                    "blocked start record", _logger))
                return;
            if (isNew)
                StorageHealth.Shared.TryWrite(() => EventRepository.LogEvent(user.Sid, EventType.PROGRAM_BLOCKED,
                    $"Blocked program {fileName} {(closed ? "closed at start" : "paused")}"), "blocked program event", _logger);

            if (kind.IsSilent)
                return;

            // Mark the flag only if a session can show the message; else a later start tries again.
            if (_owner.HasUnlockedSession(user.Id) && BlockedStartRepository.TryMarkChildNotified(user.Id, today, fileName))
            {
                var message = SettingsRepository.GetMessage(SettingsRepository.KeyMsgProgramBlocked,
                    new Dictionary<string, string> { ["name"] = AppName(fileName) });
                _owner.SendToUser(user.Id, message);
            }

            if (SettingsRepository.IsNotifyBlockedTelegramEnabled()
                && !string.IsNullOrWhiteSpace(SettingsRepository.Get(SettingsRepository.KeyTelegramChatId))
                && BlockedStartRepository.TryMarkParentNotified(user.Id, today, fileName))
            {
                var action = closed ? "Das Programm ist gesperrt und wurde geschlossen." : "Das Programm ist gesperrt und wurde pausiert.";
                _owner.SendParentAlert($"Parental Control: {user.Username} wollte {fileName} starten. {action}\n{exePath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not report the blocked program {Exe}", exePath);
        }
    }

    /// <summary>The exe file name without ".exe", for example "chrome".</summary>
    private static string AppName(string fileName) =>
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;

    private void PruneHandled()
    {
        var cutoff = DateTime.UtcNow - HandledLifetime;
        foreach (var key in _handled.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            _handled.Remove(key);
            _closeFailed.Remove(key);
        }
    }

    /// <summary>Reads the process starts of the watcher. One task; the WMI thread only writes into the channel.</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var ev in _watcher.Events.ReadAllAsync())
            {
                try
                {
                    OnProcessStart(ev);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error during the block check of pid {Pid}", ev.Pid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The block consumer stopped");
        }
    }

    private void OnProcessStart(ProcessStartEvent ev)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
        }

        var (path, start) = ProcessControl.QueryPathAndStart(ev.Pid);
        if (start is null || path is null)
            return; // ended already, or not readable: never blocked

        var config = CategoryConfigCache.Current;
        var resolution = config.Resolver.Resolve(path);
        if (!IsBlockedProgram(resolution))
            return;

        // The session can be new and not in the tracker yet: then the tick finds the process and suspends it.
        if (RestrictedUserOf(ev.SessionId) is not { } user)
            return;

        switch (Decide(ev.Pid, start.Value, ev.ParentPid, resolution, config))
        {
            case BlockDecision.Close:
                Close(ev.Pid, start.Value, path, user);
                break;
            case BlockDecision.Suspend:
                _owner.RequestTick();
                break;
        }
    }

    private User? RestrictedUserOf(int sessionId)
    {
        if (!_sessionTracker.ActiveSessions.TryGetValue(sessionId, out var session))
            return null;
        var user = UserRepository.GetBySid(session.UserSid);
        return user is { IsRestricted: true } ? user : null;
    }
}
