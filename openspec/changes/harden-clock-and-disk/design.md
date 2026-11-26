# Design

## Context

See proposal.md for the motivation and specs/ for the required behaviour.

Current state that shapes the approach:

- **Day keys.** The service takes the day and the time of day from `DateTime.Now`, `DateTime.Today` and `DateOnly.FromDateTime(DateTime.Now)`. .NET caches `TimeZoneInfo.Local` for the life of the process. So a time zone change takes effect at the next start of the service, and this explains "and a reboot" in issue #37. The call sites:
  - `UsageMonitorWorker`: `ProcessTick` (today, now, weekday) and `RunCleanupIfNeeded`.
  - `SessionTracker`: the logon check, the unlock check, `TickAllSessions`, `FlushSessionTime`, and `LastTick` of `ActiveSession`.
  - `CategoryEnforcer.Tick` (today).
  - `WebServerHost`: the activity route (App Usage and Category Usage day), the agent status and the remaining time (`DateTime.Now.TimeOfDay`, the elapsed seconds from `LastTick`), the capture quota (`DateTime.Today`), the request routes, the dashboard routes.
  - `RequestPage` (the min and max dates of a Schedule Change Request).
  - Core: `GraceRequestRepository` (once per day), `ScheduleRepository.GetEffectiveLimit` (default date), `AppUsageRepository` (hour bucket).
  - Admin app: `UserRow` and `UserDetailViewModel` (today, the date of a new Schedule Exception).
- **Elapsed time.** `SessionTracker` counts elapsed minutes from the local `DateTime.Now - LastTick`. At the end of daylight saving time, the local time goes back one hour. The branch "Clock went backwards" then adds the full `DailyMinutes` and locks the child for the day. This is a bug in the current code.
- **Clock Tampering Detection.** `UsageMonitorWorker` compares the UTC wall clock with `Environment.TickCount64`. It does not see a time zone change, because UTC does not change.
- **The tick and the writes.** `ProcessTick` calls `TickAllSessions` before the limit checks. `TickAllSessions` writes with `UsageRepository.AddMinutes`. If that write throws (`SQLITE_FULL`), the exception leaves `ProcessTick`, the catch in `RunAsync` logs it, and no limit check and no lock runs in that tick. `LastTick` does not move, so the next tick sees a gap of more than 2 minutes and adds only 1 minute. The limit branch writes `EventRepository.LogEvent` before `SessionManager.LockSession`, and the "Clock went backwards" branch writes `UsageRepository.AddMinutes` before its lock. `CategoryUsageRepository.AddSeconds` takes the whole minutes out of its accumulator before `AddMinutes`, so a failed write loses them. `CategoryEnforcer.Suspend` writes the record before `NtSuspendProcess`; a throw there leaves the category tick.
- **Reads on a full disk.** SQLite reads still work on a full disk. Only writes fail.
- **The existing lockdown.** `DataFolderLockdown.LockSessions` applies `SessionManager.LockSession` (`WTSDisconnectSession`) to each active session of a user who is not an administrator (`LockdownPolicy`, fail closed). `StartupWorker` uses it before the start gate (D14 and D15 of `require-admin-sign-in`). After the gate, nothing uses it.
- **The registry key.** `HKLM\SOFTWARE\ParentalControl` (64-bit view) has owner Administrators and a protected DACL: full control for SYSTEM and Administrators, read for Users (D14). `DataFolderRegistry` writes it, and readers check it with `DataFolderTrust`. A standard user cannot create or change a value in it.
- **Telegram.** On a failed send, `TelegramBotService` puts a message into the Offline Notification Queue (`PendingNotificationRepository.Enqueue`). That is a database write, so it fails on a full disk.

## Goals / Non-Goals

**Goals:**
- A time zone change by the child changes no day, no weekday and no time of day of a limit.
- The parent learns about each time zone change and each Storage Lockdown, without a flood of alerts.
- A failed write never loses counted time and never skips a lock.
- The day logic, the ledger and the lockdown state are pure code with xUnit tests on the Mac.

**Non-Goals:**
- A change of the UTC system time. The privilege `SeSystemtimePrivilege` ("Change the system time") goes by default only to Administrators and LOCAL SERVICE, not to Users. The existing Clock Tampering Detection covers it.
- A message to the child about the Storage Lockdown.
- The Admin app reads the database directly. It does not see the Pending Usage of the service, so it shows the stored time only.

## Decisions

All decisions below were decided by the orchestrator under the user's delegation (2026-11-26).

