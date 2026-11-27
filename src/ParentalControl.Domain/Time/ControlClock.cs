namespace ParentalControl.Domain.Time;

/// <summary>
/// The clock of the limits (control-clock, design D2). It gives the day, the weekday and the time of day in the
/// Control Time Zone, with the daylight saving rules of that zone. The system time zone has no effect on it.
/// A local value of this clock has <see cref="DateTimeKind.Unspecified"/>: never call <c>ToUniversalTime()</c> on it.
/// </summary>
public sealed class ControlClock
{
    private readonly Func<DateTime> _utcNow;

    public ControlClock(TimeZoneInfo zone, Func<DateTime>? utcNow = null)
    {
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The Control Time Zone.</summary>
    public TimeZoneInfo Zone { get; }

    public DateTime UtcNow => DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);

    /// <summary>One reading of the clock. Use it when a call site needs the day and the time of the same instant.</summary>
    public ControlMoment Read()
    {
        var utc = UtcNow;
        return new ControlMoment(utc, ToControl(utc));
    }

    /// <summary>The current time in the Control Time Zone (kind Unspecified).</summary>
    public DateTime Now => Read().Local;

    public DateOnly Today => Read().Today;

    public TimeOnly TimeOfDay => Read().TimeOfDay;

    public DayOfWeek DayOfWeek => Read().DayOfWeek;

    /// <summary>Midnight of the current control day (kind Unspecified).</summary>
    public DateTime StartOfToday => Today.ToDateTime(TimeOnly.MinValue);

    /// <summary>Converts a UTC instant to the time of the Control Time Zone (kind Unspecified).</summary>
    public DateTime ToControl(DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone), DateTimeKind.Unspecified);

    /// <summary>
    /// Converts a date and a time of the Control Time Zone to UTC. A time in the gap at the start of daylight saving
    /// time moves forward by the gap. A time in the overlap at its end gives the earlier instant, so an end time
    /// never comes late.
    /// </summary>
    public DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (Zone.IsAmbiguousTime(local))
        {
            var largest = Zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - largest, DateTimeKind.Utc);
        }

        if (Zone.IsInvalidTime(local))
        {
            // The offset before the gap: the last valid minute before the local time.
            var before = local;
            for (var i = 0; i < 24 * 60 && Zone.IsInvalidTime(before); i++)
                before = before.AddMinutes(-1);
            return DateTime.SpecifyKind(local - Zone.GetUtcOffset(before), DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}

/// <summary>One instant: the UTC time and the time of the Control Time Zone.</summary>
public readonly record struct ControlMoment(DateTime Utc, DateTime Local)
{
    public DateOnly Today => DateOnly.FromDateTime(Local);
    public TimeOnly TimeOfDay => TimeOnly.FromDateTime(Local);
    public DayOfWeek DayOfWeek => Local.DayOfWeek;
}
