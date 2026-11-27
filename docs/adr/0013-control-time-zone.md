# 13. The Limits Use a Control Time Zone, Not the Windows Time Zone

## Status
Accepted (harden-clock-and-disk, issue #37).

## Context
Windows gives the privilege `SeTimeZonePrivilege` ("Change the time zone") to the Users group. The service took each day key and each time of day from the local system time (`DateTime.Now`). After a change of the time zone and a restart of the service, the child got a new day (new Allowance Time, new Category Limits, a new capture quota) or moved the Schedule Window. The Clock Tampering Detection compares UTC only, so it did not see the change.

The elapsed session time also came from the local time. At the end of daylight saving time, the local time goes back one hour, and the service took this as "clock went backwards" and locked the child for the rest of the day.

## Decision
1. **One Control Time Zone.** The service, the Admin app and the web routes take each day, weekday and time of day of a limit from one Windows time zone ID. This includes the end time of a Break and of the Schedule Window in a message. The daylight saving rules of that zone apply.
2. **An admin-only registry value.** The ID is the value `ControlTimeZone` in `HKLM\SOFTWARE\ParentalControl`, the key of the data folder location (decision D14 of require-admin-sign-in): owner Administrators, write access only for SYSTEM and the Administrators group, a trust check at each read. The service writes the system time zone at its first start and tells the parent. The parent changes it in the Admin app. The service reads it every 60 seconds.
3. **Elapsed time from UTC and the monotonic clock.** The minutes of a session come from `Environment.TickCount64`. The check "clock went back" compares UTC times.
4. **A time zone change is reported, not blocked.** The service writes an event for each change of the system time zone and alerts the parent at most once per day. It does not lock the session.
5. **The privilege stays.** The installer does not remove `SeTimeZonePrivilege` from the Users group.

### Rejected alternatives
- **A setting in the database.** A quarantine or a relocation of the data folder gives a new, empty database. The default would then be the system time zone at that moment, which the child controls, and the child can also cause the quarantine.
- **The installer writes the value.** An install made before this change has no installer step, and the service needs the code for the first start anyway.
- **UTC as the day.** The day of a family in Germany would start at 01:00 or 02:00 local time.
- **`TimeZoneInfo.ClearCachedData()` with the system time zone.** That makes a change take effect at once; it does not stop it.
- **Remove `SeTimeZonePrivilege` from the Users group.** It changes the local security policy for each standard user, the uninstaller must restore it, a domain or Intune policy can set it back, Windows 10 Home has no `secpol.msc`, and a family that travels must ask the parent for each change. With the Control Time Zone, the change gives the child nothing.
- **Lock the session at a time zone change.** The automatic time zone of Windows changes the zone on a trip, and that would lock the child without a reason.

## Consequences
- Each stored day key (`usage`, `category_usage`, `app_usage`, `grace_requests`, `schedule_exceptions`) and each time of the Weekly Schedule now means a day or a time of the Control Time Zone. At the first start the two zones are equal, so the existing rows stay valid. A rollback to an older version uses the system time zone again.
- The capture rows store the time of the Control Time Zone, so the daily quota and the retention cutoff use the same clock. Event rows, log lines and Telegram texts keep the system time; they only show when something happened.
- A family that moves must change the Control Time Zone in the Admin app. The Admin app shows a warning while the Windows time zone differs, and the parent gets an alert at each change.
- A change of the Control Time Zone during the day can move "today" to another date. That is an action of the parent; the usage of that date applies.
