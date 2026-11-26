namespace ParentalControl.Domain.DataFolder;

public enum DataFolderState
{
    /// <summary>The service did not report a check of the data folder (it did not run, or an older version ran).</summary>
    Unknown,
    /// <summary>The service uses a secured data folder since its last start.</summary>
    Secured,
    /// <summary>
    /// No data folder worked: not the configured one and not a new one. The service did not open a database. It
    /// applies the Enforced Workstation Lock to each session of a user who is not an administrator and tries again
    /// every minute (D14).
    /// </summary>
    Lockdown,
}

/// <summary>
/// The result of the data folder step at the last start of the service. The service writes it to the registry, so the
/// Admin app can show it also when the data folder is not usable (require-admin-sign-in, finding S3).
/// </summary>
/// <param name="Detail">
/// The error for <see cref="DataFolderState.Lockdown"/>. For <see cref="DataFolderState.Secured"/>, a warning about a
/// step that failed on a folder that is still trusted (the limits work, the web admin stays locked), or null.
/// </param>
/// <param name="QuarantinedTo">The folder that the service moved an untrusted data folder to, or null.</param>
public sealed record DataFolderReport(
    DataFolderState State,
    string? Detail,
    DateTimeOffset? CheckedAtUtc,
    string? QuarantinedTo,
    DateTimeOffset? QuarantinedAtUtc)
{
    public static readonly DataFolderReport None = new(DataFolderState.Unknown, null, null, null, null);

    /// <summary>The folder that the service left alone when it made a new data folder (D14), or null.</summary>
    public string? RelocatedFrom { get; init; }

    /// <summary>The new data folder with a random name, or null.</summary>
    public string? RelocatedTo { get; init; }

    public DateTimeOffset? RelocatedAtUtc { get; init; }

    /// <summary>Why the old folder was not usable, or null.</summary>
    public string? RelocationReason { get; init; }
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
            case DataFolderState.Lockdown:
                return "Locked: the service could not secure or create a data folder"
                       + (folder.CheckedAtUtc is { } at ? $" at {Local(at, zone)}" : string.Empty)
                       + ". It locks each session of a user who is not an administrator and tries again every minute."
                       + $" Error: {folder.Detail ?? "unknown"}";
            case DataFolderState.Unknown:
                return "Locked: the service did not report a check of the data folder. Start the service \"ParentalControl.Service\".";
            case DataFolderState.Secured when !string.IsNullOrEmpty(folder.Detail):
                return "Locked: the service enforces the limits, but it could not reset the access rules of the data folder"
                       + (folder.CheckedAtUtc is { } checkedAt ? $" at {Local(checkedAt, zone)}" : string.Empty)
                       + $". It tries again at its next start. Error: {folder.Detail}";
        }

        if (!credentialSetUp)
            return "Not set up. The web admin is locked until you set up the sign-in.";
        return setupAtUtc is { } setupAt ? $"Set up on {Local(setupAt, zone)}" : "Set up";
    }

    /// <summary>
    /// True if the web admin works: the sign-in is set up and the service secured the data folder. A trusted folder
    /// whose rules the service could not reset (a Secured state with a detail) does not count.
    /// </summary>
    public static bool IsWebAdminUsable(bool credentialSetUp, DataFolderReport folder) =>
        credentialSetUp && folder.State == DataFolderState.Secured && string.IsNullOrEmpty(folder.Detail);

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

    /// <summary>The warning about a new data folder that the service made because the old one was not usable, or null.</summary>
    public static string? RelocationWarning(DataFolderReport folder, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(folder.RelocatedTo))
            return null;
        var when = folder.RelocatedAtUtc is { } at ? $"On {Local(at, zone)}, the" : "The";
        return $"{when} service could not use the data folder {folder.RelocatedFrom ?? "unknown"}"
               + (string.IsNullOrEmpty(folder.RelocationReason) ? "" : $" ({folder.RelocationReason})")
               + $". It made the new data folder {folder.RelocatedTo} and started with a new database. "
               + "Set up the limits, Telegram and the sign-in again. The old folder is left alone: delete it when you do not need it.";
    }

    /// <summary>The warning about a step that failed on a data folder that is still trusted, or null.</summary>
    public static string? StepWarning(DataFolderReport folder) =>
        folder.State == DataFolderState.Secured && !string.IsNullOrEmpty(folder.Detail)
            ? "The service uses the data folder, but a step of its check failed: " + folder.Detail
            : null;

    private static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd HH:mm");
}
