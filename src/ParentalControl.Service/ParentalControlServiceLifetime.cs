using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace ParentalControl.Service;

public sealed class ParentalControlServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionTracker _sessionTracker;
    private readonly Serilog.ILogger _logger;
    private readonly ServiceStartup _startup;
    private readonly SessionProbeHost? _probes;

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
        ServiceStartup startup,
        SessionProbeHost? probes = null)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptions)
    {
        Current = this;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        _sessionTracker = sessionTracker;
        _logger = logger;
        _startup = startup;
        _probes = probes;
    }

    /// <summary>The shutdown control: the probes end as at a service stop (session-probe-and-agent-channel D2). The log
    /// shows its order against the end of each probe.</summary>
    protected override void OnShutdown()
    {
        _logger.Information("Service control: shutdown");
        try
        {
            _probes?.StopAll("shutdown control");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error ending the Session Probes at the shutdown");
        }
        base.OnShutdown();
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
                    _probes?.OnLogon(changeDescription.SessionId);
                    break;
                case SessionChangeReason.SessionLogoff:
                    // The probe first: each pending end of the session becomes a Session-End End (D2).
                    _probes?.OnLogoff(changeDescription.SessionId);
                    _sessionTracker.OnUserLogoff(changeDescription.SessionId);
                    break;
                case SessionChangeReason.SessionLock:
                case SessionChangeReason.ConsoleDisconnect:
                case SessionChangeReason.RemoteDisconnect:
                    _sessionTracker.OnSessionLock(changeDescription.SessionId, "Windows session locked/disconnected");
                    _probes?.OnLock(changeDescription.SessionId);
                    break;
                case SessionChangeReason.SessionUnlock:
                    _sessionTracker.OnSessionUnlock(changeDescription.SessionId);
                    _probes?.OnUnlock(changeDescription.SessionId, "unlock");
                    break;
                case SessionChangeReason.ConsoleConnect:
                case SessionChangeReason.RemoteConnect:
                    _sessionTracker.OnSessionReconnect(changeDescription.SessionId);
                    _probes?.OnUnlock(changeDescription.SessionId, "connect");
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
                    _probes?.OnSuspend();
                    break;
                case PowerBroadcastStatus.ResumeSuspend:
                case PowerBroadcastStatus.ResumeAutomatic:
                    _sessionTracker.OnWake();
                    _probes?.OnResume();
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
