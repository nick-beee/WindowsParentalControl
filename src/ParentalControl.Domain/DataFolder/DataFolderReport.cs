namespace ParentalControl.Domain.DataFolder;

public enum DataFolderState
{
    /// <summary>The service did not report a check of the data folder (it did not run, or an older version ran).</summary>
    Unknown,
    /// <summary>The service secured the data folder at its last start.</summary>
    Secured,
    /// <summary>The service could not secure the data folder. It did not open the database and stopped.</summary>
    Failed,
}

/// <summary>
/// The result of the data folder step at the last start of the service. The service writes it to the registry, so the
/// Admin app can show it also when the data folder is not usable (require-admin-sign-in, finding S3).
/// </summary>
/// <param name="Detail">The error for <see cref="DataFolderState.Failed"/>, else null.</param>
/// <param name="QuarantinedTo">The folder that the service moved an untrusted data folder to, or null.</param>
public sealed record DataFolderReport(
    DataFolderState State,
    string? Detail,
    DateTimeOffset? CheckedAtUtc,
    string? QuarantinedTo,
    DateTimeOffset? QuarantinedAtUtc)
{
    public static readonly DataFolderReport None = new(DataFolderState.Unknown, null, null, null, null);
}

/// <summary>The texts of the Admin app about the web admin sign-in and the data folder.</summary>
public static class AdminSignInStatus
{
    /// <summary>
    /// The state line of the section "Web admin sign-in". "Set up on" shows only if the web admin really works:
    /// the sign-in is set up and the service secured the data folder.
    /// </summary>
    public static string Describe(bool credentialSetUp, DateTimeOffset? setupAtUtc, DataFolderReport folder, TimeZoneInfo zone)
    {
        switch (folder.State)
        {
            case DataFolderState.Failed:
                return "Locked: the service could not secure the data folder"
                       + (folder.CheckedAtUtc is { } at ? $" at {Local(at, zone)}" : string.Empty)
                       + $". Error: {folder.Detail ?? "unknown"}";
            case DataFolderState.Unknown:
                return "Locked: the service did not report a check of the data folder. Start the service \"ParentalControl.Service\".";
        }

        if (!credentialSetUp)
            return "Not set up. The web admin is locked until you set up the sign-in.";
        return setupAtUtc is { } setupAt ? $"Set up on {Local(setupAt, zone)}" : "Set up";
    }

    /// <summary>True if the web admin works: the sign-in is set up and the service secured the data folder.</summary>
    public static bool IsWebAdminUsable(bool credentialSetUp, DataFolderReport folder) =>
        credentialSetUp && folder.State == DataFolderState.Secured;

    /// <summary>The warning about a moved, untrusted data folder, or null if there is none to show.</summary>
    public static string? QuarantineWarning(DataFolderReport folder, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(folder.QuarantinedTo))
            return null;
        var when = folder.QuarantinedAtUtc is { } at ? $"On {Local(at, zone)}, the" : "The";
        return $"{when} service found a data folder that it could not trust (another account could change it). "
               + $"It moved the folder to {folder.QuarantinedTo} and started with a new database. "
               + "Set up the limits, Telegram and the sign-in again. Delete the moved folder when you do not need it.";
    }

    private static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd HH:mm");
}
