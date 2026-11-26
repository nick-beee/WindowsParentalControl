using ParentalControl.Core.Logging;

namespace ParentalControl.Agent;

/// <summary>The log of the Session Agent: one file per Windows user, beside the service log.</summary>
internal static class AgentLog
{
    public static readonly Serilog.ILogger Logger = Create();

    private static Serilog.ILogger Create()
    {
        try
        {
            return LoggingConfig.CreateLogger($"agent-{Environment.UserName}");
        }
        catch
        {
            // No writable log folder: the agent works without a log.
            return Serilog.Core.Logger.None;
        }
    }
}
