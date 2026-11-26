using ParentalControl.Core.Services;

namespace ParentalControl.Service;

public sealed class TelegramWorker : BackgroundService
{
    private readonly TelegramBotService _botService;
    private readonly Serilog.ILogger _logger;
    private readonly ServiceStartup _startup;

    public TelegramWorker(TelegramBotService botService, Serilog.ILogger logger, ServiceStartup startup)
    {
        _botService = botService;
        _logger = logger;
        _startup = startup;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The bot reads its settings from the database: wait for the start gate (require-admin-sign-in, CB1).
        await _startup.WaitReadyAsync(stoppingToken);
        _logger.Information("Telegram bot worker started");

        // Background loop for draining pending offline notifications
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _botService.ProcessPendingQueueAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error processing pending Telegram notifications queue");
                }
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }, stoppingToken);

        await _botService.PollUpdatesAsync(stoppingToken);
    }
}
