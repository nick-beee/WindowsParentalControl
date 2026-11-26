using ParentalControl.Core.Platform;
using ParentalControl.Domain.Time;

namespace ParentalControl.Core;

/// <summary>
/// The current clock of the limits for the service and the Admin app (control-clock, design D2). The service sets it
/// at the start and at each tick from the registry value <c>ControlTimeZone</c>. Before that, it uses the system time
/// zone. Each day key, weekday and time of day of a limit comes from here, not from <c>DateTime.Now</c>.
/// </summary>
public static class ControlTime
{
    private static volatile ControlClock _current = new(TimeZoneInfo.Local);

    public static ControlClock Current => _current;

    /// <summary>The Control Time Zone.</summary>
    public static TimeZoneInfo Zone => _current.Zone;

    /// <summary>Sets the clock (the service, the Admin app and the tests).</summary>
    public static void Set(ControlClock clock) => _current = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Uses <paramref name="zone"/> with the real UTC clock. Keeps the current clock if the zone is the same.</summary>
    public static void Use(TimeZoneInfo zone)
    {
        if (!string.Equals(_current.Zone.Id, zone.Id, StringComparison.OrdinalIgnoreCase))
            _current = new ControlClock(zone);
    }

    /// <summary>
    /// The zone for a reader that does not write the value (the Admin app): the stored zone if the key is trusted and
    /// Windows knows the ID, else the system time zone.
    /// </summary>
    public static TimeZoneInfo ReadZone(DataFolderRegistry registry)
    {
        var system = SystemTimeZone.CurrentZone();
        var decision = ControlZoneChoice.Choose(registry.ReadControlTimeZone(), null, system, SystemTimeZone.Find);
        return decision.Zone;
    }
}
