using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class StorageLockdownStateTests
{
    private static readonly DateOnly Day = new(2026, 11, 26);
    private const long FiveGb = 5L * 1024 * 1024 * 1024;
    private const long FiftyMb = 50L * 1024 * 1024;
    private const long Minute = 60_000;

    /// <summary>A state with a monotonic clock that goes forward one minute at each report, as the normal tick.</summary>
    private sealed class Ticks
    {
        public StorageLockdownState State { get; } = new();
        public long NowMs { get; set; }

        public StorageTickOutcome Fail(DateOnly? day = null, StorageFailure? failure = null) =>
            Report(tickFailed: true, retrySucceeded: false, pendingEmpty: false, free: 0, day, failure: failure);

        public StorageTickOutcome Ok(long free = FiveGb, bool pendingEmpty = true) =>
            Report(tickFailed: false, retrySucceeded: true, pendingEmpty, free);

        public StorageTickOutcome Report(bool tickFailed, bool retrySucceeded, bool pendingEmpty, long? free,
            DateOnly? day = null, bool startProbeFailed = false, StorageFailure? failure = null)
        {
            NowMs += Minute;
            return State.ReportTick(tickFailed, retrySucceeded, pendingEmpty, free, day ?? Day, NowMs, startProbeFailed, failure);
        }
    }

    [Fact]
    public void TwoFailedTicksDoNotEnter()
    {
        var t = new Ticks();
        t.Fail();
        Assert.False(t.Fail().Entered);
        Assert.False(t.State.InLockdown);
    }

    [Fact]
    public void ThreeFailedTicksEnter()
    {
        var t = new Ticks();
        t.Fail();
        t.Fail();
        Assert.True(t.Fail().Entered);
        Assert.True(t.State.InLockdown);
        Assert.False(t.Fail().Entered); // already in it
    }

    [Fact]
    public void SuccessBetweenFailuresDoesNotResetTheCount()
    {
        // S2 of issue #55: the child fills and frees the disk in turns. F-F-S-F enters at the fourth tick.
        var t = new Ticks();
        t.Fail();
        t.Fail();
        t.Ok();
        Assert.True(t.Fail().Entered);
    }

    [Fact]
    public void FillAndFreeInTurnsEntersAtTheThirdFailedTick()
    {
        var t = new Ticks();
        t.Fail();
        t.Ok();
        t.Fail();
        t.Ok();
        Assert.True(t.Fail().Entered);
    }

    [Fact]
    public void FailedTickEverySixMinutesNeverEnters()
    {
        var t = new Ticks();
        for (var i = 0; i < 10; i++)
        {
            Assert.False(t.Fail().Entered);
            for (var j = 0; j < 5; j++)
                t.Ok();
        }
        Assert.False(t.State.InLockdown);
    }

    [Fact]
    public void FailedTickExactlyTenMinutesOldDoesNotCount()
    {
        var t = new Ticks();
        t.Fail();            // at 1 min
        t.Fail();            // at 2 min
        t.NowMs += 8 * Minute; // the next report is at 11 min: the tick of 1 min is exactly 10 minutes old
        Assert.False(t.Fail().Entered);
        Assert.Equal(2, t.State.FailedTicksInWindow);
    }

    [Fact]
    public void FailedProbeOfTheFirstTickEntersAtOnce()
    {
        var t = new Ticks();
        var outcome = t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, startProbeFailed: true);
        Assert.True(outcome.Entered);
        Assert.True(outcome.Alert);
        Assert.True(t.State.InLockdown);
    }

    [Fact]
    public void GoodFirstTickDoesNotEnter()
    {
        var t = new Ticks();
        Assert.False(t.Report(tickFailed: false, retrySucceeded: true, pendingEmpty: true, FiveGb, startProbeFailed: false).Entered);
        Assert.False(t.State.InLockdown);
    }

    [Fact]
    public void EntryGivesTheLastFailure()
    {
        var t = new Ticks();
        var full = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        var check = new StorageFailure("limit check", StorageFailureKind.Internal, "InvalidOperationException");
        t.Fail(failure: full);
        t.Fail(failure: check);
        var outcome = t.Fail(); // a failed retry without a new failure object: the last one stays
        Assert.True(outcome.Entered);
        Assert.Equal(check, outcome.Cause);
    }

    [Fact]
    public void OldCauseIsDroppedWhenTheWindowIsEmpty()
    {
        var t = new Ticks();
        t.Fail(failure: new StorageFailure("limit check", StorageFailureKind.Internal, "InvalidOperationException"));
        for (var i = 0; i < 10; i++)
            t.Ok();
        Assert.Null(t.State.LastFailure);
    }

    [Fact]
    public void SuccessfulProbeWithFiftyMbFreeStaysIn()
    {
        var t = Entered();
        Assert.False(t.Ok(FiftyMb).Left);
        Assert.True(t.State.InLockdown);
    }

    [Fact]
    public void ExactlyHundredMbFreeLeavesAndOneByteLessStays()
    {
        var t = Entered();
        Assert.False(t.Ok(StorageLockdownState.MinFreeBytes - 1).Left);
        Assert.True(t.State.InLockdown);
        Assert.True(t.Ok(StorageLockdownState.MinFreeBytes).Left);
        Assert.False(t.State.InLockdown);
    }

    [Fact]
    public void SuccessfulProbeWithFiveGbFreeLeaves()
    {
        var t = Entered();
        Assert.True(t.Ok().Left);
        Assert.False(t.State.InLockdown);
    }

    [Fact]
    public void FailuresBeforeTheFreeDoNotDelayTheExit()
    {
        // The lock loop writes a lock event that fails between two ticks; then the parent frees space. The retry of the
        // next tick works: the lockdown ends in that tick (spec "Parent frees space": within 60 seconds).
        var t = Entered();
        var outcome = t.Report(tickFailed: true, retrySucceeded: true, pendingEmpty: true, FiveGb);
        Assert.True(outcome.Left);
        Assert.False(t.State.InLockdown);
        Assert.Equal(0, t.State.FailedTicksInWindow);
        Assert.Null(t.State.LastFailure);
    }

    [Fact]
    public void FailedRetryStaysInWithFreeSpace()
    {
        var t = Entered();
        Assert.False(t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb).Left);
        Assert.True(t.State.InLockdown);
    }

    [Fact]
    public void FailuresAfterTheExitCountFromZero()
    {
        var t = Entered();
        t.Report(tickFailed: true, retrySucceeded: true, pendingEmpty: true, FiveGb);
        Assert.False(t.Fail().Entered);
        Assert.False(t.Fail().Entered);
        Assert.True(t.Fail().Entered);
    }

    [Fact]
    public void PendingUsageLeftOrUnknownFreeSpaceStaysIn()
    {
        var t = Entered();
        Assert.False(t.Ok(pendingEmpty: false).Left);
        Assert.False(t.Report(false, true, true, null).Left);
        Assert.True(t.State.InLockdown);
    }

    [Fact]
    public void FirstEntryOfADayAlertsTheSecondDoesNotTheNextDayDoes()
    {
        var t = new Ticks();
        t.Fail(); t.Fail();
        Assert.True(t.Fail().Alert);
        t.Ok();
        t.Fail(); t.Fail();
        var second = t.Fail();
        Assert.True(second.Entered);
        Assert.False(second.Alert);
        t.Ok();
        var next = Day.AddDays(1);
        t.Fail(next); t.Fail(next);
        Assert.True(t.Fail(next).Alert);
    }

    // Review of PR #67, finding 5: on the same day, an entry with a cause of another kind alerts again.
    [Fact]
    public void SameDayEntryWithAnotherKindAlertsAgain()
    {
        var t = new Ticks();
        var locked = new StorageFailure("usage store", StorageFailureKind.Locked, "SqliteException");
        var full = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        t.Fail(failure: locked); t.Fail();
        Assert.True(t.Fail().Alert);
        t.Ok();
        t.Fail(failure: full); t.Fail();
        var second = t.Fail();
        Assert.True(second.Entered);
        Assert.True(second.Alert);
        Assert.Equal(StorageFailureKind.DiskFull, second.Cause?.Kind);
    }

    [Fact]
    public void SameDayEntryWithTheSameKindDoesNotAlertAgain()
    {
        var t = new Ticks();
        var full = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        t.Fail(failure: full); t.Fail();
        Assert.True(t.Fail().Alert);
        t.Ok();
        t.Fail(failure: full with { Operation = "storage probe" }); t.Fail();
        var second = t.Fail();
        Assert.True(second.Entered);
        Assert.False(second.Alert);
    }

    [Fact]
    public void SameDayEntryWithoutACauseDoesNotAlertAgain()
    {
        var t = new Ticks();
        t.Fail(failure: new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException")); t.Fail();
        Assert.True(t.Fail().Alert);
        t.Ok();
        t.Fail(); t.Fail();
        var second = t.Fail();
        Assert.True(second.Entered);
        Assert.Null(second.Cause);
        Assert.False(second.Alert);
    }

    [Fact]
    public void SameKindOnTheNextDayAlerts()
    {
        var t = new Ticks();
        var full = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        t.Fail(failure: full); t.Fail();
        Assert.True(t.Fail().Alert);
        t.Ok();
        var next = Day.AddDays(1);
        t.Fail(next, full); t.Fail(next);
        Assert.True(t.Fail(next).Alert);
    }

    // Review of PR #67, finding 1: a failed category check is a failed tick, enters with its operation, and holds the
    // Storage Lockdown while it fails (the retry of the tick fails).
    [Fact]
    public void FailedCategoryCheckEntersAndHolds()
    {
        var t = new Ticks();
        var category = new StorageFailure("category check", StorageFailureKind.Internal, "SqliteException");
        t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, failure: category);
        t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, failure: category);
        var outcome = t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, failure: category);
        Assert.True(outcome.Entered);
        Assert.Equal("category check", outcome.Cause?.Operation);
        Assert.False(t.Report(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, failure: category).Left);
        Assert.True(t.State.InLockdown);
    }

    private static Ticks Entered()
    {
        var t = new Ticks();
        t.Fail(); t.Fail(); t.Fail();
        return t;
    }
}
