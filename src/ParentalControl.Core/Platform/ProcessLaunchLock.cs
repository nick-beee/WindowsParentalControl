namespace ParentalControl.Core.Platform;

/// <summary>
/// The process-wide lock around each process start of the service (session-probe-and-agent-channel, design D2 step 5,
/// N6). A start with <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> makes an inheritable duplicate of one handle directly
/// before <c>CreateProcessAsUserW</c> and closes it directly after. Each other process start of the service holds the
/// same lock, so no other start can inherit that duplicate.
/// </summary>
public static class ProcessLaunchLock
{
    public static readonly Lock Gate = new();
}
