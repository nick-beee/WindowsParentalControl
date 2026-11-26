namespace ParentalControl.Domain.Categories;

/// <summary>
/// A weekday and a weekend minute value. Saturday and Sunday use the weekend value.
/// For a Category Limit, null means "no limit".
/// For an App Limit, null means "the Category Limit applies on that day".
/// </summary>
public sealed record DailyLimit(int? WeekdayMinutes, int? WeekendMinutes)
{
    public const int MaxMinutes = 1440;

    public static DailyLimit None { get; } = new(null, null);

    public bool IsNone => WeekdayMinutes is null && WeekendMinutes is null;

    public int? MinutesOn(DateOnly date) => IsWeekend(date) ? WeekendMinutes : WeekdayMinutes;

    public static bool IsWeekend(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>True if each value is null or in the range 0 to 1440.</summary>
    public bool IsValid => IsValidMinutes(WeekdayMinutes) && IsValidMinutes(WeekendMinutes);

    public static bool IsValidMinutes(int? minutes) => minutes is null or (>= 0 and <= MaxMinutes);
}
