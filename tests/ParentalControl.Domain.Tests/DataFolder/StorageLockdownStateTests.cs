using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class StorageLockdownStateTests
{
    private static readonly DateOnly Day = new(2026, 11, 26);
    private const long FiveGb = 5L * 1024 * 1024 * 1024;
    private const long FiftyMb = 50L * 1024 * 1024;

    private static StorageTickOutcome Fail(StorageLockdownState s, DateOnly? day = null) =>
        s.ReportTick(true, false, false, 0, day ?? Day);

    private static StorageTickOutcome Ok(StorageLockdownState s, long free = FiveGb, bool pendingEmpty = true) =>
        s.ReportTick(false, true, pendingEmpty, free, Day);

    [Fact]
    public void TwoFailedTicksDoNotEnter()
    {
        var s = new StorageLockdownState();
        Fail(s);
        Assert.False(Fail(s).Entered);
        Assert.False(s.InLockdown);
    }

    [Fact]
    public void ThreeFailedTicksEnter()
    {
        var s = new StorageLockdownState();
        Fail(s);
        Fail(s);
        Assert.True(Fail(s).Entered);
        Assert.True(s.InLockdown);
        Assert.False(Fail(s).Entered); // already in it
    }

    [Fact]
    public void SuccessBetweenFailuresResetsTheCount()
    {
        var s = new StorageLockdownState();
        Fail(s);
        Fail(s);
        Ok(s);
        Fail(s);
        Fail(s);
        Assert.False(s.InLockdown);
    }

    [Fact]
    public void SuccessfulProbeWithFiftyMbFreeStaysIn()
    {
        var s = Entered();
        Assert.False(Ok(s, FiftyMb).Left);
        Assert.True(s.InLockdown);
    }

    [Fact]
    public void SuccessfulProbeWithFiveGbFreeLeaves()
    {
        var s = Entered();
        Assert.True(Ok(s).Left);
        Assert.False(s.InLockdown);
    }

    [Fact]
    public void FailuresBeforeTheFreeDoNotDelayTheExit()
    {
        // The lock loop writes a lock event that fails between two ticks; then the parent frees space. The retry of the
        // next tick works: the lockdown ends in that tick (spec "Parent frees space": within 60 seconds).
        var s = Entered();
        var outcome = s.ReportTick(tickFailed: true, retrySucceeded: true, pendingEmpty: true, FiveGb, Day);
        Assert.True(outcome.Left);
        Assert.False(s.InLockdown);
        Assert.Equal(0, s.FailedTicksInRow);
    }

    [Fact]
    public void FailedRetryStaysInWithFreeSpace()
    {
        var s = Entered();
        Assert.False(s.ReportTick(tickFailed: true, retrySucceeded: false, pendingEmpty: true, FiveGb, Day).Left);
        Assert.True(s.InLockdown);
    }

    [Fact]
    public void FailuresAfterTheExitCountFromZero()
    {
        var s = Entered();
        s.ReportTick(tickFailed: true, retrySucceeded: true, pendingEmpty: true, FiveGb, Day);
        Assert.False(Fail(s).Entered);
        Assert.False(Fail(s).Entered);
        Assert.True(Fail(s).Entered);
    }

    [Fact]
    public void PendingUsageLeftOrUnknownFreeSpaceStaysIn()
    {
        var s = Entered();
        Assert.False(Ok(s, pendingEmpty: false).Left);
        Assert.False(s.ReportTick(false, true, true, null, Day).Left);
        Assert.True(s.InLockdown);
    }

    [Fact]
    public void FirstEntryOfADayAlertsTheSecondDoesNotTheNextDayDoes()
    {
        var s = new StorageLockdownState();
        Fail(s); Fail(s);
        Assert.True(Fail(s).Alert);
        Ok(s);
        Fail(s); Fail(s);
        var second = Fail(s);
        Assert.True(second.Entered);
        Assert.False(second.Alert);
        Ok(s);
        var next = Day.AddDays(1);
        Fail(s, next); Fail(s, next);
        Assert.True(Fail(s, next).Alert);
    }

    private static StorageLockdownState Entered()
    {
        var s = new StorageLockdownState();
        Fail(s); Fail(s); Fail(s);
        return s;
    }
}
