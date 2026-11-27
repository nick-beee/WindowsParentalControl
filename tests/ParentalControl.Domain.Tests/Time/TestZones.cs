namespace ParentalControl.Domain.Tests.Time;

/// <summary>Time zones with fixed rules, so the tests do not depend on the time zone data of the machine.</summary>
internal static class TestZones
{
    /// <summary>"W. Europe Standard Time": UTC+1, daylight saving from the last Sunday of March 02:00 to the last Sunday of October 03:00.</summary>
    public static readonly TimeZoneInfo WEurope = TimeZoneInfo.CreateCustomTimeZone(
        "W. Europe Standard Time", TimeSpan.FromHours(1), "W. Europe", "W. Europe Standard", "W. Europe Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    public static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone(
        "Tokyo Standard Time", TimeSpan.FromHours(9), "Tokyo", "Tokyo Standard");

    public static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
}
