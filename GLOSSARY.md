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
The whole minutes that a Restricted User spends with an exe of one App Category as a Counted Process on one day. The Session Probe samples give the time, under the gap rule. System and a locked session add no time. No data of the Session Agent adds time.

## Category Limit
The daily limit of an App Category: the weekend value on Saturday and Sunday, the weekday value on the other days, plus today's Bonus Minutes. A category can have no limit.

## Suspended App
A process of a Restricted User that the service suspends (`NtSuspendProcess`) for one of two reasons: its exe has no time left under its App Limit or Category Limit, or its exe is a Blocked Program and the process is not a Blocked Start. The process stays open and its audio is muted. The service records it with the reason. It resumes it when the reason ends (time is available again, or the block ends and time is left), when the service stops, or when its session ends (sign-out, restart, shutdown or Forced Logoff), so that it can answer the end-session query of Windows. The service never closes or kills it.

## Pending Suspension
A process that the service suspends 10 seconds later. In this time the Session Agent minimizes the windows of the process while it still runs, because the window of a suspended process cannot be minimized. The service suspends the process at the end of the 10 seconds, also if the agent does not answer. It drops a Pending Suspension without a suspension if the exe has time left again and is not a Blocked Program, the process ended, a Session End Hold of the session started, or a Session End Guard covers the process. Each suspension starts as a Pending Suspension, also a suspension because of a block. A Blocked Start is closed and has no Pending Suspension.

## Session End Hold
A short time (60 seconds) after the service resumed the Suspended Apps of a session because the session ends. During the hold the service suspends no process of that session, also no process of a Blocked Program. The hold ends when the session ends (a confirmed hold) or when its time is over while the session still exists (an unconfirmed hold). An unconfirmed hold writes an event and alerts the parent at most once per day. The child controls the Session Agent that sends the request, so the service grants at most one unconfirmed hold per Restricted User in 15 minutes. A refused request starts a Session End Guard.

## Session End Guard
A short time (60 seconds) after the service refused a Session End Hold. The Session Agent already resumed the Suspended Apps for the end of the session, and the service does not suspend these processes again during the guard. A guard covers only an eligible process: a recorded process that the service suspended, or saw suspended while no guard covered it, and did not resume itself since. A covered process is not eligible until the service suspends it again or sees it suspended again. Without an eligible process no guard starts. A guard does not resume a process and does not stop a new suspension. It ends when the session ends or when its time is over; then the covered processes are suspended again. A guard that ends by its time while the session still exists is unconfirmed. A guard that starts after an unconfirmed guard of the user in 15 minutes is repeated: the service writes an event and alerts the parent at most once per day. A guard never locks the session, and it does not count against the limit of holds.

## Lock Deferral
A time (75 seconds) after the Session Agent reported that the session of a Restricted User ends, or before a Forced Logoff. During the deferral the service does not apply the Enforced Workstation Lock of a limit, a Break, a Schedule Window end or a suspension fallback to that session, because a lock in the first seconds of a sign-out makes Windows end a program before it can ask to save. A lock that becomes due in this time is owed: at the end, if the session still exists, the service checks the session again at once and locks it if a lock is still due. The deferral ends when the session ends or when its time is over. A request during a deferral does not extend it. The child controls the Session Agent that sends the request, so after a deferral that owed a lock and ended while the session still existed, the user gets no deferral for 15 minutes; a Forced Logoff is never refused. The delayed lock of the parent from the admin page waits for the end of the deferral; the lock from Telegram does not wait. The deferral uses a monotonic time, not the wall clock.

## Blocked Program
An exe whose App Rule has the block on. The rule precedence of App Rules decides the block, so a file rule without the block allows an exe below a blocked folder. An exe below the Windows folder, an exe below the Parental Control install folder and `msedgewebview2.exe` are never a Blocked Program. The rule keeps its App Category and App Limit; they apply again when the parent removes the block.

## Blocked Start
A process of a Blocked Program that started after the block took effect and after the service started, and that the service detects within 10 seconds after its creation. The service closes a Blocked Start at once. This is the only close of a program of a Restricted User. A process of a Blocked Program that is not a Blocked Start becomes a Suspended App.

## Language Preset
A named set of default child texts: "de" (German), "en" (English) or "id" (Indonesian). A new install uses "de". The preset sets the language of the service messages, the Session Agent and the child request page. It does not change the language of the admin UIs. A preset switch replaces only the texts that the parent did not edit.

