using System.Diagnostics;
using ParentalControl.Core.Data;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Service;

/// <summary>
/// The data folder step at each start of the service (require-admin-sign-in, D9, D14, findings B1, B2, S2, S3).
/// The result goes to the Windows event log and to the registry, because the log file and the database are in the
/// data folder itself and are not usable when the step fails.
/// </summary>
public static class DataFolderStartup
{
    public const string EventSource = "ParentalControl.Service";

    /// <summary>
    /// Runs the data folder step (<see cref="DataFolderCoordinator"/>) and records the result in the registry and the
    /// event log. The step itself does not open the database. A lockdown result means that no folder works.
    /// </summary>
    /// <param name="writeLockdownEvent">False for the retries of the lockdown, so the event log gets one error, not one each minute.</param>
    public static DataFolderPreparation Prepare(bool writeLockdownEvent = true)
    {
        DataFolderPreparation prep;
        try
        {
            // CB1 b: the fast path is allowed only for the folder that the last start secured without a warning.
            var lastSecured = DataFolderStatusStore.Read().LastSecuredFolder;
            prep = DataFolderCoordinator.Prepare(DataFolderRegistry.Default, DataFolderRegistry.ProgramDataRoot,
                lastSecuredFolder: lastSecured);
        }
        catch (Exception ex)
        {
            prep = new DataFolderPreparation(null, "(unknown)", null, null, null, "The data folder step failed: " + ex.Message, null);
        }
        Record(prep, writeLockdownEvent);
        return prep;
    }

    /// <summary>
    /// Records that the database or the log file did not open after a successful data folder step (CB2), for example
    /// because the disk is full. The service then runs the lockdown and tries again.
    /// </summary>
    public static void RecordOpenFailure(DataFolderPreparation prep, string error, bool writeEvent)
    {
        var detail = $"The database, the log or a start action in {prep.Folder} failed: {error}";
        DataFolderStatusStore.TryWrite(DataFolderState.Lockdown, detail, DateTimeOffset.UtcNow, newQuarantine: null);
        if (writeEvent)
            WriteEvent(EventLogEntryType.Error, 1009,
                "Parental Control secured the data folder, but the database, the log file or a start action failed. " +
                "It locks each session of a user who is not an administrator (Enforced Workstation Lock) and tries again every minute. " +
                detail);
    }

    /// <summary>
    /// Writes the Secured state again after the start actions before the gate succeeded at a retry (third review,
    /// SF2). The failed try wrote the state Lockdown.
    /// </summary>
    public static void RecordStartRecovered(DataFolderPreparation prep) =>
        DataFolderStatusStore.TryWrite(DataFolderState.Secured, prep.Warning, DateTimeOffset.UtcNow, newQuarantine: null, folder: prep.Folder);

    private static void Record(DataFolderPreparation prep, bool writeLockdownEvent)
    {
        var now = DateTimeOffset.UtcNow;
        if (prep.IsLockdown)
        {
            DataFolderStatusStore.TryWrite(DataFolderState.Lockdown, prep.Error, now, newQuarantine: null);
            if (writeLockdownEvent)
                WriteEvent(EventLogEntryType.Error, 1001,
                "Parental Control could not secure or create a data folder, so it did not open a database. " +
                "It locks each session of a user who is not an administrator (Enforced Workstation Lock) and tries again every minute. " +
                $"Configured folder: {prep.ConfiguredFolder}. Errors: {prep.Error}");
            return;
        }

        // SF1: the step wrote SetupLockPending before the move or the create. Write it again here, in case that write failed.
        if (StartupLockPolicy.StartsSetupLock(prep.Result?.QuarantinedTo is not null, prep.Relocated))
            DataFolderRegistry.Default.TryMarkSetupLockPending(now);

        var relocation = prep.Relocated
            ? new DataFolderStatusStore.Relocation(prep.ConfiguredFolder, prep.Folder!, prep.RelocationReason!)
            : null;
        // The detail of a Secured state is only the warning of a trusted folder that the service could not reset; the
        // Admin app then shows the web admin as locked. A registry error goes to the event log only.
        DataFolderStatusStore.TryWrite(DataFolderState.Secured, prep.Warning, now, prep.Result?.QuarantinedTo, relocation, prep.Folder);

        if (prep.Result?.QuarantinedTo is { } moved)
        {
            WriteEvent(EventLogEntryType.Warning, 1002,
                $"Parental Control found a data folder that it could not trust ({prep.Result.QuarantineReason}). " +
                $"It moved the folder to {moved} and started with a new database. Set up the limits, Telegram and the sign-in again in Parental Control Admin.");
        }
        if (prep.Result is { Repairs.Count: > 0 })
        {
            WriteEvent(EventLogEntryType.Warning, 1003,
                "Parental Control repaired the data folder: " + string.Join(" ", prep.Result.Repairs));
        }
        if (prep.Relocated)
        {
            WriteEvent(EventLogEntryType.Error, 1004,
                $"Parental Control could not use the data folder {prep.ConfiguredFolder} ({prep.RelocationReason}). " +
                $"It left that folder alone, made the new data folder {prep.Folder} and started with a new database. " +
                "Set up the limits, Telegram and the sign-in again in Parental Control Admin.");
        }
        if (prep.Warning is not null)
        {
            WriteEvent(EventLogEntryType.Warning, 1005,
                $"A step of the data folder check failed, but the folder {prep.Folder} is still trusted, so the service uses it: {prep.Warning}");
        }
        if (prep.RegistryError is not null)
        {
            WriteEvent(EventLogEntryType.Error, 1006, prep.RegistryError);
        }
    }

    /// <summary>Writes the result of the step to the log file of the service, after the logger exists.</summary>
    public static void Log(Serilog.ILogger logger)
    {
        var result = DatabaseManager.DataFolderResult;
        var report = DataFolderStatusStore.Read();
        if (result?.QuarantinedTo is { } moved)
            logger.Error("The data folder was not trusted ({Reason}). The service moved it to {Moved} and started with a new database.",
                result.QuarantineReason, moved);
        foreach (var repair in result?.Repairs ?? Array.Empty<string>())
            logger.Warning("Data folder repair: {Repair}", repair);
        if (report.State == DataFolderState.Secured && report.Detail is { } warning)
            logger.Warning("Data folder warning: {Warning}", warning);
        if (result is { Walked: false })
            logger.Information("The data folder has the rules of the last start, so the check did not walk through the items");
        logger.Information("Data folder: {Folder}", DatabaseManager.DataDirectory);
    }

    public static void WriteEvent(EventLogEntryType type, int eventId, string message)
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
