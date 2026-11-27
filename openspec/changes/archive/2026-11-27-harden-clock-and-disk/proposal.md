# Proposal

## Why

Two gaps let the child stop the limits without admin rights (GitHub issues #37 and #36):

- **The time zone (#37).** Windows gives the privilege `SeTimeZonePrivilege` to the Users group. The service takes the day and the time of day from the local system time. After a time zone change and a reboot, the child gets a new day (new Allowance Time, new Category Limits, a new capture quota) or moves the Schedule Window. The Clock Tampering Detection compares UTC only, so it does not see the change.
- **A full disk (#36).** A standard user can fill the disk from its profile, because Windows sets no disk quota by default. Each tick then catches the write error and goes on. The tick writes the time before it checks the limits, so a failed write skips the checks: the Allowance Time and the Category Usage stop, and no lock applies.

## What Changes

- **Control Time Zone.** The service takes each day, weekday and time of day for a limit from one time zone that only an administrator sets. This includes the Allowance Time, the Schedule Window, the Category Limits, the Grace Requests, the capture quota and the usage rows. The system time zone has no effect on a limit. The value is in the admin-only registry key `HKLM\SOFTWARE\ParentalControl`. The service writes the current system time zone at its first start with this change. The parent changes it in the Admin app.
- **Time zone change by the child.** The service detects each change of the system time zone or of its daylight saving setting. It writes an event, and it alerts the parent at most once per day. The change has no effect on a limit.
- **Elapsed time from UTC.** The service counts elapsed time in UTC and with the monotonic clock. The end of daylight saving time is then not clock tampering. Before this change, the clock went back one hour, and the service locked the child for the rest of the day.
- **Storage Lockdown.** The tick keeps the counted time in memory (Pending Usage) when a write fails. Each limit check uses the stored time plus the Pending Usage. A failed write never skips a limit check or a lock. After 3 failed ticks in a row, the service locks each active session of a user who is not an administrator, as in the Data Folder Lockdown. It retries each minute, alerts the parent at most once per day, and leaves the Storage Lockdown after a successful write when enough free space exists. It never closes or kills a program.
- **No suspension without a record.** If the service cannot write the record of a new suspension, it does not suspend the process. The Storage Lockdown covers this gap.

## Out of scope

- A removal of `SeTimeZonePrivilege` from the Users group. The Control Time Zone makes the change harmless, and the removal changes a machine policy (design D4).
- An NTFS disk quota for the Restricted User at setup (design D9).
- Changes of the UTC system time. Windows does not give `SeSystemtimePrivilege` to the Users group, and the Clock Tampering Detection covers it.
- Timestamps that only show when something happened (event rows, log lines, Telegram texts). They stay in the system time zone. The end time of a Break or of the Schedule Window in a message is a limit time and uses the Control Time Zone.
- A setting of the Control Time Zone in the web admin. The web admin can show it in a later change.
- A message to the child about the Storage Lockdown. The Data Folder Lockdown shows none either.
- Authentication of the agent channel (issue #6).

## Capabilities

### New Capabilities
- `control-clock`: the Control Time Zone, the day and the time of day of every limit, the detection and the report of a time zone change, and elapsed time from UTC.
- `storage-failure`: the behaviour of the service when a write to the database fails at run time: Pending Usage, the order of lock and write, the Storage Lockdown, its end, and the parent alert.

### Modified Capabilities
- `admin-access`: "Quota for screen captures of the Session Agent" counts the day from midnight of the Control Time Zone, not from local midnight.
- `usage-breaks`: the end time of a Break in the child messages and the Telegram texts, and the cut at the Schedule Window end, use the Control Time Zone. A failed write does not stop the lock of a Break.

## Impact

- `src/ParentalControl.Domain`: new pure classes for the control clock, the time zone change report, the Pending Usage ledger and the Storage Lockdown state. New xUnit tests.
- `src/ParentalControl.Core`: a reader and writer of the registry value `ControlTimeZone` (with the trust check of `DataFolderRegistry`); `GetDynamicTimeZoneInformation`; `GetDiskFreeSpaceExW`; a new `EventType` value; the day keys in `GraceRequestRepository`, `ScheduleRepository` and `CategoryUsageRepository`.
- `src/ParentalControl.Service`: `UsageMonitorWorker` (tick order, failure count, Storage Lockdown loop), `SessionTracker` (UTC and monotonic elapsed time, day keys), `CategoryEnforcer` (day key, Pending Usage, no suspension without a record), `BreakEnforcer` (the Schedule Window end and the Break end texts in the control zone, lock before write), `WebServerHost` (activity, status, capture quota and request day keys), `RequestPage` (after #42: the `/request` handler and `RequestPageModel`).
- `TelegramBotService`: the Break texts take the control zone.
- `src/ParentalControl.Admin`: a Control Time Zone setting; "today" in `UserRow` and `UserDetailViewModel` from the control clock.
- No new table and no new column. New registry value `ControlTimeZone`, new registry value `LastSystemTimeZone`.
- `GLOSSARY.md`: Control Time Zone, Pending Usage, Storage Lockdown. `CONTEXT.md`: Allowance Time resets at midnight of the Control Time Zone. New ADR 0013.