## Admin Session
The signed-in state of the parent in the Remote Admin Interface. It is a token with an issue time and a session epoch. It is valid for 12 hours. A logout, a restart of the service, or a new session epoch (a new TOTP secret or "Sign out all web sessions" in the Admin app) ends it. Each route below `/api/admin/` except the login and the logout requires a valid Admin Session.

## Untrusted Data Folder
A data folder (`C:\ProgramData\ParentalControl`) or an item in it that an account other than SYSTEM or the Administrators group owns or can change, for example a folder that the Restricted User made before the install. The service never uses its content. At the start it renames the folder to `ParentalControl.untrusted-<guid>` and starts with a new database. If the rename fails, the service leaves the folder alone and uses a Relocated Data Folder. A link as the data folder is not moved; the service deletes the link itself.

## Relocated Data Folder
A new data folder `C:\ProgramData\ParentalControl-<32 random hex digits>` that the service creates, with its final owner and rules, when it cannot make the configured data folder safe. The service stores its path in the admin-only registry value `HKLM\SOFTWARE\ParentalControl\DataFolderPath`, and each component reads the data folder from there. It starts with a new database.

## Data Folder Lockdown
The state of the service while no data folder works. The service does not stop: it applies the Enforced Workstation Lock to each active session of a user who is not an administrator, and it tries the data folder step again every minute. It never closes or kills a program.

## Break Rule
The rule of one Restricted User for the whole session: on or off, N minutes of use (10 to 240) and M minutes of break (5 to 120). A Restricted User without a stored rule has the default rule: on, 45 minutes of use, 10 minutes of break. An Administrator or an unrestricted user has no Break.

## Continuous Use
The whole minutes of use of a Restricted User since the last Rest or Break. The service counts the same unlocked session minutes that it adds to the Allowance Time. It never uses data from the Session Agent for this count.

## Rest
A time without use: the session is locked or disconnected, the user is signed out, or the PC sleeps or is off. A Rest of at least M minutes sets Continuous Use to 0.

## Break
A time of M minutes after N minutes of Continuous Use. The service sends a message with the end time and locks the session with the Enforced Workstation Lock. It locks the session again after each unlock and each sign-in until the end. A Break never closes, kills or suspends a program, and it ends at the latest at the end of the Schedule Window. The parent can end a running Break.

## Lock Reason
Why the session of a Restricted User is locked now: outside the Schedule Window, no Allowance Time left, a running Break, or another lock. The service decides it from its own state for each status answer of the Agent Channel, in this priority, and the widget and the tray tooltip of the Session Agent show its text. Only the Break text says "Pause".

## Control Time Zone
The Windows time zone that gives each day, weekday and time of day of a limit: the Allowance Time, the Schedule Window, the Category Limits, the Grace Requests, the capture quota and the end of a Break. Only an administrator can change it (registry value `HKLM\SOFTWARE\ParentalControl\ControlTimeZone`, set in the Admin app). A change of the Windows time zone by the Restricted User has no effect on a limit.

## Pending Usage
Counted minutes of Allowance Time or Category Usage that the database did not store yet, for example on a full disk. The service keeps them in memory, adds them to the stored minutes for each limit check, and stores them at the next successful write.

## Storage Lockdown
The state of the service after 3 ticks in a row with a failed database write. The service applies the Enforced Workstation Lock to each active session of a user who is not an administrator, at least every 2 seconds, and alerts the parent. It ends after a successful write when the volume has at least 100 MB free. It never closes or kills a program.

## Request Window
A window of the Session Agent that shows the child request page of the local service (`http://127.0.0.1:5050/request`) in an embedded WebView2. It loads no other page, opens no other window, file or program, and has no browser tools. It uses only the machine-wide Evergreen WebView2 Runtime and a new, empty data folder for each open. If it cannot show the page, it shows a text of the Language Preset. The Session Agent never starts a browser for a Grace Request.

## Agent Window Budget
At most 5 minutes in any 60 minutes of unlocked time in which the Session Agent, as a counted process of a Session Probe sample, adds no Category Usage and does not count toward the lock of a program that is never suspended. Only the agent's own time is exempt; another process in the same sample still counts. It does not change the Allowance Time, Continuous Use, a Break or the Schedule Window. It reads only probe samples, never data of the Session Agent.

## Session-End End
An end of a Session Probe that the service did not ask for, after which the sign-out of its session, the end of the session or a stop of the service comes within 90 seconds. Windows ends the probe at a sign-out before the service knows about it. The service needs no new probe for it, and it gives no event, no alert and no budget item. Data of the Session Agent never decides it.

## Benign End
An end of a Session Probe within 15 seconds after a resume from sleep or hibernation (the resume window). The service starts a new probe at once, and the end gives no event and no budget item.

