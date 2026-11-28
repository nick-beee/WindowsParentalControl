using Microsoft.Data.Sqlite;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Service;

/// <summary>The failures since the last tick: the count and the last failure.</summary>
public readonly record struct StorageFailureReport(int Count, StorageFailure? Last);

/// <summary>
/// The write failures of the service (harden-clock-and-disk, design D7). Each write path reports a failure here, with
/// its operation and its classification (harden-storage-lockdown, design D1, D2). It records failures only: a success
/// of a route that a process of the child can call never resets the count. <see cref="UsageMonitorWorker"/> takes the
/// report once per tick for the Storage Lockdown.
/// </summary>
public sealed class StorageHealth
{
    /// <summary>The one instance of the service.</summary>
    public static StorageHealth Shared { get; } = new();

    private readonly object _gate = new();
    private int _failures;
    private StorageFailure? _last;

    /// <summary>Reports a failure of <paramref name="operation"/>, a fixed label without data of the child.</summary>
    public StorageFailure ReportFailure(string operation, Exception error)
    {
        var failure = Classify(operation, error);
        lock (_gate)
        {
            _failures++;
            _last = failure;
        }
        return failure;
    }

    /// <summary>The count and the last failure since the last call.</summary>
    public StorageFailureReport TakeReport()
    {
        lock (_gate)
        {
            var report = new StorageFailureReport(_failures, _last);
            _failures = 0;
            _last = null;
            return report;
        }
    }

    /// <summary>The count of the failures since the last call.</summary>
    public int TakeFailures() => TakeReport().Count;

    /// <summary>
    /// Runs a database write. On an error, it logs it with <paramref name="what"/>, reports it with
    /// <paramref name="operation"/> (or <paramref name="what"/> if null), and returns false. It never throws, so a failed
    /// write never stops a lock or a limit check. Pass <paramref name="operation"/> if <paramref name="what"/> has data of
    /// the child, for example the path of an exe: the operation goes into event 1015 and the alert to the parent.
    /// </summary>
    public bool TryWrite(Action write, string what, Serilog.ILogger logger, string? operation = null)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex)
        {
            var failure = ReportFailure(operation ?? what, ex);
            logger.Error(ex, "Database write failed: {What} ({Cause})", what, failure.Kind);
            return false;
        }
    }

    /// <summary>The classification of an exception (design D1): the plain values of its chain go to the Domain.</summary>
    public static StorageFailure Classify(string operation, Exception error)
    {
        int? sqliteCode = null;
        int? hresult = null;
        var io = false;
        var denied = false;
        for (var e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SqliteException sqlite:
                    sqliteCode ??= sqlite.SqliteErrorCode;
                    break;
                case IOException ioError:
                    io = true;
                    hresult ??= ioError.HResult;
                    break;
                case UnauthorizedAccessException:
                    denied = true;
                    break;
            }
        }
        var kind = StorageFailureClassifier.Classify(new StorageFailureFacts(sqliteCode, hresult, io, denied));
        return new StorageFailure(operation, kind, error.GetType().Name);
    }
}
