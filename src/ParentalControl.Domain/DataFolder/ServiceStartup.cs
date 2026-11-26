namespace ParentalControl.Domain.DataFolder;

/// <summary>The phase of the service start (require-admin-sign-in, decisions CB1, CB2 and S5).</summary>
public enum StartupPhase
{
    /// <summary>The data folder step runs. No worker uses the database yet.</summary>
    Preparing,
    /// <summary>No data folder works, or the database or the log did not open. The service locks and tries again.</summary>
    Lockdown,
    /// <summary>The data folder, the database and the log work. The workers enforce the limits.</summary>
    Ready,
}

/// <summary>
/// The start of the service as a state machine (require-admin-sign-in, CB1, CB2, S2, S5). The service reports
/// "running" to the Service Control Manager first, and then runs the data folder step in the host. The workers wait
/// until the phase is <see cref="StartupPhase.Ready"/>. The phase becomes Ready only after the data folder step, the
/// open and migration of the database, and the log all succeed, also at a retry of the lockdown. So no retry opens a
/// gap without a lock.
/// </summary>
public sealed class StartupStateMachine
{
    public StartupPhase Phase { get; private set; } = StartupPhase.Preparing;

    /// <summary>
    /// The result of one try: the data folder step (<paramref name="folderUsable"/>), then the open and migration of
    /// the database and the creation of the log (<paramref name="databaseAndLogOpened"/>, false if they did not run).
    /// Returns the new phase. A Ready phase never goes back.
    /// </summary>
    public StartupPhase Report(bool folderUsable, bool databaseAndLogOpened) =>
        Report(folderUsable, databaseAndLogOpened, startActionsDone: true);

    /// <summary>
    /// The result of one try, with the start actions before the gate (<paramref name="startActionsDone"/>): the setup
    /// lock in the new database and the read of the sessions (third review, SF2). An error of these actions on a full
    /// disk gives the lockdown too, not an exit of the process. Returns the new phase. A Ready phase never goes back.
    /// </summary>
    public StartupPhase Report(bool folderUsable, bool databaseAndLogOpened, bool startActionsDone)
    {
        if (Phase == StartupPhase.Ready)
            return Phase;
        Phase = folderUsable && databaseAndLogOpened && startActionsDone ? StartupPhase.Ready : StartupPhase.Lockdown;
        return Phase;
    }
}

/// <summary>
/// Decides if the start must set the setup lock in the database (require-admin-sign-in S2, third review SF1). The
/// service can make a new folder at one try and open its database only at a later try (a lockdown retry), or a power
/// cut can stop it between the move and the first write to the new database. So the decision joins the results of all
/// tries of one run with OR, and also uses the admin-only registry value <c>SetupLockPending</c> that the data folder
/// step writes in the same step as the move or the new folder.
/// </summary>
public sealed class SetupLockTracker
{
    /// <summary>True after a try of this run moved the folder away or made a new folder.</summary>
    public bool MovedInThisRun { get; private set; }

    /// <summary>Records the result of one try of the data folder step.</summary>
    public void Report(bool quarantined, bool relocated) =>
        MovedInThisRun |= StartupLockPolicy.StartsSetupLock(quarantined, relocated);

    /// <summary>True if the start must set the setup lock in the database that it opened.</summary>
    /// <param name="registryPending">True if the registry value <c>SetupLockPending</c> exists.</param>
    public bool MustSetLock(bool registryPending) => MovedInThisRun || registryPending;
}

/// <summary>
/// When the service applies the Enforced Workstation Lock to each session of a user who is not an administrator
/// (require-admin-sign-in, D14, CB1, S2). The lock never closes or kills a program (the hard rule).
/// </summary>
public static class StartupLockPolicy
{
    /// <summary>
    /// The data folder step can take long (a walk of many files). After this time the service locks while the step
    /// still runs, so a slow step gives no time without a limit.
    /// </summary>
    public static readonly TimeSpan LockWhilePreparingAfter = TimeSpan.FromSeconds(5);

    /// <param name="sinceStart">The time since the data folder step started.</param>
    /// <param name="setupLockPending">
    /// True after a quarantine or a relocation (S2): the service started with a new database, which has no Restricted
    /// User, so no limit applies. The lock stays until a Restricted User exists or the parent finishes the setup.
    /// </param>
    /// <param name="restrictedUserExists">True if the database has at least one Restricted User.</param>
    public static bool ShouldLock(StartupPhase phase, TimeSpan sinceStart, bool setupLockPending, bool restrictedUserExists) =>
        phase switch
        {
            StartupPhase.Preparing => sinceStart >= LockWhilePreparingAfter,
            StartupPhase.Lockdown => true,
            _ => setupLockPending && !restrictedUserExists,
        };

    /// <summary>True if the service ends the setup lock (S2): a Restricted User exists in the new database.</summary>
    public static bool ReleaseSetupLock(bool setupLockPending, bool restrictedUserExists) =>
        setupLockPending && restrictedUserExists;

    /// <summary>True if this start must set the setup lock: the service moved the folder away or made a new folder.</summary>
    public static bool StartsSetupLock(bool quarantined, bool relocated) => quarantined || relocated;
}
