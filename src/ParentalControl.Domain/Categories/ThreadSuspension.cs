using System.Diagnostics;
using ThreadState = System.Diagnostics.ThreadState;

namespace ParentalControl.Domain.Categories;

/// <summary>
/// Decides from the thread states of a process whether the process is suspended.
/// </summary>
/// <remarks>
/// NtSuspendProcess increments the suspend count of each thread. A thread in a kernel wait with the reason
/// <see cref="ThreadWaitReason.Executive"/> can keep this reason after the suspension, because Windows delivers the
/// suspension only when that wait ends. The Edge crash handler (<c>msedge.exe --type=crashpad-handler</c>) has such
/// threads. If these threads counted as "not suspended", the service suspended the process again at each tick, the
/// suspend count grew each minute, and a later resume (at most 10 NtResumeProcess calls) left the process suspended.
/// A tool that resumes the process changes the other threads from Suspended to another state, so the check still finds it.
/// </remarks>
public static class ThreadSuspension
{
    /// <summary>
    /// True if each thread waits with the reason Suspended or Executive, and at least one thread waits with the reason
    /// Suspended. False for a process without threads.
    /// </summary>
    public static bool AllSuspended(IEnumerable<(ThreadState State, ThreadWaitReason? WaitReason)> threads)
    {
        var anySuspended = false;
        foreach (var (state, reason) in threads)
        {
            if (state != ThreadState.Wait)
                return false;
            if (reason == ThreadWaitReason.Suspended)
                anySuspended = true;
            else if (reason != ThreadWaitReason.Executive)
                return false;
        }
        return anySuspended;
    }
}
