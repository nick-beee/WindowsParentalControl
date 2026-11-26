using ParentalControl.Domain.Time;

namespace ParentalControl.Domain.DataFolder;

/// <summary>What changed after one tick report.</summary>
public readonly record struct StorageTickOutcome(bool Entered, bool Left, bool Alert);

/// <summary>
/// The state of the Storage Lockdown (storage-failure, design D8). It counts the failed ticks in a row and enters the
/// Storage Lockdown at <see cref="FailedTicksToEnter"/>. It leaves only after a successful tick (probe write and store
/// of the Pending Usage) with no Pending Usage left and at least <see cref="MinFreeBytes"/> free. It alerts the
/// parent at the first entry of each day of the Control Time Zone. The state is in memory only.
/// </summary>
public sealed class StorageLockdownState
{
    public const int FailedTicksToEnter = 3;
    public const long MinFreeBytes = 100L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly OncePerDay _alerts = new();

    public bool InLockdown { get; private set; }

    public int FailedTicksInRow { get; private set; }

    /// <param name="tickFailed">A write of the tick failed, or a read for a limit check failed.</param>
    /// <param name="pendingEmpty">The Pending Usage is empty after the store of the tick.</param>
    /// <param name="freeBytes">The free space of the volume of the data folder, or null if it is not known.</param>
    public StorageTickOutcome ReportTick(bool tickFailed, bool pendingEmpty, long? freeBytes, DateOnly controlDay)
    {
        lock (_gate)
        {
            if (tickFailed)
            {
                FailedTicksInRow++;
                if (InLockdown || FailedTicksInRow < FailedTicksToEnter)
                    return default;
                InLockdown = true;
                return new StorageTickOutcome(Entered: true, Left: false, Alert: _alerts.TryTake(controlDay));
            }

            FailedTicksInRow = 0;
            if (InLockdown && pendingEmpty && freeBytes is { } free && free >= MinFreeBytes)
            {
                InLockdown = false;
                return new StorageTickOutcome(Entered: false, Left: true, Alert: false);
            }
            return default;
        }
    }
}
