using Microsoft.Extensions.Hosting.WindowsServices;
using ParentalControl.Service.Web;
using ParentalControl.Core.Services;
using ParentalControl.Core.Data;
using ParentalControl.Service;

// require-admin-sign-in, CB1 a: the host starts at once, so the service reports "running" to the Service Control
// Manager before any long work. StartupWorker then runs the data folder step (D9, D14). The other workers wait for its
// gate: no limit work and no database use before the data folder, the database and the log are safe. If the step is
// slow or fails, StartupWorker applies the Enforced Workstation Lock to each session of a user who is not an
// administrator, and it tries again every minute.
DatabaseManager.RequireServiceStep();
var startup = new ServiceStartup();
var logger = startup.Logger;

var builder = Host.CreateApplicationBuilder(args);

// S3: a failed worker (for example the web server) must not stop the others. The usage worker restarts the service
// itself after a fault, because without it no limit is enforced.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddSingleton(startup);
builder.Services.AddSingleton(logger);
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<ParentAlertOutbox>();
builder.Services.AddSingleton<ControlTimeService>();
builder.Services.AddSingleton<SessionLocker>();
builder.Services.AddSingleton<BreakEnforcer>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<ParentalControl.Domain.Categories.ForegroundHistory>();
builder.Services.AddSingleton<CategoryEnforcer>();
builder.Services.AddHostedService<StartupWorker>();
builder.Services.AddHostedService<UsageMonitorWorker>();
builder.Services.AddHostedService<TelegramWorker>();
builder.Services.AddHostedService<WebServerHost>();
builder.Services.AddHostedService<WebView2RuntimeMonitor>();
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddSingleton<IHostLifetime>(sp =>
    {
        var env = sp.GetRequiredService<IHostEnvironment>();
        var appLifetime = sp.GetRequiredService<IHostApplicationLifetime>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var hostOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>();
        var serviceOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WindowsServiceLifetimeOptions>>();
        var sessionTracker = sp.GetRequiredService<SessionTracker>();
        var serilogLogger = sp.GetRequiredService<Serilog.ILogger>();

        return new ParentalControlServiceLifetime(
            env, appLifetime, loggerFactory, hostOptions, serviceOptions, sessionTracker, serilogLogger, startup);
    });
}

using var host = builder.Build();

// Only connects the Telegram callbacks. TelegramWorker waits for the start gate before it reads a command.
host.Services.GetRequiredService<BreakEnforcer>().AttachTelegram();

// control-clock D1: the Control Time Zone before any worker reads a day key. It needs the registry only, not the database.
host.Services.GetRequiredService<ControlTimeService>().Refresh();

host.Run();
return Environment.ExitCode;
