using System.Globalization;

namespace ParentalControl.Domain.Time;

/// <summary>
/// The setting of the system time zone that the service compares (design D5): the key name of the zone and the flag
/// "Adjust for daylight saving time automatically" off (<c>DynamicDaylightTimeDisabled</c>).
/// </summary>
public readonly record struct SystemZoneSetting(string KeyName, bool DaylightSavingDisabled)
{
    /// <summary>The stored form: <c>&lt;key name&gt;|&lt;0 or 1&gt;</c>.</summary>
    public string Format() => KeyName + "|" + (DaylightSavingDisabled ? "1" : "0");

    public static SystemZoneSetting? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bar = text.LastIndexOf('|');
        if (bar <= 0 || bar == text.Length - 1) return null;
        var flag = text[(bar + 1)..];
        if (flag != "0" && flag != "1") return null;
        return new SystemZoneSetting(text[..bar], flag == "1");
    }

    public override string ToString() =>
        DaylightSavingDisabled
            ? string.Create(CultureInfo.InvariantCulture, $"{KeyName} (daylight saving off)")
            : KeyName;
}

/// <summary>What the service does after a check of the system time zone.</summary>
public readonly record struct TimeZoneChangeOutcome(bool WriteEvent, bool SendAlert)
{
    public static TimeZoneChangeOutcome None => default;
}

/// <summary>
/// Decides about a change of the system time zone (design D5): an event for each change, a parent alert at most once
/// per day of the Control Time Zone. A change has no effect on a limit.
/// </summary>
public sealed class TimeZoneChangeReport
{
    private readonly OncePerDay _alerts = new();

    /// <param name="last">The last seen setting, or null at the first start (then there is nothing to compare).</param>
    public TimeZoneChangeOutcome Check(SystemZoneSetting? last, SystemZoneSetting current, DateOnly controlDay)
    {
        if (last is null) return TimeZoneChangeOutcome.None;
        if (string.Equals(last.Value.KeyName, current.KeyName, StringComparison.OrdinalIgnoreCase) &&
            last.Value.DaylightSavingDisabled == current.DaylightSavingDisabled)
            return TimeZoneChangeOutcome.None;
        return new TimeZoneChangeOutcome(WriteEvent: true, SendAlert: _alerts.TryTake(controlDay));
    }
}
