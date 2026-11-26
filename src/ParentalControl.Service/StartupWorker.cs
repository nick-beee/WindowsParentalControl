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
/// </summary>
public sealed class StartupWorker : BackgroundService
{
    private readonly ServiceStartup _startup;
    private readonly SessionTracker _sessionTracker;
    private readonly TelegramBotService _telegram;
    private readonly Serilog.ILogger _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HashSet<int> _lockedSessions = new();

    // Tests replace the data folder step, the open of the database, the session lock and the records in the registry
    // and the event log, so a test run does not change the state of an installed service.
    public Func<bool, DataFolderPreparation> PrepareStep { get; init; } = writeEvent => DataFolderStartup.Prepare(writeEvent);
    public Func<DataFolderPreparation, string?>? OpenStep { get; init; }
    public Func<IReadOnlyList<int>> LockStep { get; init; } = DataFolderLockdown.LockSessions;
    public Action<DataFolderPreparation, string, bool> RecordOpenFailure { get; init; } = DataFolderStartup.RecordOpenFailure;
    public Action<EventLogEntryType, int, string> WriteEvent { get; init; } = DataFolderStartup.WriteEvent;
    public TimeSpan LockInterval { get; init; } = DataFolderRecovery.LockInterval;
    public TimeSpan RetryInterval { get; init; } = DataFolderRecovery.RetryInterval;
    /// <summary>False in a test: no session recovery, no admin start check and no setup lock.</summary>
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var prep = await RunStepWithLockAsync(writeEvent: true, stoppingToken);
            var openError = prep.IsLockdown ? null : Open(prep);
            if (_machine.Report(!prep.IsLockdown, openError is null) == StartupPhase.Lockdown)
            {
                if (openError is not null)
                    RecordOpenFailure(prep, openError, true);
                prep = await RunLockdownAsync(stoppingToken);
            }
            if (_machine.Phase != StartupPhase.Ready)
                return; // stopped during the lockdown

            await OnReadyAsync(prep);
            await RunSetupLockAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // An unknown error must not leave a service that neither enforces nor locks: the Service Control Manager
            // starts it again.
            WriteEvent(EventLogEntryType.Error, 1013,
                $"The start of Parental Control failed ({ex.GetType().Name}: {ex.Message}). The service restarts.");
            _logger.Fatal(ex, "The start of the service failed. The service restarts.");
            DataFolderLockdown.RestartService(_lifetime);
        }
    }

    private string? Open(DataFolderPreparation prep) => OpenStep is { } open ? open(prep) : _startup.TryOpen(prep);

    /// <summary>Runs the data folder step on a pool thread and locks while it takes long (CB1 a).</summary>
    private async Task<DataFolderPreparation> RunStepWithLockAsync(bool writeEvent, CancellationToken stoppingToken)
    {
        var started = Stopwatch.StartNew();
        var step = Task.Run(() =>
        {
            try
            {
                return PrepareStep(writeEvent);
            }
            catch (Exception ex)
            {
                return new DataFolderPreparation(null, "(unknown)", null, null, null, "The data folder step failed: " + ex.Message, null);
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
    /// The lockdown (D14, CB2, S5): lock every 2 seconds, and every minute try the whole start again: the data folder
    /// step, the database and the log. Only a full success ends the lockdown, in this process.
    /// </summary>
    private async Task<DataFolderPreparation> RunLockdownAsync(CancellationToken stoppingToken)
    {
        var nextRetry = DateTimeOffset.UtcNow + RetryInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            LockOnce("Parental Control has no usable data folder or database");
            if (DateTimeOffset.UtcNow >= nextRetry)
            {
                var prep = await RunStepWithLockAsync(writeEvent: false, stoppingToken);
                nextRetry = DateTimeOffset.UtcNow + RetryInterval;
                if (!prep.IsLockdown)
                {
                    var openError = Open(prep);
                    if (_machine.Report(true, openError is null) == StartupPhase.Ready)
                    {
                        WriteEvent(EventLogEntryType.Information, 1007,
                            $"Parental Control has a usable data folder and database again ({prep.Folder}). It enforces the limits as normal.");
                        return prep;
                    }
                    RecordOpenFailure(prep, openError!, false);
                }
            }
            await Task.Delay(LockInterval, stoppingToken);
        }
        return new DataFolderPreparation(null, "(stopped)", null, null, null, "stopped", null);
    }

    private async Task OnReadyAsync(DataFolderPreparation prep)
    {
        _lockedSessions.Clear();
        if (RunReadyActions)
        {
            _logger.Information("Parental Control Service starting");
            DataFolderStartup.Log(_logger);
            // S2: a move of an untrusted folder or a new folder gives a new database without a Restricted User.
            if (StartupLockPolicy.StartsSetupLock(prep.Result?.QuarantinedTo is not null, prep.Relocated))
                StartSetupLock();
            _sessionTracker.Initialize();
        }
        _startup.Open();
        if (!RunReadyActions)
            return;
        try
        {
            await AdminAccessStartup.RunAsync(_telegram, _logger);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The start check of the admin sign-in failed");
        }
    }

    private void StartSetupLock()
    {
        SettingsRepository.Set(SettingsRepository.KeyDataFolderSetupLock, DateTimeOffset.UtcNow.ToString("O"));
        const string text = "Parental Control started with a new database after a move of the data folder. " +
                            "It locks each session of a user who is not an administrator until a Restricted User exists " +
                            "or the parent finishes the setup in Parental Control Admin.";
        _logger.Warning(text);
        WriteEvent(EventLogEntryType.Warning, 1010, text);
    }

    /// <summary>The setup lock after a quarantine or a relocation (S2). It ends when a Restricted User exists.</summary>
    private async Task RunSetupLockAsync(CancellationToken stoppingToken)
    {
        if (!RunReadyActions)
            return;
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
