using System.Diagnostics;
using ParentalControl.Core.Data;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Service;

/// <summary>
/// The data folder step at each start of the service (require-admin-sign-in, findings B1, B2, S2, S3).
/// The result goes to the Windows event log and to the registry, because the log file and the database are in the
/// data folder itself and are not usable when the step fails.
/// </summary>
public static class DataFolderStartup
{
    public const string EventSource = "ParentalControl.Service";

    /// <summary>
    /// Secures the data folder and opens the database. Returns false if the folder is not safe: the service must then
    /// stop without a use of the folder (fail closed). The Service Control Manager starts it again (sc failure).
    /// </summary>
    public static bool TryInitialize()
    {
        try
        {
            DatabaseManager.Initialize(enforceDataFolderSecurity: true);
        }
        catch (DataFolderUnsafeException ex)
        {
            DataFolderStatusStore.TryWrite(DataFolderState.Failed, ex.Message, DateTimeOffset.UtcNow, newQuarantine: null);
            WriteEvent(EventLogEntryType.Error, 1001,
                "Parental Control stopped: the data folder is not safe, so the service did not open the database. " +
                "The service tries again at its next start. " + ex);
            return false;
        }

        var result = DatabaseManager.DataFolderResult;
        DataFolderStatusStore.TryWrite(DataFolderState.Secured, null, DateTimeOffset.UtcNow, result?.QuarantinedTo);
        if (result?.QuarantinedTo is { } moved)
        {
            WriteEvent(EventLogEntryType.Warning, 1002,
                $"Parental Control found a data folder that it could not trust ({result.QuarantineReason}). " +
                $"It moved the folder to {moved} and started with a new database. Set up the limits, Telegram and the sign-in again in Parental Control Admin.");
        }
        if (result is { Repairs.Count: > 0 })
        {
            WriteEvent(EventLogEntryType.Warning, 1003,
                "Parental Control repaired the data folder: " + string.Join(" ", result.Repairs));
        }
        return true;
    }

    /// <summary>Writes the result of the step to the log file of the service, after the logger exists.</summary>
    public static void Log(Serilog.ILogger logger)
    {
        var result = DatabaseManager.DataFolderResult;
        if (result is null)
            return;
        if (result.QuarantinedTo is { } moved)
            logger.Error("The data folder was not trusted ({Reason}). The service moved it to {Moved} and started with a new database.",
                result.QuarantineReason, moved);
        foreach (var repair in result.Repairs)
            logger.Warning("Data folder repair: {Repair}", repair);
    }

    private static void WriteEvent(EventLogEntryType type, int eventId, string message)
    {
        try
        {
            if (!EventLog.SourceExists(EventSource))
                EventLog.CreateEventSource(EventSource, "Application");
            EventLog.WriteEntry(EventSource, message, type, eventId);
        }
        catch
        {
            // The registry status still shows the result in the Admin app.
        }
    }
}
