using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class ServiceStartupTests
{
    [Fact]
    public void Start_is_ready_only_when_the_folder_the_database_and_the_log_work()
    {
        var machine = new StartupStateMachine();
        Assert.Equal(StartupPhase.Preparing, machine.Phase);
        Assert.Equal(StartupPhase.Ready, machine.Report(folderUsable: true, databaseAndLogOpened: true));
    }

    [Fact]
    public void Failure_after_the_folder_step_gives_the_lockdown_and_a_retry_must_open_the_database()
    {
        // CB2 and S5: the folder works, but the database does not open (for example SQLITE_FULL).
        var machine = new StartupStateMachine();
        Assert.Equal(StartupPhase.Lockdown, machine.Report(folderUsable: true, databaseAndLogOpened: false));
        // A retry whose folder step works but whose database still fails stays in the lockdown.
        Assert.Equal(StartupPhase.Lockdown, machine.Report(folderUsable: true, databaseAndLogOpened: false));
        Assert.Equal(StartupPhase.Lockdown, machine.Report(folderUsable: false, databaseAndLogOpened: false));
        // Only a full success ends the lockdown.
        Assert.Equal(StartupPhase.Ready, machine.Report(folderUsable: true, databaseAndLogOpened: true));
        // Ready never goes back.
        Assert.Equal(StartupPhase.Ready, machine.Report(folderUsable: false, databaseAndLogOpened: false));
    }

    [Fact]
    public void Folder_failure_gives_the_lockdown_even_if_the_open_flag_is_true()
    {
        var machine = new StartupStateMachine();
        Assert.Equal(StartupPhase.Lockdown, machine.Report(folderUsable: false, databaseAndLogOpened: true));
    }

    [Fact]
    public void Lock_starts_only_after_a_few_seconds_of_the_folder_step()
    {
        Assert.False(StartupLockPolicy.ShouldLock(StartupPhase.Preparing, TimeSpan.FromSeconds(4.9), false, false));
        Assert.True(StartupLockPolicy.ShouldLock(StartupPhase.Preparing, StartupLockPolicy.LockWhilePreparingAfter, false, false));
    }

    [Fact]
    public void Lockdown_always_locks()
    {
        Assert.True(StartupLockPolicy.ShouldLock(StartupPhase.Lockdown, TimeSpan.Zero, false, true));
    }

    [Fact]
    public void Ready_locks_only_while_the_setup_lock_is_on_and_no_Restricted_User_exists()
    {
        Assert.False(StartupLockPolicy.ShouldLock(StartupPhase.Ready, TimeSpan.Zero, setupLockPending: false, restrictedUserExists: false));
        Assert.True(StartupLockPolicy.ShouldLock(StartupPhase.Ready, TimeSpan.Zero, setupLockPending: true, restrictedUserExists: false));
        Assert.False(StartupLockPolicy.ShouldLock(StartupPhase.Ready, TimeSpan.Zero, setupLockPending: true, restrictedUserExists: true));
    }

    [Fact]
    public void Setup_lock_starts_after_a_move_or_a_new_folder_only()
    {
        // A normal first install has no move and no new folder: no setup lock (S2).
        Assert.False(StartupLockPolicy.StartsSetupLock(quarantined: false, relocated: false));
        Assert.True(StartupLockPolicy.StartsSetupLock(quarantined: true, relocated: false));
        Assert.True(StartupLockPolicy.StartsSetupLock(quarantined: false, relocated: true));
    }

    [Fact]
    public void Setup_lock_ends_when_a_Restricted_User_exists()
    {
        Assert.True(StartupLockPolicy.ReleaseSetupLock(setupLockPending: true, restrictedUserExists: true));
        Assert.False(StartupLockPolicy.ReleaseSetupLock(setupLockPending: true, restrictedUserExists: false));
        Assert.False(StartupLockPolicy.ReleaseSetupLock(setupLockPending: false, restrictedUserExists: true));
    }

    [Fact]
    public void Failed_start_actions_before_the_gate_give_the_lockdown()
    {
        // SF2: the setup lock write or the read of the sessions fails on a full disk. No exit: the lockdown.
        var machine = new StartupStateMachine();
        Assert.Equal(StartupPhase.Lockdown, machine.Report(true, true, startActionsDone: false));
        Assert.Equal(StartupPhase.Ready, machine.Report(true, true, startActionsDone: true));
    }

    [Fact]
    public void Setup_lock_joins_all_tries_of_one_run()
    {
        // SF1: a relocation, then an open failure, then a retry without a relocation still sets the lock.
        var tracker = new SetupLockTracker();
        tracker.Report(quarantined: false, relocated: true);
        tracker.Report(quarantined: false, relocated: false);
        Assert.True(tracker.MustSetLock(registryPending: false));
    }

    [Fact]
    public void Setup_lock_comes_from_the_registry_after_a_restart()
    {
        // SF1: a power cut or a 1013 exit between the move and the database write. The next run has no move.
        var tracker = new SetupLockTracker();
        tracker.Report(quarantined: false, relocated: false);
        Assert.True(tracker.MustSetLock(registryPending: true));
        Assert.False(tracker.MustSetLock(registryPending: false));
    }
}
