namespace ParentalControl.Domain.DataFolder;

/// <summary>What changed after one tick report.</summary>
/// <param name="Cause">At an entry: the last failure of the failed ticks, for the texts of the entry. Null if unknown.</param>
public readonly record struct StorageTickOutcome(bool Entered, bool Left, bool Alert, StorageFailure? Cause = null);

/// <summary>
/// The state of the Storage Lockdown (storage-failure; designs D8 of harden-clock-and-disk and D5, D6 of
/// harden-storage-lockdown). It enters the Storage Lockdown at <see cref="FailedTicksToEnter"/> failed ticks within
/// <see cref="FailedTickWindow.Length"/>, or at once if the probe write of the first tick after the start fails. A
/// successful tick does not clear the earlier failed ticks. It leaves when the retry of a tick (probe write and store of
/// the Pending Usage, no failed limit check) succeeds with no Pending Usage left and at least <see cref="MinFreeBytes"/>
/// free; the exit clears the failed ticks. A write that failed between two ticks, before the space was free, does not
/// keep the lockdown. It alerts the parent at the first entry of each day of the Control Time Zone, and again on the
/// same day at an entry with a known cause of another kind than the cause of the last alert. The state is in memory
/// only.
/// </summary>
public sealed class StorageLockdownState
{
    public const int FailedTicksToEnter = 3;
    public const long MinFreeBytes = 100L * 1024 * 1024;

    private readonly object _gate = new();
    private DateOnly? _alertDay;
    private StorageFailureKind? _alertKind;
    private readonly FailedTickWindow _window = new();

    public bool InLockdown { get; private set; }

    /// <summary>The failed ticks in the window after the last report.</summary>
    public int FailedTicksInWindow
    {
        get { lock (_gate) return _window.Count; }
    }

    /// <summary>The last failure that a report gave, until the exit. Null if none.</summary>
    public StorageFailure? LastFailure { get; private set; }

    /// <param name="tickFailed">A write since the last tick failed, or the count of the session time or a limit check of
    /// the tick failed.</param>
    /// <param name="retrySucceeded">The probe write and the store of the Pending Usage of this tick succeeded, and no
    /// limit check failed. In the Storage Lockdown, only this retry decides the exit.</param>
    /// <param name="pendingEmpty">The Pending Usage is empty after the store of the tick.</param>
    /// <param name="freeBytes">The free space of the volume of the data folder, or null if it is not known.</param>
    /// <param name="controlDay">The day of the Control Time Zone, for the alert.</param>
    /// <param name="nowMs">The time of the monotonic clock in milliseconds.</param>
    /// <param name="startProbeFailed">This is the first tick after the start of the service, and its probe write
    /// failed: enter at once.</param>
    /// <param name="failure">The last failure since the last tick, or null.</param>
    public StorageTickOutcome ReportTick(bool tickFailed, bool retrySucceeded, bool pendingEmpty, long? freeBytes,
        DateOnly controlDay, long nowMs, bool startProbeFailed = false, StorageFailure? failure = null)
    {
        lock (_gate)
        {
            var failed = tickFailed || startProbeFailed;
            var inWindow = _window.Report(failed, nowMs);
            if (failure is not null)
                LastFailure = failure;
            else if (!InLockdown && inWindow == 0)
                LastFailure = null; // no failed tick left in the window: an old cause would be wrong at the next entry

            if (InLockdown)
            {
                if (!retrySucceeded || !pendingEmpty || freeBytes is not { } free || free < MinFreeBytes)
                    return default;
                InLockdown = false;
                _window.Clear();
                LastFailure = null;
                return new StorageTickOutcome(Entered: false, Left: true, Alert: false);
            }

            if (!startProbeFailed && inWindow < FailedTicksToEnter)
                return default;
            InLockdown = true;
            return new StorageTickOutcome(Entered: true, Left: false, Alert: TakeAlert(controlDay, LastFailure?.Kind), LastFailure);
        }
    }

    /// <summary>
    /// True for the first entry of a day. On the same day, true only if the cause is known and its kind differs from the
    /// kind of the last alert, so a parent who freed space for a full disk learns about a new cause (review of PR #67,
    /// finding 5). An entry without a known cause does not alert again on the same day.
    /// </summary>
    private bool TakeAlert(DateOnly controlDay, StorageFailureKind? kind)
    {
        if (_alertDay == controlDay && (kind is null || kind == _alertKind))
            return false;
        _alertDay = controlDay;
        _alertKind = kind;
        return true;
    }
}
