namespace ParentalControl.Domain.DataFolder;

/// <summary>The cause of a failed write or a failed limit check (harden-storage-lockdown, design D1).</summary>
public enum StorageFailureKind
{
    /// <summary>SQLite or Windows reports a full disk.</summary>
    DiskFull,

    /// <summary>The database is read-only, or Windows denies the access.</summary>
    ReadOnly,

    /// <summary>Another connection or program locks the database.</summary>
    Locked,

    /// <summary>A disk or file error that is not a full disk.</summary>
    DiskError,

    /// <summary>Another database error, for example a damaged database file.</summary>
    DatabaseError,

    /// <summary>An error of the service that is not a database error, for example a bug in a limit check.</summary>
    Internal,
}

/// <summary>
/// The plain values of an exception that the classification needs. The service takes them from the exception chain,
/// so the Domain needs no SQLite package.
/// </summary>
/// <param name="SqliteErrorCode">The error code of a <c>SqliteException</c> in the chain, or null.</param>
/// <param name="HResult">The HResult of an <c>IOException</c> in the chain, or null.</param>
/// <param name="IsIoError">The chain has an <c>IOException</c>.</param>
/// <param name="IsAccessDenied">The chain has an <c>UnauthorizedAccessException</c>.</param>
public readonly record struct StorageFailureFacts(int? SqliteErrorCode, int? HResult, bool IsIoError, bool IsAccessDenied);

/// <summary>
/// One failure: a fixed label of the operation, the kind, and the name of the exception type. The label and the type
/// name hold no data of the child (no path, no window title), so the event and the alert can show them.
/// </summary>
public sealed record StorageFailure(string Operation, StorageFailureKind Kind, string ExceptionType);

/// <summary>Classifies a failure by its SQLite code and its HResult (design D1).</summary>
public static class StorageFailureClassifier
{
    public const int SqlitePerm = 3;
    public const int SqliteBusy = 5;
    public const int SqliteLocked = 6;
    public const int SqliteReadOnly = 8;
    public const int SqliteIoErr = 10;
    public const int SqliteFull = 13;
    public const int SqliteCantOpen = 14;
    public const int SqliteAuth = 23;

    /// <summary><c>ERROR_DISK_FULL</c> (112) as an HResult.</summary>
    public const int HResultDiskFull = unchecked((int)0x80070070);

    /// <summary><c>ERROR_HANDLE_DISK_FULL</c> (39) as an HResult.</summary>
    public const int HResultHandleDiskFull = unchecked((int)0x80070027);

    public static StorageFailureKind Classify(StorageFailureFacts facts)
    {
        // An extended SQLite code has the primary code in the low 8 bits.
        var primary = facts.SqliteErrorCode is { } code ? code & 0xFF : (int?)null;

        if (primary == SqliteFull || facts.HResult is HResultDiskFull or HResultHandleDiskFull)
            return StorageFailureKind.DiskFull;
        if (primary is SqliteReadOnly or SqlitePerm or SqliteAuth || facts.IsAccessDenied)
            return StorageFailureKind.ReadOnly;
        if (primary is SqliteBusy or SqliteLocked)
            return StorageFailureKind.Locked;
        if (primary is SqliteIoErr or SqliteCantOpen || facts.IsIoError)
            return StorageFailureKind.DiskError;
        if (primary is not null)
            return StorageFailureKind.DatabaseError;
        return StorageFailureKind.Internal;
    }
}

/// <summary>
/// The texts of the Storage Lockdown entry: event 1015 and the Telegram alert (design D2). Only a full disk says that
/// the disk is full. The texts ask the parent to free space if the cause is a full disk, or if the free space is below
/// <see cref="StorageLockdownState.MinFreeBytes"/> or not known, because the exit needs that free space for each cause.
/// The texts stay in English.
/// </summary>
public static class StorageLockdownTexts
{
    /// <summary>The cause of a kind, as a part of a sentence.</summary>
    public static string Cause(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.DiskFull => "the disk is full",
        StorageFailureKind.ReadOnly => "the database is read-only or access is denied",
        StorageFailureKind.Locked => "another program locks the database",
        StorageFailureKind.DiskError => "a disk or file error",
        StorageFailureKind.DatabaseError => "a database error",
        _ => "an internal error of the service, not a database error",
    };

    /// <summary>The space left on the volume in MB, or "unknown".</summary>
    public static string Free(long? freeBytes) => freeBytes is { } b ? $"{b / (1024 * 1024)} MB" : "unknown";

    private static string MinFree => $"{StorageLockdownState.MinFreeBytes / (1024 * 1024)} MB";

    private static string What(StorageFailure? failure) => failure is null
        ? "The cause is not known"
        : $"The last failure: {failure.Operation}, {Cause(failure.Kind)} ({failure.ExceptionType})";

    private static bool IsDiskFull(StorageFailure? failure) => failure?.Kind == StorageFailureKind.DiskFull;

    /// <summary>The free space is below the exit minimum, or not known (review of PR #67, finding 2).</summary>
    private static bool IsLowSpace(long? freeBytes) => freeBytes is not { } b || b < StorageLockdownState.MinFreeBytes;

    /// <summary>The text of event 1015.</summary>
    public static string EntryEvent(StorageFailure? failure, long? freeBytes)
    {
        string advice;
        if (IsDiskFull(failure))
            advice = $"An administrator must free space on the disk, to at least {MinFree}.";
        else if (IsLowSpace(freeBytes))
            advice = "An administrator must read the service log. " +
                     $"An administrator must also free space on the disk, to at least {MinFree}, because the Storage Lockdown ends only with {MinFree} free.";
        else
            advice = "This cause is not a full disk. An administrator must read the service log.";
        return $"Parental Control cannot write its database or check a limit. {What(failure)}. " +
               $"Space left on the volume of the data folder: {Free(freeBytes)}. " +
               $"It locks each session of a user who is not an administrator until the cause ends. {advice}";
    }

    /// <summary>The Telegram alert to the parent.</summary>
    public static string Alert(string machine, StorageFailure? failure, long? freeBytes)
    {
        string advice;
        if (IsDiskFull(failure))
            advice = $"Sign in as an administrator and free space on the disk, to at least {MinFree}.";
        else if (IsLowSpace(freeBytes))
            advice = "Sign in as an administrator and read event 1015 in the Windows event log and the service log. " +
                     $"Also free space on the disk, to at least {MinFree}, because the Storage Lockdown ends only with {MinFree} free.";
        else
            advice = "Sign in as an administrator and read event 1015 in the Windows event log and the service log.";
        return $"Parental Control on {machine}: the Storage Lockdown started. {What(failure)}. " +
               $"Space left on the disk: {Free(freeBytes)}. Each session of a child is locked, and the programs stay open. {advice}";
    }
}
