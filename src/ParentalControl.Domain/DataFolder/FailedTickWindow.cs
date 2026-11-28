namespace ParentalControl.Domain.DataFolder;

/// <summary>
/// The failed ticks of the last <see cref="Length"/> (harden-storage-lockdown, design D5). It keeps the times of the
/// monotonic clock (milliseconds, <c>Environment.TickCount64</c>), so a change of the wall clock has no effect. A
/// successful tick does not remove a failed tick. Not thread-safe: <see cref="StorageLockdownState"/> calls it under
/// its lock.
/// </summary>
public sealed class FailedTickWindow
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(10);

    private readonly Queue<long> _failedAtMs = new();

    /// <summary>The failed ticks in the window at the time of the last call of <see cref="Report"/>.</summary>
    public int Count => _failedAtMs.Count;

    /// <summary>Adds a failed tick at <paramref name="nowMs"/> if <paramref name="failed"/>, then drops each failed tick
    /// that is <see cref="Length"/> or more old. Returns the failed ticks in the window.</summary>
    public int Report(bool failed, long nowMs)
    {
        if (failed)
            _failedAtMs.Enqueue(nowMs);
        var lengthMs = (long)Length.TotalMilliseconds;
        while (_failedAtMs.Count > 0 && nowMs - _failedAtMs.Peek() >= lengthMs)
            _failedAtMs.Dequeue();
        return _failedAtMs.Count;
    }

    public void Clear() => _failedAtMs.Clear();
}
