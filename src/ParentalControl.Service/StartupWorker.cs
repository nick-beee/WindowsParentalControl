using System.Diagnostics;
using ParentalControl.Core.Data;
using ParentalControl.Core.Platform;
using ParentalControl.Core.Services;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Service;

/// <summary>
/// Runs the data folder step after the host started, so the service reports "running" to the Service Control Manager
/// first (require-admin-sign-in, CB1 a). It opens the start gate (<see cref="ServiceStartup"/>) only after the folder
/// step, the database and the log all work (CB2, S5). It applies the Enforced Workstation Lock to each session of a
/// user who is not an administrator:
/// <list type="bullet">
/// <item>while the folder step runs longer than <see cref="StartupLockPolicy.LockWhilePreparingAfter"/> (CB1 a);</item>
/// <item>in the lockdown, when no folder, no database or no log works; it tries the whole start again every minute
/// and opens the gate in this process after a success, so there is no restart gap (D14, CB2, S5);</item>
/// <item>after a quarantine or a relocation, until a Restricted User exists in the new database or the parent finishes
/// the setup in the Admin app (S2).</item>
/// </list>
/// Third review: the setup lock comes from the flags of all tries of one run and from the registry value
/// <c>SetupLockPending</c> (SF1). Each error before the gate opens gives the lockdown, not an exit of the process
/// (SF2); only an error after the gate opened restarts the service (exit code 3). The open of the database runs inside
/// the wrapper that locks after 5 seconds (N-a).
/// </summary>
public sealed class StartupWorker : BackgroundService
{
    private readonly ServiceStartup _startup;
    private readonly SessionTracker _sessionTracker;
    private readonly TelegramBotService _telegram;
    private readonly Serilog.ILogger _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HashSet<int> _lockedSessions = new();
    private readonly SetupLockTracker _setupLock = new();

