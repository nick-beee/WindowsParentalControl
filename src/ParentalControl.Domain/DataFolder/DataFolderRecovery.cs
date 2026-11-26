namespace ParentalControl.Domain.DataFolder;

/// <summary>The result of one try of the data folder step on the configured folder.</summary>
public enum ConfiguredFolderResult
{
    /// <summary>The service secured the folder.</summary>
    Secured,
    /// <summary>
    /// A step failed, but the folder itself and each item in it are trusted (SYSTEM or Administrators own them, no other
    /// account can change them, no link, no file with more than one name). Only SYSTEM or an administrator can have
    /// made such a folder, so the service can keep its settings and limits.
    /// </summary>
    FailedButTrusted,
    /// <summary>A step failed and the folder is not trusted, or the check itself failed.</summary>
    Failed,
    /// <summary>The registry key or the registry value of the location is not usable.</summary>
    InvalidLocation,
}

/// <summary>The result of one try to create a new data folder with a random name.</summary>
public enum NewFolderResult
{
    Created,
    /// <summary>An item with the name exists already. The service never uses an existing item as a new folder.</summary>
    NameExists,
    Failed,
}

/// <summary>What the service does after the data folder step.</summary>
public enum DataFolderMode
{
    /// <summary>The service uses the configured folder.</summary>
    UseConfigured,
    /// <summary>The service uses the configured folder, which is trusted, and reports the failed step as a warning.</summary>
    UseConfiguredWithWarning,
    /// <summary>The service makes a new folder with a random name and uses it with a new database.</summary>
    CreateNewFolder,
}

/// <summary>
/// The decisions of the data folder step at the start of the service (require-admin-sign-in, D14). The service never
/// stops the enforcement of the limits because of the data folder:
/// <list type="number">
/// <item>It tries the configured folder (from the registry, else the default) up to <see cref="ConfiguredTries"/> times.</item>
/// <item>If the folder stays unusable but is trusted, it keeps it, so a short error does not delete the settings.</item>
/// <item>Else it makes a new folder with a random name, up to <see cref="NewFolderTries"/> names.</item>
/// <item>If no folder works, it applies the Enforced Workstation Lock to each session of a user who is not an
/// administrator, every <see cref="LockInterval"/>, and tries the step again every <see cref="RetryInterval"/>.</item>
/// </list>
/// </summary>
public static class DataFolderRecovery
{
    public const int ConfiguredTries = 3;
    public const int NewFolderTries = 5;
    public static readonly TimeSpan ConfiguredRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LockInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>True if the service tries the configured folder again after <paramref name="triesSoFar"/> tries.</summary>
    public static bool RetryConfigured(ConfiguredFolderResult result, int triesSoFar) =>
        result is ConfiguredFolderResult.Failed && triesSoFar < ConfiguredTries;

    /// <summary>What the service does with the last result of the configured folder.</summary>
    public static DataFolderMode Decide(ConfiguredFolderResult result) => result switch
    {
        ConfiguredFolderResult.Secured => DataFolderMode.UseConfigured,
        ConfiguredFolderResult.FailedButTrusted => DataFolderMode.UseConfiguredWithWarning,
        _ => DataFolderMode.CreateNewFolder,
    };

    /// <summary>True if the service tries another random name after <paramref name="triesSoFar"/> tries.</summary>
    public static bool TryAnotherName(NewFolderResult result, int triesSoFar) =>
        result is not NewFolderResult.Created && triesSoFar < NewFolderTries;
}

/// <summary>Whether the user of a session is an administrator.</summary>
public enum SessionAdminStatus
{
    Administrator,
    NotAdministrator,
    /// <summary>The service could not read the token of the session.</summary>
    Unknown,
}

/// <param name="IsActive">True if the session is connected (not already locked or disconnected).</param>
public sealed record LockdownSession(int SessionId, bool IsActive, SessionAdminStatus Admin);

/// <summary>
/// The lockdown of the service while no data folder works (require-admin-sign-in, D14). The service has no settings
/// then, so it does not know which user is the child. It applies the Enforced Workstation Lock to each active session
/// of a user who is not an administrator. It never closes or kills a program (the hard rule). A session whose user it
/// cannot check is locked too (fail closed).
/// </summary>
public static class LockdownPolicy
{
    public static IReadOnlyList<int> SessionsToLock(IEnumerable<LockdownSession> sessions) =>
        sessions.Where(s => s.IsActive && s.Admin != SessionAdminStatus.Administrator)
                .Select(s => s.SessionId)
                .ToList();
}
