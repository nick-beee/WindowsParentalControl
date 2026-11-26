using System.ServiceProcess;

namespace ParentalControl.Admin.Services;

/// <summary>
/// The state of the Windows service for the texts of the web admin sign-in (require-admin-sign-in, third review SF3).
/// While the service does not run, another program can answer on port 5050, so the Admin app does not show the sign-in
/// as usable.
/// </summary>
public static class ServiceState
{
    public const string ServiceName = "ParentalControl.Service";

    /// <summary>True if the service runs. An error (for example no such service) gives false.</summary>
    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }
}
