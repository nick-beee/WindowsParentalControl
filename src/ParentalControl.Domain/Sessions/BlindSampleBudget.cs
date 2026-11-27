namespace ParentalControl.Domain.Sessions;

public enum ProbeLockAction
{
    None,
    /// <summary>Apply the Enforced Workstation Lock through the Lock Deferral (a probe lock).</summary>
    Lock,
    /// <summary>The probe locks are off (shadow mode): log and alert a "would lock", no lock.</summary>
    WouldLock,
}

public enum ProbeLockReason
{
    None,
    /// <summary>The 6th blind sample or budget item within 10 minutes of unlocked time.</summary>
    Budget,
    /// <summary>A blind sample after the first lock of the day.</summary>
    Strike,
}

public readonly record struct ProbeLockDecision(ProbeLockAction Action, ProbeLockReason Reason)
{
    public static ProbeLockDecision None { get; } = new(ProbeLockAction.None, ProbeLockReason.None);
}

/// <summary>
/// The rolling budget of blind samples and budget items of one Restricted User (session-probe-and-agent-channel, design
/// D6a). It keeps the unlocked times of the items of the last 10 minutes of unlocked time; readable samples do not
/// remove an item. At the 6th item the lock is due. After the first lock of a day (the strike), each blind sample of
/// that day locks; a budget item never does. The day is the day of the Control Time Zone. The state is in memory.
/// Not thread-safe.
/// </summary>
public sealed class BlindSampleBudget
{
    public const int LockAtItems = 6;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Queue<TimeSpan> _items = new();
    private DateOnly? _strikeDay;

    /// <summary>The items in the window at the last call.</summary>
    public int Count => _items.Count;

    public bool StrikeSet(DateOnly today) => _strikeDay == today;

    /// <summary>Adds one item and gives the lock that is due, if any.</summary>
    /// <param name="unlockedNow">The unlocked clock of the user.</param>
    /// <param name="blind">A blind sample (budget and strike); else a budget item (budget only).</param>
    public ProbeLockReason Add(TimeSpan unlockedNow, DateOnly today, bool blind)
    {
        Expire(unlockedNow);
        _items.Enqueue(unlockedNow);
        if (blind && StrikeSet(today)) return ProbeLockReason.Strike;
        return _items.Count >= LockAtItems ? ProbeLockReason.Budget : ProbeLockReason.None;
    }

    /// <summary>True if a lock that a Lock Deferral owed is still due: the strike is set, or the window holds 6 items.</summary>
    public bool IsLockDue(TimeSpan unlockedNow, DateOnly today)
    {
        Expire(unlockedNow);
        return StrikeSet(today) || _items.Count >= LockAtItems;
    }

    /// <summary>A probe lock was applied (or logged as "would lock"): the strike of the day is set, and the window
    /// starts empty.</summary>
    public void LockApplied(DateOnly today)
    {
        _strikeDay = today;
        _items.Clear();
    }

    /// <summary>Clears the window and the strike (the switch of the probe locks to on).</summary>
    public void Clear()
    {
        _strikeDay = null;
        _items.Clear();
    }

    private void Expire(TimeSpan unlockedNow)
    {
        while (_items.Count > 0 && unlockedNow - _items.Peek() >= Window) _items.Dequeue();
    }
}
