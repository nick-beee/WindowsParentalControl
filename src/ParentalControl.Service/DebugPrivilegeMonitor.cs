using ParentalControl.Core;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Time;

namespace ParentalControl.Service;

/// <summary>
/// Enables <c>SeDebugPrivilege</c> in the token of the service at the start (session-probe-and-agent-channel, design
/// D3b). With it the service reads, lists and suspends a process of the child also if the child removed SYSTEM from
/// its DACL. If the enable fails, the service logs an error and alerts the parent at most once per day of the Control
/// Time Zone, and tries again every hour.
/// </summary>
public sealed class DebugPrivilegeMonitor : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly Serilog.ILogger _logger;
    private readonly ParentAlertOutbox _alerts;
    private readonly OncePerDay _dailyAlert = new();

    public DebugPrivilegeMonitor(Serilog.ILogger logger, ParentAlertOutbox alerts)
    {
        _logger = logger;
        _alerts = alerts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (TryEnable()) return;
                await Task.Delay(RetryInterval, stoppingToken);
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
