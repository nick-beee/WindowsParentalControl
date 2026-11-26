using ParentalControl.Core.Logging;

namespace ParentalControl.Agent;

/// <summary>
/// The log of the Session Agent, in the local app data of the Windows user. The data folder of the service allows only
/// SYSTEM and the administrators, so the agent (a standard user) cannot write there. The log is for diagnosis only.
/// </summary>
internal static class AgentLog
{
    public static readonly Serilog.ILogger Logger = Create();

    private static Serilog.ILogger Create()
    {
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParentalControl", "logs");
            return LoggingConfig.CreateLogger("agent", folder);
        }
        catch
        {
            // No writable log folder: the agent works without a log.
            return Serilog.Core.Logger.None;
        }
    }
}
