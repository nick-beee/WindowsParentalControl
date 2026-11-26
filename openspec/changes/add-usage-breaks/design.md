# Design

## Context

See proposal.md for the motivation and `specs/usage-breaks/spec.md` for the required behavior.

The current state that shapes the approach:
- `UsageMonitorWorker` ticks every 60 seconds. For each unlocked session of a Restricted User, it checks the Allowance Time and then the Schedule Window. If one of them ends, it sends a message with `NotificationManager.SendMessage` (`WTSSendMessageW`) and locks the session with `SessionManager.LockSession` (`WTSDisconnectSession`). This is the Enforced Workstation Lock (ADR 0006). Then it sends the threshold warnings and runs `CategoryEnforcer.Tick()`.
- `SessionTracker.TickAllSessions` adds the elapsed whole minutes of each unlocked session to `usage.minutes_used`. A gap of more than 2 minutes counts as 1 minute (an undetected sleep). `FlushSessionTime` adds the open minutes at a lock, a sign-out and a sleep.
- `SessionTracker.OnSessionUnlock` locks the session again if the Allowance Time or the Schedule Window is over. It sends the message at most one time in 10 seconds. `OnUserLogon` signs the child out (`WTSLogoffSession`) in the same case.
- `ParentalControlServiceLifetime.OnSessionChange` calls `OnSessionLock` for `SessionLock` and `ConsoleDisconnect`, and `OnSessionUnlock` for `SessionUnlock` and `ConsoleConnect`.
- `SessionTracker.SynchronizeSessions` reads the lock state each minute with `WTSEnumerateSessionsW` and `WTSQuerySessionInformationW`. Issue #9: after Win+L, this poll shows the session as unlocked.
- The pure logic is in `src/ParentalControl.Domain` (`net8.0`) with xUnit tests that run on the Mac.
- Two draft changes are not merged: `add-german-child-texts` (PR #20, message catalog with the presets "de", "en" and "id") and `require-admin-sign-in` (PR #21, Admin Session for each route below `/api/admin/`).

## Goals / Non-Goals

**Goals:**
- The break logic (count, Rest, warnings, start, end, rule change) in pure code in `ParentalControl.Domain`, tested on the Mac.
- One source of minutes for the Allowance Time and for Continuous Use, so that the two counts cannot differ.
- No new WinAPI call. The Break uses the same lock and message calls as the Allowance Time.

**Non-Goals:**
- A fix of the lock state in `SessionTracker` (issues #9 and #14). The design works around it (D7).
- The page in the React admin (issue #26).

## Decisions

Each decision below was decided by the orchestrator under the user's delegation (2026-11-26).

### D1. One Break Rule per Restricted User for the whole session
A Break Rule belongs to one Restricted User and covers all programs. A Break locks the session.
- *Rejected:* a Break Rule per App Category that suspends only that category. A suspended game can still show its frozen window, and the child can use other programs. Then the Break is not a real rest. This is a follow-up change.
- *Rejected:* one global rule for all users. The service already stores the limits per user, and a second child can need another rule.

### D2. Default rule also for existing installs
A Restricted User without a row in `break_rules` gets the default rule: on, 45 minutes of use, 10 minutes of break. The code reads the default. It does not seed a row and it uses no seed key. This covers a new install, a user that the parent marks as restricted later, and an install that gets this version as an update.
- The brief named the default for a new install only. The orchestrator also applies it to an update, because the page for the settings waits for issue #26. With "off" on an update, the parent has no page to turn the rule on, and the feature does nothing. The fork has one family as its user, and that family asked for breaks. The release notes must say that breaks are on after the update.
- *Rejected:* "off" for an existing install. Reason above.
- *Rejected:* a seed row per user at the first start. A user that becomes restricted later needs a second seed path.

### D3. Value ranges
N is 10 to 240 minutes, M is 5 to 120 minutes, in whole minutes.
- N of at least 10 leaves room for the 5-minute and the 1-minute warning.
- M of at least 5 makes a Break a real rest.
- *Rejected:* free values. N below 6 makes the 5-minute warning come at the start of the use.

### D4. One source of minutes for the Allowance Time and Continuous Use
Continuous Use counts at the same two places that add minutes to the Allowance Time: `SessionTracker.TickAllSessions` and `SessionTracker.FlushSessionTime`. Each call of `UsageRepository.AddMinutes` for a Restricted User also adds the same minutes to Continuous Use. The rule "a gap of more than 2 minutes counts as 1 minute" applies to both counts. A later fix of issue #9 corrects both counts at the same time.
- *Rejected:* the Foreground Window samples of the Session Agent. The agent channel has no authentication (issue #6, `CLAUDE.md` security rule).
- *Rejected:* a separate timer for breaks. Two timers can drift apart, and the child sees two different numbers.

### D5. Measure the Rest with the time of the last use
The table `break_state` stores `last_use_utc`: the time of the last counted minute, or the time of a lock, sign-out or sleep event of an unlocked session. A Rest ends at an unlock, a sign-in or a wake event, or at the first counted minute after a gap. If the time from `last_use_utc` to the end of the Rest is M minutes or more, Continuous Use becomes 0.
- The events give the exact times, so a Rest of M − 1 minutes does not reset the count (scenario "Rest one minute too short").
- A tick gap of M minutes or more without a sleep event (the PC slept or was off) is a Rest.
- At a service start, if the session of the user is unlocked, the down time counts neither as use nor as a Rest. The service does not know what happened in that time. A short restart (for example an update) must not give a free reset. At a service start without a session of the user, or with a locked session, the Rest starts at `last_use_utc`.
- *Rejected:* reset only after an enforced Break. Then a child who stops for 30 minutes gets a Break soon after the return.
- *Rejected:* a reset at midnight. Use from 23:50 to 00:30 is continuous use. A night with the PC off is a Rest anyway.

### D6. The Break is the Enforced Workstation Lock
At the start of a Break, the service sends `msg_break_start` with `NotificationManager.SendMessage` (`WTSSendMessageW`, 5 seconds, wait), then calls `SessionManager.LockSession` (`WTSDisconnectSession`) and `SessionTracker.OnSessionLock`. It writes the event `BREAK_STARTED`. This is the same call order as for the end of the Allowance Time.
- *Rejected:* suspend all processes of the session (`NtSuspendProcess`). The child can still use the desktop and start new programs, so it is not a break.
- *Rejected:* `LockWorkStation`. It works only from a process inside the session, and the service runs in session 0.
- *Rejected:* sign the child out (`WTSLogoffSession`). This closes the programs and breaks the hard rule.

### D7. Keep the session locked during a Break
Three places lock again:
1. `SessionTracker.OnSessionUnlock`: a running Break is a third reason next to the Allowance Time and the Schedule Window. The service sends `msg_break_running` (at most one time in 10 seconds, with the existing throttle), then calls `LockSession`.
2. `SessionTracker.OnUserLogon`: a running Break locks the new session with `LockSession`. It does not sign the child out.
3. The 60-second tick: if a session of the user is unlocked during a Break, the tick locks it. This covers issue #9 (a wrong lock state) and issue #14 (the re-lock after an unlock is not verified).
- *Rejected:* sign the child out at a sign-in during a Break, as the service does today when the Allowance Time is over (`ForceLogoff`). Programs from the autostart can already run, and the sign-out closes them. This breaks the hard rule.
- *Rejected:* the unlock event only. Issues #9 and #14 show that the event path is not reliable.

### D8. Order in the tick and the Schedule Window end
In `UsageMonitorWorker.ProcessTick`, the break check runs after the checks of the Allowance Time and the Schedule Window, for each session that they did not lock. If one of them locks the session, no Break starts in that tick.
- The end of a Break is the earlier of `start + M` and today's Schedule Window end (`ScheduleRepository.GetEffectiveLimit`, which includes the Schedule Exceptions). Without a schedule, it is `start + M`. After the Schedule Window end, the Schedule Window lock continues as before. The child sees the same `{break_end}` text; the unlock after that time gets the Schedule Window message.
- The break warnings are not sent when the Allowance Time or the Schedule Window ends at or before the start of the Break. The child gets the Allowance Time or Schedule Window warnings instead.
- *Rejected:* the Break comes first. Then the child gets a break message and, at the end of the Break, a second lock message for the day.
- *Rejected:* no Break in the last M minutes before the Schedule Window end. This adds a rule that the child cannot see, and the end of the window locks the session anyway.

### D9. Bonus Minutes do not touch a Break
Bonus Minutes change only `usage.bonus_minutes`. The break logic does not read them.
- *Rejected:* Bonus Minutes end a running Break. The parent has a separate function for that (D10).

### D10. The parent ends a Break
- Remote Admin Interface: `POST /api/admin/breaks/{userId}/end`.
- Telegram: the command `/break` lists each Restricted User with a Break Rule: the state ("Break until 15:10" or "Break in 12 minutes") and, for a running Break, a button. The `callback_data` is `brk_end:<userId>:<breakStartUnixSeconds>`, at most 32 bytes (the limit is 64). The existing check in `TelegramBotService.HandleCallbackQueryAsync` accepts a callback only from the configured chat. The service ends the Break only if the stored start of the running Break equals the start in the button.
- The end of a Break by the parent sets Continuous Use to 0 and writes `BREAK_ENDED` with the reason "parent". The session stays locked. The child unlocks it.
- The Telegram texts for the parent are Indonesian with the preset "id" and English with each other preset, as PR #20 defines for all Telegram texts.
- *Rejected:* a Telegram notice with a button at each Break start, on by default. With 45/10, the parent gets about one message per hour. The notice exists, but the setting `telegram_notify_break` is off by default.
- *Rejected:* the service unlocks the session when the parent ends the Break. The service cannot unlock a session without the password of the child, and the child is maybe not at the PC.

### D11. Change of the Break Rule
- Off: the count stops, `break_state` gets a reset, and a running Break ends at once (`BREAK_ENDED`, reason "rule off").
- On: Continuous Use starts at 0.
- A change of M does not change the end of a running Break.
- A change of N: Continuous Use becomes `min(value, N − 5)`. So the 5-minute warning always comes before the Break. The sent warnings of the current cycle get a reset.
- *Rejected:* the new values apply after the next Break or Rest only. The parent expects an effect at once, and the state must then store the old rule.
- *Rejected:* a Break at once if Continuous Use is already at the new N. That locks the session without a warning.

### D12. Time base and persistence
The service stores times in UTC (`DateTime.UtcNow`, ISO 8601). A change of the time zone, which a standard user can make, does not move a Break. A standard user cannot change the system clock. The existing Clock Tampering Detection logs a clock jump. The text `{break_end}` shows the local time.
- *Rejected:* `Environment.TickCount64` only. It starts at 0 after a reboot, so the state does not survive a restart of the PC.

New tables in the `DatabaseManager` schema block (`CREATE TABLE IF NOT EXISTS`):
- `break_rules (user_id INTEGER PRIMARY KEY REFERENCES users(id), enabled INTEGER NOT NULL, use_minutes INTEGER NOT NULL, break_minutes INTEGER NOT NULL, updated_at TEXT NOT NULL)`. No row means the default rule (D2).
- `break_state (user_id INTEGER PRIMARY KEY REFERENCES users(id), continuous_use_minutes INTEGER NOT NULL DEFAULT 0, last_use_utc TEXT NULL, break_started_utc TEXT NULL, break_ends_utc TEXT NULL, warned_5 INTEGER NOT NULL DEFAULT 0, warned_1 INTEGER NOT NULL DEFAULT 0)`. A running Break has `break_ends_utc` in the future.

Other data:
- Settings key `telegram_notify_break` in `app_settings`, default "false".
- `EventType` gets `BREAK_STARTED` and `BREAK_ENDED`. The detail text of `BREAK_ENDED` names the reason: "elapsed", "schedule end", "parent" or "rule off".
- No change to an existing table. The service writes `break_state` at each counted minute, so one row update per minute.

### D13. Pure logic in `ParentalControl.Domain.Breaks`
- `BreakRule`: a record with `Enabled`, `UseMinutes`, `BreakMinutes`, the default (45, 10, on) and a range check.
- `BreakState`: a record with the stored values of `break_state`.
- `BreakPlanner`: pure functions. Input: rule, state, the time in UTC and one input event (minutes counted, Rest start, Rest end, service start with or without an unlocked session, tick, rule change, end by the parent), plus the Schedule Window end in UTC and a flag "Allowance Time or Schedule Window locks in this tick". Output: the new state and a list of actions (send the 5-minute or the 1-minute warning, start a Break with its end time, end a Break with a reason, lock again).
- `BreakEnforcer` (Service project) reads and writes `break_rules` and `break_state` through a new `BreakRepository` (Core), calls the planner and runs the actions with `NotificationManager`, `SessionManager` and `TelegramBotService`.
- *Rejected:* the logic in `UsageMonitorWorker`. It cannot run on the Mac, and that class already mixes four concerns.

### D14. Admin API routes
All routes are below `/api/admin/`. With PR #21, the deny-by-default gate covers them without a change to the gate. The JSON uses camelCase, as the other admin routes do.
- `GET /api/admin/breaks` → `200 [{ "userId": 3, "username": "Kid", "enabled": true, "useMinutes": 45, "breakMinutes": 10, "continuousUseMinutes": 31, "breakEndsAt": null }]`. `breakEndsAt` is an ISO 8601 time in UTC or null.
- `PUT /api/admin/breaks/{userId}` with `{ "enabled": true, "useMinutes": 45, "breakMinutes": 10 }` → `200` with the item. `400` for a missing field or a value out of range (D3). `404` for an unknown user or a user that is not restricted.
- `POST /api/admin/breaks/{userId}/end` → `200 { "ended": true }`. `409` if no Break runs. `404` as above.
- `GET` and `POST /api/admin/settings` get the key `telegram_notify_break`.
- *Rejected:* the Break Rule as fields in `POST /api/admin/settings`. That route has no user, and a rule belongs to one user.
- These routes do not merge before PR #21. On `main` today, the admin routes have no sign-in while TOTP is off (issue #18). Then a process of the child could end its own Break.

### D15. Child texts in the catalog of PR #20
Three new keys in `ChildMessageKeys` and `MessageCatalog` (`src/ParentalControl.Domain/Messages/MessageCatalog.cs` of PR #20). The keys are in `ChildMessageKeys.All`, so the preset switch and the edit of texts work for them without other code.

| Key | Placeholders | "de" default |
|---|---|---|
| `msg_break_warn` | `{minutes}` | In {minutes} {minutes\|Minute\|Minuten} ist Pause. Speichere jetzt, was du gerade machst. Deine Programme bleiben offen. |
| `msg_break_start` | `{minutes}`, `{break_end}` | Zeit für eine Pause! Der Computer wird jetzt für {minutes} {minutes\|Minute\|Minuten} gesperrt, bis {break_end} Uhr. Deine Programme bleiben offen. |
| `msg_break_running` | `{minutes}`, `{break_end}` | Die Pause dauert noch bis {break_end} Uhr ({minutes} {minutes\|Minute\|Minuten}). Der Computer wird wieder gesperrt. |

"en" defaults:
- `msg_break_warn`: "Break in {minutes} {minutes|minute|minutes}. Please save your work. Your programs stay open."
- `msg_break_start`: "Time for a break! The computer locks now for {minutes} {minutes|minute|minutes}, until {break_end}. Your programs stay open."
- `msg_break_running`: "The break lasts until {break_end} ({minutes} {minutes|minute|minutes} left). The computer locks again."

"id" defaults:
- `msg_break_warn`: "Waktu istirahat dalam {minutes} menit. Harap simpan pekerjaan Anda. Program Anda tetap terbuka."
- `msg_break_start`: "Waktunya istirahat! Komputer dikunci sekarang selama {minutes} menit, sampai pukul {break_end}. Program Anda tetap terbuka."
- `msg_break_running`: "Istirahat berlangsung sampai pukul {break_end} ({minutes} menit lagi). Komputer dikunci kembali."

In `msg_break_running`, `{minutes}` is the remaining time of the Break, rounded up to whole minutes.
- *Rejected:* a temporary path through the "en"/"id" defaults in `SettingsRepository`, if PR #20 is late. Two text paths double the work and the tests. The text tasks wait for PR #20.

### D16. The admin page waits for the React admin
The page for the Break Rule is a part of the React admin (ADR 0009, issue #26). It reads and writes the routes of D14. The page is not a tracked task of this change, so the change can be archived when the service part works.
- *Rejected:* a section in the current `AdminPage.cs`. ADR 0009 replaces that page, so the work is lost.
- *Rejected:* the settings in the WPF Admin app. ADR 0009 makes the web admin the place for settings.

### D17. No ADR
All decisions are in settings and in two new tables. A later change can revert each of them, so this change needs no ADR.

## Risks / Trade-offs

- [Issue #9: after Win+L, the poll shows the session as unlocked. Then the service counts the locked time as use, and a voluntary Rest with Win+L does not reset Continuous Use.] → The error makes a Break come earlier, not later. The lock and sign-out events still record the start of the Rest. The fix of #9 corrects the Allowance Time and Continuous Use together (D4). A VM check confirms that the Allowance Time does not grow during a Break.
- [Issue #14: the re-lock after an unlock is not verified.] → The tick locks again within 60 seconds (D7). A VM check measures the time.
- [`LockSession` on a session that is already disconnected.] → `WTSDisconnectSession` on a disconnected session has no effect or fails. The tick ignores the result. A VM check confirms it.
- [The service restarts while the child plays. The down time counts neither as use nor as a Rest.] → The child gets the down time as extra use. A restart is rare, and the child cannot stop the service.
- [An online game loses its connection during a Break of 10 minutes.] → Accepted. The program stays open, as the parent asked. The parent can change M or put the game on another rule later (per-category breaks, follow-up).
- [The message with `wait` blocks the tick for up to 5 seconds per session.] → The same as for the Allowance Time today. With one child, the delay is small.
- [Breaks are on after an update (D2).] → The release notes say so. The parent can turn the rule off with the API or, after #26, on the page.

## Migration Plan

- The tables come with the first start of the new version. No existing table changes.
- Rollback: install the previous version. It ignores the two tables and the settings key. Breaks stop.
- Order of the merge: PR #21 and PR #20 first, then the implementation of this change. The proposal itself can merge before them.
