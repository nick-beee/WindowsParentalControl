using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests;

public class SessionLockRulesTests
{
    [Theory]
    [InlineData(0, false, WtsLockFlag.Locked)]
    [InlineData(1, false, WtsLockFlag.Unlocked)]
    [InlineData(-1, false, WtsLockFlag.Unknown)] // WTS_SESSIONSTATE_UNKNOWN (0xFFFFFFFF)
    [InlineData(2, false, WtsLockFlag.Unknown)]
    [InlineData(0, true, WtsLockFlag.Unlocked)] // Windows 7 / Server 2008 R2 swap LOCK and UNLOCK
    [InlineData(1, true, WtsLockFlag.Locked)]
    [InlineData(-1, true, WtsLockFlag.Unknown)]
    public void DecodeSessionFlags_reads_the_flag(int raw, bool inverted, WtsLockFlag expected) =>
        Assert.Equal(expected, SessionLockRules.DecodeSessionFlags(raw, inverted));

    [Theory]
    [InlineData(6, 1, true)] // Windows 7, Server 2008 R2
    [InlineData(6, 0, false)]
    [InlineData(6, 2, false)]
    [InlineData(10, 0, false)] // Windows 10 and 11
    public void IsInvertedFlagOs_is_true_for_version_6_1_only(int major, int minor, bool expected) =>
        Assert.Equal(expected, SessionLockRules.IsInvertedFlagOs(major, minor));

    [Theory]
    [InlineData(false, WtsLockFlag.Locked, true)] // Win+L before the service start
    [InlineData(false, WtsLockFlag.Unlocked, false)]
    [InlineData(false, WtsLockFlag.Unknown, false)]
    [InlineData(true, WtsLockFlag.Unlocked, true)] // Enforced Workstation Lock (WTSDisconnectSession)
    [InlineData(true, WtsLockFlag.Unknown, true)]
    [InlineData(true, WtsLockFlag.Locked, true)]
    public void InitialLockState_uses_the_disconnect_and_the_lock_flag(bool disconnected, WtsLockFlag flag, bool expected) =>
        Assert.Equal(expected, SessionLockRules.InitialLockState(disconnected, flag));

    [Fact]
    public void Reconcile_keeps_a_win_l_lock_when_the_flag_is_not_known()
    {
        // Issue #9: the old poll took "WTSActive" as unlocked and overwrote the lock from the event.
        Assert.Equal(LockReconcileAction.Keep, SessionLockRules.Reconcile(trackedLocked: true, isDisconnected: false, WtsLockFlag.Unknown));
    }

    [Fact]
    public void Reconcile_keeps_a_lock_that_windows_confirms()
    {
        Assert.Equal(LockReconcileAction.Keep, SessionLockRules.Reconcile(trackedLocked: true, isDisconnected: false, WtsLockFlag.Locked));
        Assert.Equal(LockReconcileAction.Keep, SessionLockRules.Reconcile(trackedLocked: true, isDisconnected: true, WtsLockFlag.Unknown));
    }

    [Theory]
    [InlineData(WtsLockFlag.Unknown)]
    [InlineData(WtsLockFlag.Unlocked)]
    [InlineData(WtsLockFlag.Locked)]
    public void Reconcile_marks_a_disconnected_session_as_locked(WtsLockFlag flag) =>
        Assert.Equal(LockReconcileAction.MarkLocked, SessionLockRules.Reconcile(trackedLocked: false, isDisconnected: true, flag));

    [Fact]
    public void Reconcile_marks_a_missed_win_l_lock_as_locked() =>
        Assert.Equal(LockReconcileAction.MarkLocked, SessionLockRules.Reconcile(trackedLocked: false, isDisconnected: false, WtsLockFlag.Locked));

    [Fact]
    public void Reconcile_runs_the_unlock_check_and_does_not_unlock_directly() =>
        Assert.Equal(LockReconcileAction.RunUnlockCheck, SessionLockRules.Reconcile(trackedLocked: true, isDisconnected: false, WtsLockFlag.Unlocked));

    [Theory]
    [InlineData(WtsLockFlag.Unlocked)]
    [InlineData(WtsLockFlag.Unknown)]
    public void Reconcile_keeps_an_unlocked_session_that_is_connected(WtsLockFlag flag) =>
        Assert.Equal(LockReconcileAction.Keep, SessionLockRules.Reconcile(trackedLocked: false, isDisconnected: false, flag));
}
