using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace ParentalControl.Service;

public sealed class ParentalControlServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionTracker _sessionTracker;
    private readonly Serilog.ILogger _logger;
    private readonly ServiceStartup _startup;

    /// <summary>The service lifetime of this process, or null outside a Windows service. Used for the exit code.</summary>
    public static ParentalControlServiceLifetime? Current { get; private set; }

    public ParentalControlServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptions,
        SessionTracker sessionTracker,
        Serilog.ILogger logger,
        ServiceStartup startup)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptions)
    {
        Current = this;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        _sessionTracker = sessionTracker;
        _logger = logger;
        _startup = startup;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        // Before the start gate opens, the database is not safe to use (CB1). SessionTracker.Initialize reads all
        // sessions from Windows when the gate opens, so an event before it is not lost.
        if (!_startup.IsReady)
            return;

        try
        {
            switch (changeDescription.Reason)
            {
                case SessionChangeReason.SessionLogon:
                    _sessionTracker.OnUserLogon(changeDescription.SessionId);
                    break;
                case SessionChangeReason.SessionLogoff:
                    _sessionTracker.OnUserLogoff(changeDescription.SessionId);
                    break;
                case SessionChangeReason.SessionLock:
                case SessionChangeReason.ConsoleDisconnect:
                case SessionChangeReason.RemoteDisconnect:
                    _sessionTracker.OnSessionLock(changeDescription.SessionId, "Windows session locked/disconnected");
                    break;
                case SessionChangeReason.SessionUnlock:
                    _sessionTracker.OnSessionUnlock(changeDescription.SessionId);
                    break;
                case SessionChangeReason.ConsoleConnect:
                case SessionChangeReason.RemoteConnect:
                    _sessionTracker.OnSessionReconnect(changeDescription.SessionId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling session change: {Reason}, SessionId: {SessionId}",
                changeDescription.Reason, changeDescription.SessionId);
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (!_startup.IsReady)
            return base.OnPowerEvent(powerStatus);
        try
        {
            switch (powerStatus)
            {
                case PowerBroadcastStatus.Suspend:
                    _sessionTracker.OnSleep();
                    break;
                case PowerBroadcastStatus.ResumeSuspend:
                case PowerBroadcastStatus.ResumeAutomatic:
                    _sessionTracker.OnWake();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling power event: {PowerStatus}", powerStatus);
        }

        return base.OnPowerEvent(powerStatus);
    }
}
