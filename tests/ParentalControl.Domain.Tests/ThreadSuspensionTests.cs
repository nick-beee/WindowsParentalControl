using System.Diagnostics;
using ThreadState = System.Diagnostics.ThreadState;
using ParentalControl.Domain.Categories;
using Xunit;

namespace ParentalControl.Domain.Tests;

public class ThreadSuspensionTests
{
    private static readonly (ThreadState, ThreadWaitReason?) Suspended = (ThreadState.Wait, ThreadWaitReason.Suspended);
    private static readonly (ThreadState, ThreadWaitReason?) Executive = (ThreadState.Wait, ThreadWaitReason.Executive);

    [Fact]
    public void AllThreadsSuspended_IsSuspended() =>
        Assert.True(ThreadSuspension.AllSuspended(new[] { Suspended, Suspended, Suspended }));

    [Fact]
    public void SuspendedAndExecutiveThreads_IsSuspended() =>
        // The Edge crash handler on VM 102: 8 threads Suspended, 2 threads Executive after NtSuspendProcess.
        Assert.True(ThreadSuspension.AllSuspended(new[] { Suspended, Executive, Suspended, Executive }));

    [Fact]
    public void OnlyExecutiveThreads_IsNotSuspended() =>
        Assert.False(ThreadSuspension.AllSuspended(new[] { Executive, Executive }));

    [Fact]
    public void NoThreads_IsNotSuspended() =>
        Assert.False(ThreadSuspension.AllSuspended(Array.Empty<(ThreadState, ThreadWaitReason?)>()));

    [Fact]
    public void ThreadInUserWait_IsNotSuspended() =>
        // A tool resumed the process: its threads wait for input again (issue #24).
        Assert.False(ThreadSuspension.AllSuspended(new[] { Suspended, (ThreadState.Wait, ThreadWaitReason.UserRequest), Executive }));

    [Fact]
    public void RunningThread_IsNotSuspended() =>
        Assert.False(ThreadSuspension.AllSuspended(new[] { Suspended, (ThreadState.Running, (ThreadWaitReason?)null) }));
}
