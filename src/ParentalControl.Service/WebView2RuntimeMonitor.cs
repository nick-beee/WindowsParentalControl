using System.Diagnostics;
using ParentalControl.Core;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Time;

namespace ParentalControl.Service;

/// <summary>
/// Checks the machine-wide Evergreen WebView2 Runtime for the Request Window (request-window, design D7). The setup
/// starts the service before it runs the runtime bootstrapper, so the first check waits 15 minutes. Then the check
/// runs once each day. If the runtime is missing, the service writes a warning event, at most one each day of the
/// Control Time Zone. The check has no effect on a limit.
/// </summary>
public sealed class WebView2RuntimeMonitor : BackgroundService
{
    /// <summary>Windows event ID: the WebView2 Runtime is missing, so the Request Window shows its fallback text.</summary>
    public const int RuntimeMissingEventId = 1019;

    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly Serilog.ILogger _logger;
    private readonly OncePerDay _warnings = new();

    public WebView2RuntimeMonitor(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                Check();
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Checks the runtime once. Never throws.</summary>
    public void Check()
    {
        try
        {
            if (WebView2Runtime.FindFolder() is { } folder)
            {
                _logger.Information("WebView2 Runtime found: {Folder}", folder);
                return;
            }

            if (!_warnings.TryTake(ControlTime.Current.Today)) return;
            var text = "The Microsoft Edge WebView2 Runtime is not installed for all users. The request button of " +
                       "Parental Control shows a message instead of the request page. Run the Parental Control setup " +
                       "again with a network connection, or install the Evergreen WebView2 Runtime.";
            _logger.Warning(text);
            DataFolderStartup.WriteEvent(EventLogEntryType.Warning, RuntimeMissingEventId, text);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "The check of the WebView2 Runtime failed");
        }
    }
}
