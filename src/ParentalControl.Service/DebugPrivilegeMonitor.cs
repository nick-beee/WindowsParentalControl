using ParentalControl.Core;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Time;

namespace ParentalControl.Service;

/// <summary>
/// Enables <c>SeDebugPrivilege</c> in the token of the service at the start (session-probe-and-agent-channel, design
/// D3b). With it the service reads, lists and suspends a process of the child also if the child removed SYSTEM from
/// its DACL. If the enable fails, the service logs an error and alerts the parent at most once per day of the Control
/// Time Zone, and tries again every hour.
/// <para>
/// The first enable runs synchronously in <see cref="StartAsync"/>. The host starts the hosted services in the order
/// of registration, and <see cref="BackgroundService.ExecuteAsync"/> can run fully asynchronously, so an enable in it
/// can come after <c>StartupWorker</c> and the first reads of the processes of the child (review S6).
/// </para>
/// </summary>
public sealed class DebugPrivilegeMonitor : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly Serilog.ILogger _logger;
    private readonly ParentAlertOutbox _alerts;
    private readonly OncePerDay _dailyAlert = new();
    private bool _enabledAtStart;

    public DebugPrivilegeMonitor(Serilog.ILogger logger, ParentAlertOutbox alerts)
    {
        _logger = logger;
        _alerts = alerts;
    }

    /// <summary>Enables the privilege before the next hosted service starts; then starts the hourly retry if needed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _enabledAtStart = TryEnable();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_enabledAtStart) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(RetryInterval, stoppingToken);
                if (TryEnable()) return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Enables the privilege once. Never throws.</summary>
    public bool TryEnable()
    {
        try
        {
            if (ProcessControl.EnableDebugPrivilege(out var error))
            {
                _logger.Information("SeDebugPrivilege is enabled");
                return true;
            }
            _logger.Error("SeDebugPrivilege cannot be enabled ({Error}). A program whose DACL denies SYSTEM is not read", error);
            if (_dailyAlert.TryTake(ControlTime.Current.Today))
                _alerts.Post($"Parental Control on {Environment.MachineName}: the service cannot enable SeDebugPrivilege ({error}). " +
                             "A program of the child that denies access to the service may not be counted or suspended.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The enable of SeDebugPrivilege failed");
        }
        return false;
    }
}