### D1. The Control Time Zone is a registry value
The Control Time Zone is the string value `ControlTimeZone` in `HKLM\SOFTWARE\ParentalControl` (64-bit view). It holds a Windows time zone ID, for example `W. Europe Standard Time`. The service writes it with the code of `DataFolderRegistry`, which sets the owner and the protected DACL of the key on each write. Each reader checks the key with `DataFolderTrust`, as for `DataFolderPath`.
- If the value is absent at a start, the service reads the system time zone (D5) and writes it. It writes an event and sends one Telegram message to the parent with the name of the zone and the place to change it. If the send fails, the message goes into the Offline Notification Queue; at this point the database works. The message goes only when the service itself writes the value, so a later start does not send it again.
- If the key is not trusted or the ID is not known to Windows (`TimeZoneInfo.FindSystemTimeZoneById` throws), the service uses the last valid zone of this process, else the system time zone. It writes event 1017 and alerts the parent at most once per control day. It does not write the system zone over a value that is present but invalid; the parent fixes it in the Admin app.
- The Admin app runs elevated (`requireAdministrator`), so it can write the value.
- *Alternative: a setting in the `settings` table.* Rejected. A quarantine or a relocation of the data folder gives a new, empty database. The setting is then absent, and the default is the system time zone at that moment. The child controls that zone, and the child can also cause the quarantine.
- *Alternative: the installer writes the value.* Rejected. The update path needs the service code anyway, and an install made before this change has no installer step. The two are at about the same moment, so the installer adds no protection.

