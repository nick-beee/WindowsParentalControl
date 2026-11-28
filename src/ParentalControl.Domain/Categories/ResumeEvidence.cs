namespace ParentalControl.Domain.Categories;

/// <summary>What the CPU time of a recorded process says about a resume (lock-after-repeated-resume D10).</summary>
public enum ResumeEvidence
{
    /// <summary>The CPU time grew by at least <see cref="ChildResumeEvidence.MinCpuGrowth"/>: the process really ran.</summary>
    Ran,

    /// <summary>The CPU time grew less than <see cref="ChildResumeEvidence.MinCpuGrowth"/>, or not at all.</summary>
    NoGrowth,

    /// <summary>The service has no CPU time of the process from a time when it was suspended.</summary>
    NoBaseline,

    /// <summary>The service could not read the CPU time now (an exception, access denied).</summary>
    ReadFailed,
}

/// <summary>
/// Decides whether a recorded process really ran after the service last saw it suspended (issue #24, change
/// <c>lock-after-repeated-resume</c>, design D10, D11). Only <see cref="ResumeEvidence.Ran"/> lets a Child Resume count.
/// Each other result gives no count; the planner still suspends a running process again.
/// </summary>
/// <remarks>
/// The thread check (<see cref="ThreadSuspension"/>) can see a suspended process as running, for example when a
/// suspended thread runs a kernel APC for a moment, or when a thread keeps a kernel wait after the suspension. Such a
/// thread uses almost no CPU time, and a suspended thread cannot run user code, so the service reads the user-mode CPU
/// time only. A process that the child resumes and uses for some seconds uses much more. Windows charges the CPU time of a thread in clock ticks (usually 15.625 ms), so one tick can come from a thread that
/// ran for some microseconds. <see cref="MinCpuGrowth"/> is more than 3 ticks.
/// </remarks>
public static class ChildResumeEvidence
{
    /// <summary>The user-mode CPU time that a process must use after its suspension to count as resumed.</summary>
    public static readonly TimeSpan MinCpuGrowth = TimeSpan.FromMilliseconds(50);

    /// <param name="suspendedCpu">The CPU time when the service last saw the process suspended, or null.</param>
    /// <param name="currentCpu">The CPU time now, or null if the read failed.</param>
    public static ResumeEvidence Judge(TimeSpan? suspendedCpu, TimeSpan? currentCpu) =>
        currentCpu is not { } now ? ResumeEvidence.ReadFailed
        : suspendedCpu is not { } then ? ResumeEvidence.NoBaseline
        : now - then >= MinCpuGrowth ? ResumeEvidence.Ran
        : ResumeEvidence.NoGrowth;
}
