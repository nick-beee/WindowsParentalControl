using System.Diagnostics;
using ParentalControl.Core;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Time;

namespace ParentalControl.Service;

/// <summary>
/// Keeps <see cref="ControlTime.Current"/> on the Control Time Zone (control-clock, design D1 and D5).
/// <see cref="Refresh"/> reads the registry value at the start and at each tick, so a change in the Admin app takes
/// effect within 60 seconds. <see cref="CheckSystemZone"/> reports each change of the system time zone. A time zone
/// change of the child has no effect on a limit.
/// </summary>
public sealed class ControlTimeService
{
    /// <summary>Windows event ID: the Control Time Zone is unknown or the key is not trusted.</summary>
    public const int UnknownZoneEventId = 1017;

    /// <summary>Windows event ID: the service set the Control Time Zone at its first start with this change.</summary>
    public const int ZoneSetEventId = 1018;

    private readonly DataFolderRegistry _registry;
    private readonly ParentAlertOutbox _alerts;
    private readonly Serilog.ILogger _logger;
    private readonly Func<SystemZoneSetting?> _readSystem;
    private readonly Func<string, TimeZoneInfo?> _find;
    private readonly Action<int, string> _writeWindowsEvent;
    private readonly StorageHealth _health;
    private readonly object _gate = new();

    private TimeZoneInfo? _lastValid;
    private string? _lastError;
    private readonly OncePerDay _errorAlerts = new();
    private readonly TimeZoneChangeReport _report = new();
    private SystemZoneSetting? _lastSeen;
    private bool _lastSeenLoaded;

    public ControlTimeService(ParentAlertOutbox alerts, Serilog.ILogger logger)
        : this(DataFolderRegistry.Default, alerts, logger, SystemTimeZone.Read, SystemTimeZone.Find,
            (id, text) => DataFolderStartup.WriteEvent(id == ZoneSetEventId ? EventLogEntryType.Information : EventLogEntryType.Warning, id, text), StorageHealth.Shared) { }

    public ControlTimeService(DataFolderRegistry registry, ParentAlertOutbox alerts, Serilog.ILogger logger,
        Func<SystemZoneSetting?> readSystem, Func<string, TimeZoneInfo?> find, Action<int, string> writeWindowsEvent,
        StorageHealth health)
    {
        _registry = registry;
        _alerts = alerts;
        _logger = logger;
        _readSystem = readSystem;
        _find = find;
        _writeWindowsEvent = writeWindowsEvent;
        _health = health;
    }

    private TimeZoneInfo SystemZone() =>
        _readSystem() is { } setting && _find(setting.KeyName) is { } zone ? zone : TimeZoneInfo.Local;

    /// <summary>Reads the Control Time Zone and sets <see cref="ControlTime.Current"/>. Never throws.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            try
            {
                var decision = ControlZoneChoice.Choose(_registry.ReadControlTimeZone(), _lastValid, SystemZone(), _find);
                var zone = decision.Zone;

                if (decision.WriteValue)
                {
                    try
                    {
                        _registry.WriteControlTimeZone(zone.Id);
                        _logger.Information("Control Time Zone set to the system time zone {Zone}", zone.Id);
                        if (decision.FirstStartMessage)
                        {
                            _writeWindowsEvent(ZoneSetEventId, $"Parental Control set the Control Time Zone to \"{zone.Id}\". The limits use this zone. Change it in Parental Control Admin, Settings.");
                            _alerts.Post($"Parental Control on {Environment.MachineName}: the limits now use the time zone \"{zone.Id}\" ({zone.DisplayName}). " +
                                         "A change of the Windows time zone has no effect on the limits. To change the zone, open Parental Control Admin, Settings, as an administrator.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // The zone is still used in this process. The next tick tries the write again.
                        _logger.Error(ex, "Could not write the Control Time Zone {Zone}", zone.Id);
                    }
                }

                if (decision.Error is { } error)
                    ReportUnknownZone(error, zone);
                else
                {
                    _lastValid = zone;
                    _lastError = null;
                }

                if (!string.Equals(ControlTime.Zone.Id, zone.Id, StringComparison.OrdinalIgnoreCase))
                    _logger.Information("Control Time Zone: {Zone} ({Name})", zone.Id, zone.DisplayName);
                ControlTime.Use(zone);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error while reading the Control Time Zone; the last zone stays in use");
            }
        }
    }

    private void ReportUnknownZone(string error, TimeZoneInfo used)
    {
        if (_lastError != error)
        {
            _lastError = error;
            var text = $"{error} Parental Control uses \"{used.Id}\". Set the Control Time Zone in Parental Control Admin.";
            _logger.Warning(text);
            _writeWindowsEvent(UnknownZoneEventId, text);
        }

        var day = new ControlClock(used).Today;
        if (_errorAlerts.TryTake(day))
            _alerts.Post($"Parental Control on {Environment.MachineName}: {error} The limits use \"{used.Id}\" now. " +
                         "Open Parental Control Admin, Settings, as an administrator and select the time zone.");
    }

    /// <summary>
    /// Compares the system time zone with the last seen one. On a change: an event row, the registry value, and a parent
    /// alert at most once per control day. Never throws. Call it after the database is open.
    /// </summary>
    public void CheckSystemZone()
    {
        lock (_gate)
        {
            try
            {
                if (_readSystem() is not { } current)
                    return;

                if (!_lastSeenLoaded)
                {
                    _lastSeen = _registry.ReadLastSystemTimeZone();
                    _lastSeenLoaded = true;
                }

                var last = _lastSeen;
                var outcome = _report.Check(last, current, ControlTime.Current.Today);
                _lastSeen = current;

                if (last is null)
                {
                    _registry.TryWriteLastSystemTimeZone(current);
                    return;
                }

                if (!outcome.WriteEvent)
                    return;

                var detail = $"System time zone changed: {last} -> {current}. The limits use the Control Time Zone {ControlTime.Zone.Id}.";
                _logger.Warning(detail);
                // The registry value moves only after the event row is stored, so a change on a full disk is reported
                // again at the next start.
                if (_health.TryWrite(() => EventRepository.LogEvent("SYSTEM", EventType.TIME_ZONE_CHANGE, detail), "time zone change event", _logger))
                    _registry.TryWriteLastSystemTimeZone(current);

                if (outcome.SendAlert)
                    _alerts.Post($"Parental Control on {Environment.MachineName}: the Windows time zone changed from \"{last.Value.KeyName}\" to \"{current.KeyName}\". " +
                                 $"The limits still use the Control Time Zone \"{ControlTime.Zone.Id}\". If the family did not travel, the child changed it.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error while checking the system time zone");
            }
        }
    }
}