### D2. One control clock for every day key
New pure class `ControlClock` in `src/ParentalControl.Domain/Time`. It takes a `TimeZoneInfo` and a source of the UTC time. It gives `Now` (the local time in the control zone), `Today` (`DateOnly`), `TimeOfDay` (`TimeOnly`) and `DayOfWeek`, from `TimeZoneInfo.ConvertTime`. `TimeZoneInfo` applies the daylight saving rules of the zone. Windows stores these rules under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones`, which only administrators can change.
- The service has one provider of the current `ControlClock`. It reads the registry value at the start and at each tick (60 seconds), so a change in the Admin app takes effect within 60 seconds.
- Each call site in the Context list takes the day, the weekday and the time of day from the control clock. `ScheduleRepository.GetEffectiveLimit` and `GraceRequestRepository` get the date from the caller and lose their `DateTime.Now` default. The Admin app reads the same registry value and uses the same class.
- Timestamps that only show a time (event rows, log lines, Telegram texts, `CreatedAt`) stay as they are. They have no effect on a limit.
- *Alternative: use UTC for the day.* Rejected. The day of a family in Germany would start at 01:00 or 02:00 local time.
- *Alternative: call `TimeZoneInfo.ClearCachedData()` and keep the system time zone.* Rejected. That makes the change take effect at once, not prevent it.

### D3. Elapsed time from the monotonic clock
`ActiveSession` keeps `LastTickUtc` and `LastTickMono` (`Environment.TickCount64`) instead of the local `LastTick`. The elapsed minutes come from the monotonic difference. The check "Clock went backwards" compares UTC times only. The routes that show the elapsed seconds of the current minute use UTC too.
- So the end of daylight saving time adds no hour and does not lock the child, and the start of daylight saving time is not a gap of 60 minutes.
- `GetTickCount64` includes the time of sleep and hibernation. The existing rule "a gap of more than 2 minutes adds 1 minute" stays for that case.

### D4. No change of `SeTimeZonePrivilege`
The installer does not remove `SeTimeZonePrivilege` from the Users group.
- With D1 to D3, a time zone change gives the child nothing, so the removal adds only less noise in the event log.
- Side effects of the removal (`LsaRemoveAccountRights` on `S-1-5-32-545`): it changes the local security policy for each standard user of the PC; the uninstaller must restore it, and a failed uninstall leaves it; a domain or Intune policy can set it back; Windows 10 Home has no `secpol.msc`, so the parent cannot see or undo it easily; a family that travels must ask the parent for each time zone change.
- *Alternative: remove the privilege as defence in depth.* Rejected for the side effects above.

### D5. Detection of a time zone change
At each tick and at each start, the service calls `GetDynamicTimeZoneInformation`. It reads `TimeZoneKeyName` and `DynamicDaylightTimeDisabled`. It compares the pair with the last seen pair. The last seen pair is in memory and in the new registry value `LastSystemTimeZone` (`<key name>|<0 or 1>`) in the same key, so a change before a reboot is seen at the next start.
- New pure class `TimeZoneChangeReport` in Domain: it takes the last seen pair, the current pair and the control day, and returns "write an event" and "send an alert". It allows one alert per control day.
- On a change, the service writes an event with the new `EventType.TIME_ZONE_CHANGE` (old and new value), writes the registry value, and sends a Telegram alert if the report says so. The alert day is in memory. After a restart of the service, one more alert on the same day is possible; this is accepted.
- The same call gives the system time zone for D1 and for the Admin app.
- *Alternative: listen for `WM_TIMECHANGE`.* Rejected. The service runs in session 0 without a window. A change has no effect on a limit, so a poll every 60 seconds is fast enough.
- *Alternative: treat a time zone change as clock tampering and lock the session.* Rejected. The automatic time zone of Windows changes the zone on a trip, and that would lock the child without a reason.

### D6. Pending Usage
New pure class `PendingUsage` in `src/ParentalControl.Domain/Usage`. It holds the minutes of Allowance Time per (user, day) and the minutes of Category Usage per (user, day, category) that the database did not store yet.
- A service class `UsageLedger` wraps the writes. `AddAllowance` and `AddCategory` first add the time to `PendingUsage`, then try to store all pending entries. An entry leaves `PendingUsage` only after its write returned without an error. Each write is one `UPSERT` statement, so a returned write is committed, and no minute counts two times.
- `CategoryUsageRepository.AddSeconds` keeps its accumulator of seconds. The whole minutes go to the ledger, not straight to `AddMinutes`, so a failed write keeps them.
- Readers add the pending time to the stored time: the limit check of `UsageMonitorWorker`, `CategoryEnforcer` (in place of `CategoryUsageRepository.GetForUserAndDate`), the agent status and the remaining time in `WebServerHost`, and the request page.
- The activity route of the agent puts its Category Usage through the ledger. A failed write of App Usage (the Activity Timeline) is lost; it has no effect on a limit.
- No new table and no new column. The Pending Usage is in memory. A restart of the service loses it. The Storage Lockdown (D8) limits this loss to the time of at most 3 ticks, because a locked session adds no time.
- *Alternative: write the pending time to a file.* Rejected. The disk is full.

### D7. The order of the tick
`ProcessTick` gets this order:
1. Sync the sessions and run the clock watchdog (its event write in its own `try`).
2. Count the elapsed time of each session into the ledger. This step does not throw on a write error.
3. Run the limit and Schedule Window checks of each session, with the stored time plus the Pending Usage. In each lock path, the order is: the message to the child, `SessionManager.LockSession`, `OnSessionLock`, then the writes (event, log). Each write is in its own `try`.
4. Run `CategoryEnforcer.Tick`. In `Suspend`, a failed `SuspensionRepository.Insert` skips this one suspension, marks the write failure, and the loop goes on with the next action.
5. Report the result of the tick to the Storage Lockdown state (D8).
- Each write path reports its failure to one `StorageHealth` object of the service: the ledger, the event writes, the suspension records, the activity route and the probe write. Any write exception counts, not only `SQLITE_FULL`. A tick in which a read for a limit check fails counts as a failed tick too. This fails closed. A database that is busy for 3 minutes in a row is not normal either.
- The "Clock went backwards" branch locks before its write.
- *Alternative: count only `SQLITE_FULL` and `SQLITE_IOERR`.* Rejected. Another error that stops the writes also stops the time count, and the child gains the same.

### D8. Storage Lockdown
New pure class `StorageLockdownState` in `src/ParentalControl.Domain/DataFolder`. It counts the failed ticks in a row. It enters the Storage Lockdown at 3. It leaves it when a probe write and the store of all Pending Usage succeed and the free space is at least 100 MB. It returns "alert" at the first entry of each control day.
- While the state is in the Storage Lockdown, `UsageMonitorWorker` runs a loop that calls `DataFolderLockdown.LockSessions()` every 2 seconds, as in D14. The lock selects each active session of a user who is not an administrator (`LockdownPolicy`, fail closed). The service never closes or kills a program.
- The normal tick goes on every 60 seconds and is the retry. It also writes a probe: the setting `storage_probe` in the existing `settings` table, with the UTC time. The free space comes from `GetDiskFreeSpaceExW` on the data folder. The limit of 100 MB stops a fast change between lockdown and normal when a small write still fits into free pages of the database file.
- A write failure never calls `DataFolderLockdown.RestartService`. A restart loses the Pending Usage, and the start on a full disk then runs into the start lockdown of CB2 anyway.
- Windows event log: event 1015 at the entry, event 1016 at the exit. These do not need the database.
- The Telegram alert uses a new send method that does not use the Offline Notification Queue. On a failed send, the alert stays in memory, and the service tries again every minute while it runs. The text tells the parent that the disk is full, that each session of a child is locked, and that an administrator must free space.
- The tick still checks the time zone (D5) and the control clock (D2) in the Storage Lockdown.
- *Alternative: lock only the sessions of Restricted Users.* Rejected. The reads can fail too, and the same rule as in the Data Folder Lockdown is simpler and fails closed.
- *Alternative: no lock, only Pending Usage.* Rejected. A reboot loses the Pending Usage, so the child could play, restart, and lose the counted time again and again. Other writes fail too (Grace Requests, suspension records).
- *Alternative: enter the lockdown at once at the first failure.* Rejected. A single short error (a virus scanner, a backup) would lock the child. The Pending Usage keeps the 3 ticks exact.

### D9. No NTFS disk quota in this change
The setup does not set an NTFS disk quota for the Restricted User.
- Side effects of a quota (`IDiskQuotaControl` or `fsutil quota`): it turns on quota tracking for the whole volume; the limit must fit each disk size and the needs of the child (a game can need 50 GB); a hit gives "disk full" errors in the games and the school programs of the child, which look like a bug; files that a service writes for the child (Windows Update, a game store service, Delivery Optimization) have another owner and do not count; the uninstaller must turn it off.
- With D6 to D8, a full disk gives the child no time, only a locked session. A quota would only make the lock less frequent.
- *Alternative: an opt-in quota at setup.* Deferred to a later change, if the parent wants it.

### D10. Terms, ADR and event types
- `GLOSSARY.md` gets Control Time Zone, Pending Usage and Storage Lockdown. `CONTEXT.md`: the Allowance Time resets at midnight of the Control Time Zone; the Clock Tampering Detection entry names the time zone report.
- ADR 0011 records the Control Time Zone. It changes the meaning of each stored day key and of each time in the Weekly Schedule, so it is hard to reverse.
- New `EventType` values: `TIME_ZONE_CHANGE` and `STORAGE_LOCKDOWN`. Windows event IDs 1015 (Storage Lockdown entry), 1016 (exit), 1017 (Control Time Zone unknown or untrusted).

### WinAPI calls and stored values
- New WinAPI calls: `GetDynamicTimeZoneInformation` (kernel32), `GetDiskFreeSpaceExW` (kernel32).
- Existing calls used in a new place: `WTSDisconnectSession` through `SessionManager.LockSession` and `DataFolderLockdown.LockSessions`; `NtSuspendProcess` (order unchanged: record first).
- New registry values in `HKLM\SOFTWARE\ParentalControl`: `ControlTimeZone` (REG_SZ), `LastSystemTimeZone` (REG_SZ).
- New setting key in the existing `settings` table: `storage_probe`. No new table and no new column.

## Risks / Trade-offs

- [The family moves or travels, and the parent forgets to change the Control Time Zone] → The Admin app shows a warning while the system zone differs. The time zone alert tells the parent about each change.
- [The parent changes the Control Time Zone during the day, so "today" moves to another date] → This is an action of the parent. The usage of the other date applies. The Admin app says this next to the setting.
- [The value is set at the first start after the update, and the child changed the system time zone before the update] → The parent gets a message with the zone at the first start and can correct it.
- [A restart of the service loses the Pending Usage] → At most 3 ticks of time, because the Storage Lockdown locks after 3 failed ticks and a locked session adds no time.
- [A busy database for 3 minutes locks the child without a full disk] → This fails closed. The event log and the alert show it, and the lock ends after the next successful write.
- [After a restart of the service, a second Telegram alert on the same day is possible] → Accepted; the alert state is in memory, because the database cannot store it on a full disk.
- [The Admin app shows less time than the service during a write failure] → It reads the database only. The service shows the right remaining time to the child.
- [A suspension does not start during a write failure] → The Storage Lockdown locks the session after at most 3 ticks.

## Migration Plan

- The update needs no database migration. At the first start, the service writes `ControlTimeZone` from the system time zone and `LastSystemTimeZone` from the same call. The day keys of the existing rows stay valid, because the two zones are equal at that moment.
- Rollback: an older version ignores the two registry values and goes back to the system time zone.
