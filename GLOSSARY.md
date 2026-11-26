# Glossary

## User Sign-In (Logon)
A Windows interactive session logon event (`OnUserLogon`) initiated when a user authenticates at the Windows logon screen and enters a new interactive desktop session. Does not include workstation unlock events.

## User Sign-Out (Logoff)
A Windows interactive session termination event (`OnUserLogoff`) initiated when a user explicitly signs out of Windows, closes all running applications, and destroys the active session. Does not include workstation lock events.

## Restricted User
A managed Windows user account subject to daily screen time quotas, scheduled curfew boundaries, and forced session locks enforced by Parental Control Service.

## Admin / Unrestricted User
A Windows user account with administrative privileges or unrestricted status that bypasses quota and curfew enforcement. Monitored optionally for unauthorized access alerts.

## Session Telegram Notification
An automated push message sent to the administrator's Telegram chat upon user sign-in or sign-out, containing identity, timestamp, machine name, and screen time status.
## Admin Access Verification
An interactive Telegram security alert dispatched when an unrestricted or administrator user signs in. Contains machine name, timestamp, and an immediate remote lock action (`LOCK_SESSION:<id>`) allowing the administrator to instantly lock the workstation if the logon was unauthorized.

## Interactive Session Prompt
An administrative message dispatched into an active user session requiring user acknowledgement (confirmation or decline with an optional explanatory reason) within a defined countdown window.

## Prompt Turnaround Duration
The elapsed duration between the initial presentation of an Interactive Session Prompt on the user screen and the receipt of the user response or expiration timeout.

## Offline Notification Queue
A persistent local storage repository retaining system alerts and session lifecycle notifications during periods of host network unavailability, preserving original event occurrence timestamps until network connectivity is restored.


## App Category
A kind of program that a limit applies to. The set is fixed: Games, Video, Social, Study, Creative, System and Unknown. An exe that no App Rule matches is in Unknown. System is never counted and never suspended.

## App Rule
A rule that maps an exe to one App Category. It matches the exact exe file name, or a folder prefix of the full exe path on a whole folder boundary. The match ignores letter case. A file-name rule wins over a folder rule, and the longest folder wins over a shorter one. A rule can carry an App Limit.

## App Limit
An optional weekday and weekend minute value on an App Rule. It replaces the Category Limit for the exes of that rule, but it compares against the same Category Usage. Example: Games 120 minutes and Minecraft 180 minutes. Then the other games stop when all games together reach 120 minutes, and Minecraft stops when all games together reach 180 minutes.

## Category Usage
The whole minutes that a Restricted User spends with an exe of one App Category as the Foreground Window on one day. The Session Agent samples give the time. System and a locked session add no time.

## Category Limit
The daily limit of an App Category: the weekend value on Saturday and Sunday, the weekday value on the other days, plus today's Bonus Minutes. A category can have no limit.

## Suspended App
A process of a Restricted User that the service suspends (`NtSuspendProcess`) because its exe has no time left under its App Limit or Category Limit. The process stays open and its audio is muted. The service records it and resumes it when time is available again or when the service stops. The service never closes or kills it.
