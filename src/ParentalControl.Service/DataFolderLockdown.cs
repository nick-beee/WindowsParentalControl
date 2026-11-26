using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Service;

/// <summary>
/// The lockdown of the service while no data folder works (require-admin-sign-in, D14). The service does not stop,
/// because a stopped service enforces no limit. It has no database and so no settings: it does not know which user is
/// the child. It therefore applies the Enforced Workstation Lock (<see cref="SessionManager.LockSession"/>) to each
/// active session of a user who is not an administrator, and it tries the data folder step again every minute.
/// It never closes or kills a program (the hard rule). It writes only to the event log and the registry, never to a
/// file in a data folder.
/// </summary>
public static class DataFolderLockdown
{
    /// <summary>
    /// The exit code after the data folder works again. The process ends without a report of SERVICE_STOPPED, so the
    /// Service Control Manager counts a failure and starts the service again (sc failure: 10 s, 30 s, then 60 s).
    /// The next start finds the folder and enforces as normal.
    /// </summary>
    public const int RestartExitCode = 3;

    public static int Run(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "ParentalControl.Service");
        builder.Services.AddHostedService<LockdownWorker>();
        using var host = builder.Build();
        host.Run();
        return Environment.ExitCode;
    }

    /// <summary>Applies the lock to the sessions that <see cref="LockdownPolicy"/> selects. Returns the locked sessions.</summary>
    public static IReadOnlyList<int> LockSessions()
    {
        var sessions = SessionManager.GetLoggedOnSessions()
            .Select(s => new LockdownSession(
                s.SessionId,
                // A disconnected session or a Win+L lock is not active. An unknown flag counts as active (fail closed).
                !s.IsDisconnected && SessionManager.QueryLockFlag(s.SessionId).Flag != WtsLockFlag.Locked,
                SessionAdminCheck.ForSession(s.SessionId)));
        var toLock = LockdownPolicy.SessionsToLock(sessions);
        foreach (var id in toLock)
            SessionManager.LockSession(id);
        return toLock;
    }

    private sealed class LockdownWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _lifetime;

        public LockdownWorker(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nextRetry = DateTimeOffset.UtcNow + DataFolderRecovery.RetryInterval;
            var lockedBefore = new HashSet<int>();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var id in LockSessions())
                    {
                        if (lockedBefore.Add(id))
                            DataFolderStartup.WriteEvent(EventLogEntryType.Warning, 1008,
                                $"Parental Control has no usable data folder and locked session {id} (Enforced Workstation Lock).");
                    }
                }
                catch
                {
                    // Try again at the next interval.
                }

                if (DateTimeOffset.UtcNow >= nextRetry)
                {
                    nextRetry = DateTimeOffset.UtcNow + DataFolderRecovery.RetryInterval;
                    var prep = DataFolderStartup.Prepare(writeLockdownEvent: false);
                    if (!prep.IsLockdown)
                    {
                        DataFolderStartup.WriteEvent(EventLogEntryType.Information, 1007,
                            $"Parental Control has a usable data folder again ({prep.Folder}). The service restarts to enforce as normal.");
                        Restart();
                        return;
                    }
                }

                try
                {
                    await Task.Delay(DataFolderRecovery.LockInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void Restart()
        {
            if (WindowsServiceHelpers.IsWindowsService())
            {
                // No SERVICE_STOPPED report: the Service Control Manager restarts the service.
                Environment.Exit(RestartExitCode);
            }
            Environment.ExitCode = RestartExitCode;
            _lifetime.StopApplication();
        }
    }
}
