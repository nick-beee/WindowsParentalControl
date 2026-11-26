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

    /// <summary>The data folder of the last start, or null (a lockdown, or an older version).</summary>
    public string? Folder { get; init; }

    /// <summary>
    /// The folder that the last start secured without a warning, or null. The fast path of the next start (CB1 b) is
    /// allowed only for this folder.
    /// </summary>
    /// <summary>
    /// The program that holds the port of the web server, as the service found it at a failed bind (third review,
    /// SF3), or null. The service clears it after a successful bind. The text comes from an exe and is untrusted data.
    /// </summary>
    public string? WebPortBlockedBy { get; init; }

    public DateTimeOffset? WebPortBlockedAtUtc { get; init; }

    public string? LastSecuredFolder =>
        State == DataFolderState.Secured && string.IsNullOrEmpty(Detail) ? Folder : null;
}

/// <summary>The texts of the Admin app about the web admin sign-in and the data folder.</summary>
public static class AdminSignInStatus
{
    /// <summary>
    /// The state line of the section "Web admin sign-in". "Set up on" shows only if the web admin really works:
    /// the sign-in is set up and the service secured the data folder.
    /// </summary>
    /// <param name="serviceRunning">
    /// False if the service does not run. The registry still shows the state of its last start, but a stopped service
    /// is the time when another program can take port 5050, so "Set up on" does not show then (third review, SF3).
    /// </param>
    public static string Describe(bool credentialSetUp, DateTimeOffset? setupAtUtc, DataFolderReport folder, TimeZoneInfo zone,
        bool serviceRunning = true)
    {
        if (!serviceRunning)
            return "Locked: " + ServiceStoppedWarning;
        if (PortBlockedWarning(folder, zone) is { } portWarning)
            return "Locked: " + portWarning;
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
    /// While another program holds the port of the web server, the web admin does not count as usable either: a page
    /// on http://localhost:5050 then comes from that program, so the parent must not type a code there (SF3).
    public static bool IsWebAdminUsable(bool credentialSetUp, DataFolderReport folder, bool serviceRunning = true) =>
        serviceRunning && credentialSetUp && folder.State == DataFolderState.Secured && string.IsNullOrEmpty(folder.Detail)
        && string.IsNullOrEmpty(folder.WebPortBlockedBy);

    /// <summary>The text while the service does not run (SF3).</summary>
    public const string ServiceStoppedWarning =
        "the service \"ParentalControl.Service\" does not run, so another program can answer on http://localhost:5050. "
        + "Do not type a sign-in code there. Start the service or restart the PC.";

    /// <summary>
    /// The warning about a program that holds the port of the web server (third review, SF3), or null. While it shows,
    /// the page on http://localhost:5050 can come from a program of the child.
    /// </summary>
    public static string? PortBlockedWarning(DataFolderReport folder, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(folder.WebPortBlockedBy))
            return null;
        var since = folder.WebPortBlockedAtUtc is { } at ? $" since {Local(at, zone)}" : string.Empty;
        return $"another program holds port 5050 of the web admin{since}: {folder.WebPortBlockedBy}. "
               + "A page on http://localhost:5050 can come from that program. Do not type a sign-in code there. "
               + "Parental Control does not close the program. Close it or restart the PC; the service still enforces the limits.";
    }

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
