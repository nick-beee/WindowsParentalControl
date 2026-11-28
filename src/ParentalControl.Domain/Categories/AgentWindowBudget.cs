namespace ParentalControl.Domain.Categories;

/// <summary>
/// The Agent Window Budget (request-window, design D8): in any 60 minutes of unlocked time, at most 300 seconds in
/// which the Session Agent is a counted process of a Session Probe sample are exempt. Exempt time adds no Category
/// Usage for the entry of the agent and does not count toward the lock of a program that is never suspended.
/// <para>
/// The window moves with the unlocked time between two samples: the gap since the last sample, also if the sample
/// counts no time (for example only System programs). The caller passes that gap as zero after an unlock, a resume, a
/// sleep that the backstop finds, and a planned start (<c>SampleCredit.Gap</c>), and it does not call the budget for a
/// locked sample. So locked time, sleep, hibernation and time without a session do not move the window. The exempt
/// seconds of a sample are at most its counted interval, and they are the last seconds of the advance. The clock is
/// monotonic, so the Control Time Zone has no effect. The state is in memory.
/// </para>
/// <para>
/// No service code calls this class before the Session Probe (PR #43, task 6.1). It must never get its input from the
/// unauthenticated agent routes. One instance per Restricted User (all sessions of the user share it); not thread-safe.
/// </para>
/// </summary>
public sealed class AgentWindowBudget
{
    /// <summary>The length of the sliding window, in seconds of unlocked time.</summary>
    public const int WindowSeconds = 3600;

    /// <summary>The most exempt seconds in one window.</summary>
    public const int MaxExemptSeconds = 300;

    // The unlocked second (1-based index of unlocked time) of each exempt second in the current window.
    private readonly Queue<long> _exempt = new();
    private long _unlockedSeconds;
    private TimeSpan? _lastSample;

    /// <summary>The unlocked seconds that the budget counted so far.</summary>
    public long UnlockedSeconds => _unlockedSeconds;

    /// <summary>The exempt seconds that are left now, without a new sample.</summary>
    public int RemainingSeconds
    {
        get
        {
            Expire(_unlockedSeconds + 1);
            return MaxExemptSeconds - _exempt.Count;
        }
    }

    /// <summary>
    /// Records one sample and gives the exempt seconds for the entry of the Session Agent in it.
    /// </summary>
    /// <param name="monotonicNow">A monotonic time of the sample, for example the <c>t</c> of the probe. The window
    /// never moves more than the time since the last sample of the user, so two sessions of one user do not move it
    /// twice.</param>
    /// <param name="gap">The unlocked time since the last sample of the session (<c>SampleCredit.Gap</c>): zero after an
    /// unlock, a resume, a sleep or a planned start. The window moves by this time.</param>
    /// <param name="intervalSeconds">The interval that the sample counts (the gap rule). It caps the exempt seconds.</param>
    /// <param name="agentCounted">The Session Agent is a counted process of the sample, in any role.</param>
    /// <param name="unlocked">The session is unlocked. Else the window does not move.</param>
    /// <returns>0 to <paramref name="intervalSeconds"/>; the rest of the interval counts as usual.</returns>
    public int Sample(TimeSpan monotonicNow, TimeSpan gap, int intervalSeconds, bool agentCounted, bool unlocked)
    {
        if (intervalSeconds < 0) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));

        var advance = 0;
        if (unlocked && gap > TimeSpan.Zero)
        {
            var gapSeconds = (int)Math.Round(gap.TotalSeconds);
            // A repeated sample at the same time moves nothing.
            advance = _lastSample is { } last
                ? (int)Math.Clamp(Math.Round((monotonicNow - last).TotalSeconds), 0, gapSeconds)
                : gapSeconds;
        }
        if (_lastSample is not { } previous || monotonicNow > previous) _lastSample = monotonicNow;

        // The exempt seconds are the last seconds of the advance, at most the counted interval: the budget then stays
        // used as long as possible (fail closed).
        var exemptFrom = advance - Math.Min(intervalSeconds, advance);
        var exempt = 0;
        for (var i = 0; i < advance; i++)
        {
            _unlockedSeconds++;
            if (!agentCounted || i < exemptFrom) continue;
            Expire(_unlockedSeconds);
            if (_exempt.Count >= MaxExemptSeconds) continue;
            _exempt.Enqueue(_unlockedSeconds);
            exempt++;
        }
        return exempt;
    }

    // Drops each exempt second that is not in the window that ends at the unlocked second `second`.
    private void Expire(long second)
    {
        while (_exempt.Count > 0 && _exempt.Peek() <= second - WindowSeconds) _exempt.Dequeue();
    }
}
