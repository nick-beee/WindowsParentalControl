using ParentalControl.Domain.Time;

namespace ParentalControl.Domain.DataFolder;

/// <summary>What changed after one tick report.</summary>
public readonly record struct StorageTickOutcome(bool Entered, bool Left, bool Alert);

/// <summary>
/// The state of the Storage Lockdown (storage-failure, design D8). It counts the failed ticks in a row and enters the
/// Storage Lockdown at <see cref="FailedTicksToEnter"/>. It leaves when the retry of a tick (probe write and store of
/// the Pending Usage) succeeds with no Pending Usage left and at least <see cref="MinFreeBytes"/> free. A write that
/// failed between two ticks, before the space was free, does not keep the lockdown. It alerts the parent at the first
/// entry of each day of the Control Time Zone. The state is in memory only.
/// </summary>
public sealed class StorageLockdownState
{
    public const int FailedTicksToEnter = 3;
    public const long MinFreeBytes = 100L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly OncePerDay _alerts = new();

    public bool InLockdown { get; private set; }

    public int FailedTicksInRow { get; private set; }

    /// <param name="tickFailed">A write since the last tick failed, or a read for a limit check failed.</param>
    /// <param name="retrySucceeded">The probe write and the store of the Pending Usage of this tick succeeded, and no
    /// read for a limit check failed. In the Storage Lockdown, only this retry decides the exit.</param>
    /// <param name="pendingEmpty">The Pending Usage is empty after the store of the tick.</param>
    /// <param name="freeBytes">The free space of the volume of the data folder, or null if it is not known.</param>
    public StorageTickOutcome ReportTick(bool tickFailed, bool retrySucceeded, bool pendingEmpty, long? freeBytes, DateOnly controlDay)
    {
        lock (_gate)
        {
            FailedTicksInRow = tickFailed ? FailedTicksInRow + 1 : 0;

            if (InLockdown)
            {
                if (!retrySucceeded || !pendingEmpty || freeBytes is not { } free || free < MinFreeBytes)
                    return default;
                InLockdown = false;
                FailedTicksInRow = 0;
                return new StorageTickOutcome(Entered: false, Left: true, Alert: false);
            }

            if (FailedTicksInRow < FailedTicksToEnter)
                return default;
            InLockdown = true;
            return new StorageTickOutcome(Entered: true, Left: false, Alert: _alerts.TryTake(controlDay));
        }
    }
}
