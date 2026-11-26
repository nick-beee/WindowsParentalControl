namespace ParentalControl.Domain.Time;

/// <summary>The registry value <c>ControlTimeZone</c> as the service read it.</summary>
/// <param name="StoredId">The value, or null if it is absent.</param>
/// <param name="KeyError">Null if the key is trusted (or absent), else the reason why it is not trusted.</param>
public sealed record ControlZoneReading(string? StoredId, string? KeyError);

/// <summary>The zone to use and what else the service must do (design D1).</summary>
/// <param name="WriteValue">Write <see cref="Zone"/> as the new value (only when the value is absent).</param>
/// <param name="FirstStartMessage">Tell the parent that the service set the Control Time Zone.</param>
/// <param name="Error">Not null if the stored value cannot be used: write event 1017 and alert the parent.</param>
public sealed record ControlZoneDecision(TimeZoneInfo Zone, bool WriteValue, bool FirstStartMessage, string? Error);

/// <summary>The choice of the Control Time Zone at a start and at each tick (design D1).</summary>
public static class ControlZoneChoice
{
    /// <param name="lastValid">The last valid zone of this process, or null.</param>
    /// <param name="system">The current system time zone.</param>
    /// <param name="find">Finds a Windows time zone by its ID; null if Windows does not know it.</param>
    public static ControlZoneDecision Choose(ControlZoneReading reading, TimeZoneInfo? lastValid, TimeZoneInfo system,
        Func<string, TimeZoneInfo?> find)
    {
        if (reading.KeyError is { } keyError)
            return new ControlZoneDecision(lastValid ?? system, false, false,
                "The registry key of Parental Control is not trusted: " + keyError);

        if (string.IsNullOrWhiteSpace(reading.StoredId))
            return new ControlZoneDecision(system, WriteValue: true, FirstStartMessage: true, null);

        var id = reading.StoredId.Trim();
        if (find(id) is { } zone)
            return new ControlZoneDecision(zone, false, false, null);

        return new ControlZoneDecision(lastValid ?? system, false, false,
            $"The Control Time Zone \"{id}\" is not a time zone of Windows.");
    }
}