## Session Probe
A console program (`ParentalControl.Probe.exe` in `{app}\probe`) that the service starts as SYSTEM, with a restricted token, in the session of each Restricted User. It samples the Foreground Window, the focus window, the windows of other processes below the Foreground Window and the window below the cursor, at random intervals of at most 10 seconds. It sends the window handles and the thread and process ids of each sample to the service through an anonymous pipe. It opens no process and writes no file. The child cannot end, suspend or open it. The service ends it at the sign-out of its session and at the service stop, and starts a new one after an end that it did not ask for.

## Agent Channel
A named pipe that the service creates for each start of the Session Agent, with access for SYSTEM only. The service opens the client end itself and gives it to the agent as an inherited handle (`--channel`). The agent sends its status polls, prompt answers, screen captures and session-end requests through it. The service takes the user and the session of a message from the launch record of the agent, never from the message. The channel proves which agent process got the handle, not that its data is true, so no limit reads it. It replaces the HTTP routes below `/api/agent/`.

## Counted Process
A process of a Session Probe sample that adds Category Usage. These are the entries of a sample:
- The Foreground Window, the focus window and each window of another process below the Foreground Window count, except an entry in System.
- The window below the cursor counts only if it has `noActivate`, except in System. Without `noActivate` it counts no time, but its exe can be suspended.
- A trusted WebView2 process (`msedgewebview2.exe` in a folder of the Evergreen WebView2 Runtime) counts in the same role as its creator: the first parent process that is not a trusted WebView2. Each other `msedgewebview2.exe` counts by its App Rule.
- The Session Agent counts in Unknown, except during an active Interactive Session Prompt of the user and within the Agent Window Budget. Below the cursor it counts nothing. Only the agent that the service started (its launch record) gets these exemptions; a copy of the agent exe that the child started is a normal Unknown exe.

The service adds the time of a sample at most one time to each App Category of its Counted Processes.

## Blind Sample
A sample of the Session Probe that shows that a window or a desktop is there, but that the service cannot attribute to an exe. It is one of these:
- The input desktop cannot be opened or set (`blind: desktop`).
- A window is in front, but it gives no thread (`blind: window`).
- More than 8 other processes have visible windows below the Foreground Window, or the walk passed 4096 windows (`blind: children`).
- An entry is Unreadable: the access is denied, or the process is in another session.

A Blind Sample counts toward the lock of the budget. After the first such lock of a Restricted User on a day, each Blind Sample of that day locks at once (the strike). Both locks are Probe Locks. The service ignores a Blind Sample if the session is not active or is locked.

## Budget Item
An item that counts toward the lock of the budget like a Blind Sample, but never triggers the strike, because an honest action or a real probe bug can also cause it. It is one of these:
- A sample with an Ended entry (the window closed between the sample and the read) in a role that would count time.
- The gap item of the gap rule: a gap of more than 12 seconds whose two samples are not the same, and each gap of more than 60 seconds. A gap over an end of the Session Probe is no gap item.
- A long sample: a sample that takes more than 12 seconds, for the gap that ends at it.
- From the 3rd unexpected end of the Session Probe within 10 minutes, each further unexpected end. A Session-End End and a Benign End are no Budget Item.

A sample adds at most one Budget Item. At the 6th Blind Sample or Budget Item of a Restricted User within 10 minutes of unlocked, connected time, the service locks the session. The service ignores a Budget Item if the session is not active or is locked.

## Probe Lock
An Enforced Workstation Lock that comes from the data of the Session Probe. It is one of these:
- The lock at the 6th Blind Sample or Budget Item within 10 minutes, and the strike.
- The lock of a program that is never suspended after 60 seconds in an exhausted App Category.
- The lock of a full set of suspend candidates (`ForegroundHistory`).

A Probe Lock applies only if the parent switched the probe locks on. Else the service is in Shadow Mode. The locks of the Allowance Time, the Schedule Window, a Break, the Data Folder Lockdown and the Storage Lockdown are not Probe Locks.

## Shadow Mode
The state of the service while the probe locks are off: the setting `probe_locks_enforced` is not "1". A new install and the update to the release of the Session Probe start in Shadow Mode. At each decision for a Probe Lock, the service does not lock. It writes the event `PROBE_WOULD_LOCK` with the reason and alerts the parent, at most one time for each Restricted User, day and reason (for a program that is never suspended, for each exe and day). The budget and the strike advance as if the service had locked. The counting from the Session Probe and the suspensions work as in normal operation. Only the parent switches the probe locks on, in the web admin.
