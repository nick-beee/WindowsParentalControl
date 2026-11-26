using Microsoft.Extensions.Hosting.WindowsServices;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Service;

/// <summary>
/// The Enforced Workstation Lock while the service cannot enforce the limits of a known child (require-admin-sign-in,
/// D14, CB1, CB2, S2): while a slow data folder step runs, while no data folder or no database works (the lockdown),
/// and after a quarantine or a relocation until a Restricted User exists. In these states the service has no settings
/// of a child, so it does not know which user is the child. It applies the lock
/// (<see cref="SessionManager.LockSession"/>) to each active session of a user who is not an administrator.
/// It never closes or kills a program (the hard rule). It writes only to the event log and the registry, never to a
/// file in a data folder.
/// </summary>
public static class DataFolderLockdown
{
    /// <summary>
    /// The exit code after a fault that stops the enforcement (for example an exception in the usage worker). The
    /// process ends without a report of SERVICE_STOPPED, so the Service Control Manager counts a failure and starts the
    /// service again (sc failure: 10 s, 30 s, then 60 s).
    /// </summary>
    public const int RestartExitCode = 3;

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

    /// <summary>
    /// Ends the process so that the Service Control Manager starts the service again. Outside a Windows service (a
    /// test or a console run), it stops the host.
    /// </summary>
    public static void RestartService(IHostApplicationLifetime lifetime)
    {
        if (WindowsServiceHelpers.IsWindowsService())
            Environment.Exit(RestartExitCode); // no SERVICE_STOPPED report: the Service Control Manager restarts the service
        Environment.ExitCode = RestartExitCode;
        lifetime.StopApplication();
    }
}