    // Tests replace the data folder step, the open of the database, the session lock and the records in the registry
    // and the event log, so a test run does not change the state of an installed service.
    public Func<bool, DataFolderPreparation> PrepareStep { get; init; } = writeEvent => DataFolderStartup.Prepare(writeEvent);
    public Func<DataFolderPreparation, string?>? OpenStep { get; init; }
    public Func<IReadOnlyList<int>> LockStep { get; init; } = DataFolderLockdown.LockSessions;
    public Action<DataFolderPreparation, string, bool> RecordOpenFailure { get; init; } = DataFolderStartup.RecordOpenFailure;
    /// <summary>Writes the Secured state again after a retry of the start actions only (SF2).</summary>
    public Action<DataFolderPreparation> RecordStartRecovered { get; init; } = DataFolderStartup.RecordStartRecovered;
    public Action<EventLogEntryType, int, string> WriteEvent { get; init; } = DataFolderStartup.WriteEvent;
    /// <summary>Reads the registry value <c>SetupLockPending</c> (SF1).</summary>
    public Func<bool> ReadSetupLockPending { get; init; } = () => DataFolderRegistry.Default.IsSetupLockPending();
    /// <summary>Deletes the registry value <c>SetupLockPending</c> (SF1). False on an error.</summary>
    public Func<bool> ClearSetupLockPending { get; init; } = () => DataFolderRegistry.Default.TryClearSetupLockPending();
    /// <summary>Writes the setup lock into the database. A full disk makes it throw (SF2).</summary>
    public Action WriteSetupLockSetting { get; init; } =
        () => SettingsRepository.Set(SettingsRepository.KeyDataFolderSetupLock, DateTimeOffset.UtcNow.ToString("O"));
    public TimeSpan LockInterval { get; init; } = DataFolderRecovery.LockInterval;
    public TimeSpan RetryInterval { get; init; } = DataFolderRecovery.RetryInterval;
    /// <summary>The longest wait for the first bind of the web server before the data folder step (SF3).</summary>
    public TimeSpan WebBindWait { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>False in a test: no session recovery, no admin start check and no setup lock loop.</summary>
    public bool RunReadyActions { get; init; } = true;

    public StartupWorker(ServiceStartup startup, SessionTracker sessionTracker, TelegramBotService telegram, Serilog.ILogger logger,
        IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _startup = startup;
        _sessionTracker = sessionTracker;
        _telegram = telegram;
        _logger = logger;
    }

    /// <summary>The phase of the start, for the tests.</summary>
    public StartupPhase Phase => _machine.Phase;

    /// <summary>The number of lock rounds, for the tests.</summary>
    public int LockRounds { get; private set; }

    private readonly StartupStateMachine _machine = new();

    /// <summary>The result of one try of the start.</summary>
    /// <param name="Opened">True if the database and the log are open. A retry then does not open them again.</param>
    /// <param name="StartActionsDone">True if the start actions before the gate succeeded (the setup lock, the sessions).</param>
    private sealed record StartAttempt(DataFolderPreparation Prep, bool FolderUsable, bool Opened, bool StartActionsDone);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // Before the gate opens, no error ends the process (SF2): an exit gave a restart gap of the Service Control
        // Manager without a lock. Each error gives the lockdown: lock every 2 seconds, try again every minute.
        try
        {
            // SF3: the web server binds its port before the data folder step, so a program of the child cannot take
            // the port while the step runs.
            await _startup.WaitWebBindTriedAsync(WebBindWait, stoppingToken);
            while (_machine.Phase != StartupPhase.Ready)
            {
                try
                {
                    await StartUntilReadyAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WriteEvent(EventLogEntryType.Error, 1013,
                        $"The start of Parental Control failed before it enforced the limits ({ex.GetType().Name}: {ex.Message}). " +
                        "It locks each session of a user who is not an administrator and tries again.");
                    LockOnce("The start of Parental Control failed");
                    await Task.Delay(LockInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await AfterGateAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // After the gate opened, an unknown error must not leave a service that neither enforces nor locks: the
            // Service Control Manager starts it again.
            WriteEvent(EventLogEntryType.Error, 1013,
                $"The start of Parental Control failed ({ex.GetType().Name}: {ex.Message}). The service restarts.");
            _logger.Fatal(ex, "The start of the service failed. The service restarts.");
            DataFolderLockdown.RestartService(_lifetime);
        }
    }

    /// <summary>The first try, then the lockdown until a try succeeds. Returns when the phase is Ready.</summary>
    private async Task StartUntilReadyAsync(CancellationToken stoppingToken)
    {
        var attempt = await RunWithLockAsync(() => TryStart(writeEvent: true, last: null), stoppingToken);
        if (Report(attempt) == StartupPhase.Lockdown)
            await RunLockdownAsync(attempt, stoppingToken);
    }

    private StartupPhase Report(StartAttempt attempt) =>
        _machine.Report(attempt.FolderUsable, attempt.Opened, attempt.StartActionsDone);

    private string? Open(DataFolderPreparation prep) => OpenStep is { } open ? open(prep) : _startup.TryOpen(prep);

    /// <summary>
    /// One try of the start before the gate: the data folder step, the open and migration of the database and the log
    /// (N-a: inside the wrapper that locks), then the start actions (SF2). If the database and the log are open from
    /// an earlier try, only the start actions run again: a second open would add a second log sink on the same file.
    /// </summary>
    private StartAttempt TryStart(bool writeEvent, StartAttempt? last)
    {
        DataFolderPreparation prep;
        if (last is { Opened: true })
        {
            prep = last.Prep;
        }
        else
        {
            prep = Prepare(writeEvent);
            // SF1: a move or a new folder at any try of this run sets the setup lock, also if the open fails first.
            _setupLock.Report(prep.Result?.QuarantinedTo is not null, prep.Relocated);
            if (prep.IsLockdown)
                return new StartAttempt(prep, false, false, false);
            var openError = Open(prep);
            if (openError is not null)
            {
                RecordOpenFailure(prep, openError, writeEvent);
                return new StartAttempt(prep, true, false, false);
            }
        }

        var actionError = RunStartActions(prep);
        if (actionError is not null)
        {
            RecordOpenFailure(prep, "A start action before the gate failed: " + actionError, writeEvent && last is null);
            return new StartAttempt(prep, true, true, false);
        }
        if (last is { Opened: true })
            RecordStartRecovered(prep);
        return new StartAttempt(prep, true, true, true);
    }

    private DataFolderPreparation Prepare(bool writeEvent)
    {
        try
        {
            return PrepareStep(writeEvent);
        }
        catch (Exception ex)
        {
            return new DataFolderPreparation(null, "(unknown)", null, null, null, "The data folder step failed: " + ex.Message, null);
        }
    }

    /// <summary>
    /// The start actions before the gate opens (SF1, SF2, N-c). The database is open, but the workers do not run yet.
    /// Returns null after a success, else the error: the caller runs the lockdown, not an exit.
    /// </summary>
    private string? RunStartActions(DataFolderPreparation prep)
    {
        try
        {
            if (RunReadyActions)
            {
                _logger.Information("Parental Control Service starting");
                DataFolderStartup.Log(_logger);
            }
            // SF1: the new database after a move has no Restricted User. The registry value keeps the lock across a
            // retry, an exit, a power cut and a reboot. It is cleared only after the write to the database succeeded.
            if (_setupLock.MustSetLock(ReadSetupLockPending()))
            {
                WriteSetupLockSetting();
                const string text = "Parental Control started with a new database after a move of the data folder. " +
                                    "It locks each session of a user who is not an administrator until a Restricted User exists " +
                                    "or the parent finishes the setup in Parental Control Admin.";
                _logger.Warning(text);
                WriteEvent(EventLogEntryType.Warning, 1010, text);
                if (!ClearSetupLockPending())
                    _logger.Warning("Could not delete the registry value SetupLockPending. The next start sets the setup lock again.");
            }
            // N-c: read the sessions before the gate. The service lifetime ignores session events until the gate
            // opens; AfterGateAsync reads the sessions again for the events in between.
            if (RunReadyActions)
                _sessionTracker.Initialize();
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>The gate is open: the workers enforce. Runs the actions that may use Telegram (N-b, N-c).</summary>
    private async Task AfterGateAsync(CancellationToken stoppingToken)
    {
        _lockedSessions.Clear();
        _startup.Open();
        if (!RunReadyActions)
            return;
        try
        {
            // N-c: a logon between Initialize and Open had no event handler. Initialize skips the known sessions.
            _sessionTracker.Initialize();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The second read of the sessions after the start gate failed. The session sync of the usage worker adds them.");
        }
        // N-b: the setup lock starts before the Telegram calls of the admin start check, which can take long.
        var setupLock = RunSetupLockAsync(stoppingToken);
        try
        {
            await AdminAccessStartup.RunAsync(_telegram, _logger);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The start check of the admin sign-in failed");
        }
        await setupLock;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a pool thread and locks while it takes long (CB1 a, N-a): in the Preparing phase
    /// after 5 seconds, in the lockdown at once.
    /// </summary>
    private async Task<StartAttempt> RunWithLockAsync(Func<StartAttempt> work, CancellationToken stoppingToken)
    {
        var started = Stopwatch.StartNew();
        var step = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                var prep = new DataFolderPreparation(null, "(unknown)", null, null, null, "The start failed: " + ex.Message, null);
                return new StartAttempt(prep, false, false, false);
            }
        }, stoppingToken);
        var lockingLogged = false;
        while (!step.IsCompleted)
        {
            await Task.WhenAny(step, Task.Delay(LockInterval, stoppingToken));
            stoppingToken.ThrowIfCancellationRequested();
            if (step.IsCompleted || !StartupLockPolicy.ShouldLock(_machine.Phase, started.Elapsed, false, false))
                continue;
            if (!lockingLogged && _machine.Phase == StartupPhase.Preparing)
            {
                lockingLogged = true;
                WriteEvent(EventLogEntryType.Warning, 1012,
                    $"The check of the data folder takes longer than {StartupLockPolicy.LockWhilePreparingAfter.TotalSeconds:0} seconds. " +
                    "Parental Control locks each session of a user who is not an administrator until the check ends.");
            }
            LockOnce("The check of the data folder still runs");
        }
        return await step;
    }

    /// <summary>
    /// The lockdown (D14, CB2, S5, SF2): lock every 2 seconds, and every minute try the start again. Only a full success
    /// ends the lockdown, in this process.
    /// </summary>
    private async Task RunLockdownAsync(StartAttempt last, CancellationToken stoppingToken)
    {
        var nextRetry = DateTimeOffset.UtcNow + RetryInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            LockOnce("Parental Control has no usable data folder or database");
            if (DateTimeOffset.UtcNow >= nextRetry)
            {
                var current = last;
                last = await RunWithLockAsync(() => TryStart(writeEvent: false, current), stoppingToken);
                nextRetry = DateTimeOffset.UtcNow + RetryInterval;
                if (Report(last) == StartupPhase.Ready)
                {
                    WriteEvent(EventLogEntryType.Information, 1007,
                        $"Parental Control has a usable data folder and database again ({last.Prep.Folder}). It enforces the limits as normal.");
                    return;
                }
            }
            await Task.Delay(LockInterval, stoppingToken);
        }
        stoppingToken.ThrowIfCancellationRequested();
    }

    /// <summary>The setup lock after a quarantine or a relocation (S2). It ends when a Restricted User exists.</summary>
    private async Task RunSetupLockAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = !string.IsNullOrEmpty(SettingsRepository.Get(SettingsRepository.KeyDataFolderSetupLock));
                if (!pending)
                {
                    _lockedSessions.Clear();
                    return; // the lock is only set at a start, so the loop can end
                }
                var restricted = UserRepository.GetAll().Any(u => u.IsRestricted);
                if (StartupLockPolicy.ReleaseSetupLock(pending, restricted))
                {
                    SettingsRepository.Set(SettingsRepository.KeyDataFolderSetupLock, string.Empty);
                    _logger.Information("A Restricted User exists in the new database. The setup lock ends.");
                    WriteEvent(EventLogEntryType.Information, 1011,
                        "A Restricted User exists in the new database. Parental Control no longer locks the sessions after the move of the data folder.");
                    return;
                }
                if (StartupLockPolicy.ShouldLock(_machine.Phase, TimeSpan.Zero, pending, restricted))
                    LockOnce("The setup after a move of the data folder is not finished");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in the setup lock after a move of the data folder");
                LockOnce("The setup lock could not read the database");
            }
            await Task.Delay(LockInterval, stoppingToken);
        }
    }

    private void LockOnce(string reason)
    {
        LockRounds++;
        try
        {
            foreach (var id in LockStep())
            {
                if (_lockedSessions.Add(id))
                {
                    WriteEvent(EventLogEntryType.Warning, 1008,
                        $"{reason}. Parental Control locked session {id} (Enforced Workstation Lock).");
                    if (_startup.IsReady)
                        _logger.Warning("{Reason}. Locked session {SessionId} (Enforced Workstation Lock)", reason, id);
                }
            }
        }
        catch
        {
            // Try again at the next interval.
        }
    }
}
