using System.Globalization;

namespace ParentalControl.Domain.Breaks;

/// <summary>
/// The placeholder values of the break texts (<c>msg_break_warn</c>, <c>msg_break_start</c>, <c>msg_break_running</c>).
/// <c>{break_end}</c> is the local time of the end of the Break as <c>HH:mm</c>. <c>{minutes}</c> is rounded up.
/// </summary>
public static class BreakTexts
{
    public static string EndText(DateTime endUtc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), zone)
            .ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The values of the warning: the minutes of use before the Break.</summary>
    public static Dictionary<string, string> WarnValues(int minutesLeft) => new(StringComparer.Ordinal)
    {
        ["minutes"] = minutesLeft.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The values of the start message: the length of the Break and its end.</summary>
    public static Dictionary<string, string> StartValues(DateTime startUtc, DateTime endUtc, TimeZoneInfo zone) => new(StringComparer.Ordinal)
    {
        ["minutes"] = BreakPlanner.MinutesLeft(startUtc, endUtc).ToString(CultureInfo.InvariantCulture),
        ["break_end"] = EndText(endUtc, zone),
    };

    /// <summary>The values of the running message: the end of the Break and the remaining minutes (at least 1).</summary>
    public static Dictionary<string, string> RunningValues(DateTime nowUtc, DateTime endUtc, TimeZoneInfo zone) => new(StringComparer.Ordinal)
    {
        ["minutes"] = Math.Max(1, BreakPlanner.MinutesLeft(nowUtc, endUtc)).ToString(CultureInfo.InvariantCulture),
        ["break_end"] = EndText(endUtc, zone),
    };
}
