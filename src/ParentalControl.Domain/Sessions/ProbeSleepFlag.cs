namespace ParentalControl.Domain.Sessions;

/// <summary>
/// The sleep state of the Session Probe host (session-probe-and-agent-channel, design D2, "Sleep and resume"). At
/// <c>PBT_APMSUSPEND</c> the host stops the watchdog, the outage timer and the counting until the resume event. If the
/// resume event does not come (a suspend that Windows cancelled, or a lost power event), the flag must not stay set:
/// the unbiased interrupt time does not move during a real sleep, so more than <see cref="StaleAfter"/> of unbiased time
/// after the suspend event means that the PC runs. Then <see cref="ClearIfStale"/> clears the flag once.
/// Not thread-safe: the caller holds one lock.
/// </summary>
public sealed class ProbeSleepFlag
{
    /// <summary>The unbiased time after the suspend event after which the flag is stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private TimeSpan? _since;

    /// <summary>True between the suspend event and the resume event (or the stale clear).</summary>
    public bool IsSet => _since is not null;

    /// <summary>The suspend event, with the unbiased time at the event.</summary>
    public void Suspend(TimeSpan unbiasedNow) => _since = unbiasedNow;

    /// <summary>The resume event. Returns true if the flag was set.</summary>
    public bool Resume()
    {
        var was = _since is not null;
        _since = null;
        return was;
    }

    /// <summary>
    /// Clears the flag if it is set for more than <see cref="StaleAfter"/> of unbiased time. Returns true once, at the
    /// clear; the caller then handles it as a resume and logs a warning.
    /// </summary>
    public bool ClearIfStale(TimeSpan unbiasedNow)
    {
        if (_since is not { } since || unbiasedNow - since <= StaleAfter) return false;
        _since = null;
        return true;
    }
}
