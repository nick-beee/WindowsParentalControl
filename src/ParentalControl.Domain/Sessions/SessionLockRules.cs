namespace ParentalControl.Domain.Sessions;

/// <summary>The lock flag that Windows reports for a session (<c>WTSSessionInfoEx</c>, <c>SessionFlags</c>).</summary>
public enum WtsLockFlag
{
    /// <summary>Windows did not give a value, or the value is not known.</summary>
    Unknown,
    Locked,
    Unlocked,
}

/// <summary>What the minute poll does with the lock state of a tracked session.</summary>
public enum LockReconcileAction
{
    /// <summary>Keep the tracked state.</summary>
    Keep,

    /// <summary>Mark the session as locked. A locked session adds no Allowance Time.</summary>
    MarkLocked,

    /// <summary>
    /// Windows shows the session as unlocked, but the tracked state is locked.
    /// Run the unlock check: it locks the session again if the time is over.
    /// </summary>
    RunUnlockCheck,
}

/// <summary>
/// Rules for the lock state of a session. The session change events (lock, unlock, disconnect)
/// set the state at once. The poll of Windows Terminal Services only corrects it.
/// The connect state (<c>WTSActive</c>) cannot show a Win+L lock, so it never sets a session to unlocked.
/// </summary>
public static class SessionLockRules
{
    /// <summary><c>WTS_SESSIONSTATE_LOCK</c>.</summary>
    public const int SessionStateLock = 0;

    /// <summary><c>WTS_SESSIONSTATE_UNLOCK</c>.</summary>
    public const int SessionStateUnlock = 1;

    /// <summary>
    /// Decodes <c>SessionFlags</c>. On Windows 7 and Windows Server 2008 R2 (version 6.1) Windows swaps
    /// the values of LOCK and UNLOCK (a documented error). Each other value is <see cref="WtsLockFlag.Unknown"/>.
    /// </summary>
    public static WtsLockFlag DecodeSessionFlags(int sessionFlags, bool invertedFlagOs) => sessionFlags switch
    {
        SessionStateLock => invertedFlagOs ? WtsLockFlag.Unlocked : WtsLockFlag.Locked,
        SessionStateUnlock => invertedFlagOs ? WtsLockFlag.Locked : WtsLockFlag.Unlocked,
        _ => WtsLockFlag.Unknown,
    };

    /// <summary>True for Windows 7 and Windows Server 2008 R2, which report the inverted flag.</summary>
    public static bool IsInvertedFlagOs(int major, int minor) => major == 6 && minor == 1;

    /// <summary>
    /// The lock state of a session that the service finds without an event, for example at the start of the service.
    /// A disconnected session (also the Enforced Workstation Lock) is locked.
    /// </summary>
    public static bool InitialLockState(bool isDisconnected, WtsLockFlag flag) =>
        isDisconnected || flag == WtsLockFlag.Locked;

    /// <summary>
    /// Compares the tracked state with the poll. The poll never sets the state to unlocked directly:
    /// if Windows shows the session as unlocked, the unlock check must run (it can lock the session again).
    /// If the flag is not known, the tracked state stays.
    /// </summary>
    public static LockReconcileAction Reconcile(bool trackedLocked, bool isDisconnected, WtsLockFlag flag)
    {
        if (isDisconnected || flag == WtsLockFlag.Locked)
            return trackedLocked ? LockReconcileAction.Keep : LockReconcileAction.MarkLocked;

        if (flag == WtsLockFlag.Unlocked && trackedLocked)
            return LockReconcileAction.RunUnlockCheck;

        return LockReconcileAction.Keep;
    }
}
