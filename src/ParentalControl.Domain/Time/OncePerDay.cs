namespace ParentalControl.Domain.Time;

/// <summary>Allows one action per day of the Control Time Zone. The state is in memory only.</summary>
public sealed class OncePerDay
{
    private readonly object _gate = new();
    private DateOnly? _day;

    /// <summary>True for the first call on <paramref name="day"/>; false for each later call on the same day.</summary>
    public bool TryTake(DateOnly day)
    {
        lock (_gate)
        {
            if (_day == day) return false;
            _day = day;
            return true;
        }
    }
}
