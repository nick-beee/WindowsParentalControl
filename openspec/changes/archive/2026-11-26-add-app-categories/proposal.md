# Proposal

## Why

Today the only limit is the Allowance Time of the whole session. When it ends, the service locks the session, so the child also loses the programs for school. The parent wants to limit games and video by themselves and keep study programs open. The hard rule stays: a limit never closes a program of the child.

## What Changes

- New **App Category** concept: Games, Video, Social, Study, Creative, System, Unknown. Each category has a daily limit for weekdays and for the weekend (Saturday and Sunday), or no limit.
- Default limits: Games, Video, Social and Unknown get 120 minutes on a weekday and 180 minutes on a weekend day. Study, Creative and System have no limit. System is never counted and never suspended.
- New **App Rule** concept: a rule maps an exe to a category, by exact exe file name or by folder prefix of the exe path. A rule can carry its own **App Limit** (weekday and weekend minutes). The app's time still counts toward its category; the app may run until the Category Usage reaches the App Limit instead of the Category Limit. Example: Games 120 minutes, Minecraft 180 minutes: other games stop when all games together reach 120 minutes, Minecraft stops when all games together reach 180 minutes. The service seeds a small set of default rules (Steam and Epic game folders, Microsoft Office, the Windows folder). An exe without a rule is in Unknown.
- The Session Agent reports the full exe path of the Foreground Window, not only the process name. The service resolves the category and counts **Category Usage** minutes per Restricted User and day.
- When the usage of a category reaches its limit (plus today's Bonus Minutes), the service **suspends** every process of that category in the child's session (`NtSuspendProcess`). The session stays open. The child gets a message. The Session Agent mutes the audio of the suspended processes.
- The service resumes the suspended processes when the day changes, when Bonus Minutes or a higher limit make time available again, and when the service stops.
- Warnings at 5 and 1 minutes before a category limit ends, through the existing session message.
- Web admin: a page to edit the category limits and the App Rules, and a way to assign an app from the App Usage table to a category.
- The existing session Allowance Time, Schedule Window and Enforced Workstation Lock do not change. The Allowance Time counts the whole session, Study included. The parent keeps it at 120 minutes on weekdays and 180 minutes on weekends for now (set with the existing weekend preset), so the session can lock at the same time as a category limit. A higher session limit is a later setting, not part of this change.
- New project `ParentalControl.Domain` (`net8.0`, no WinAPI) for the rule matching and the limit calculation, with an xUnit test project that runs on the Mac and in CI.

## Out of scope

- Breaks ("N minutes, then M minutes pause"), blocked programs and browsers: stage 2.
- Authentication of the Session Agent and a process list from the service as the main source of usage: stage 3. Until then a child who sends fake data to `127.0.0.1:5050` can cheat the category counter (see design, Risks).
- Rules by signature publisher or SHA256, and AI classification: stage 4.
- Category settings in the WPF Admin: a later change. The web admin is the editor for now.
- Different category limits per Restricted User. The categories and limits apply to all Restricted Users.
- A limit per day of the week beyond the weekday/weekend split, and limits per date (schedule exceptions).
- Instant suspension of a program of an exhausted category at its start: the service suspends it at the next tick (60 seconds at most). Stage 2 adds a start trace.

## Capabilities

### New Capabilities
- `app-categories`: the categories, the App Rules and their optional App Limits, how the service resolves the category of an exe, the default rules, and how the parent edits them.
- `category-limits`: the count of Category Usage, the weekday and weekend limits, the warnings, the suspension and the resume of the processes of a category.

### Modified Capabilities
<!-- None: openspec/specs/ is empty. -->

## Impact

- `src/ParentalControl.Core/Data/DatabaseManager.cs`: new tables `app_categories`, `app_rules`, `category_usage`, `suspended_processes`; retention in `UsageMonitorWorker.RunCleanupIfNeeded`.
- New repositories in `src/ParentalControl.Core/Data/`.
- `src/ParentalControl.Core/Platform/NativeMethods.cs`: `OpenProcess`, `NtSuspendProcess`, `NtResumeProcess`, `QueryFullProcessImageNameW`.
- `src/ParentalControl.Service/UsageMonitorWorker.cs`: category check in the 60-second tick; resume on stop in the service lifetime.
- `src/ParentalControl.Service/Web/WebServerHost.cs` (`POST /api/agent/activity`, new admin routes) and `Web/Pages/AdminPage.cs`.
- `src/ParentalControl.Agent/ActivityTracker.cs`: send the full path; new audio mute for suspended processes.
- New `src/ParentalControl.Domain/` and `tests/ParentalControl.Domain.Tests/`; `ci.yml` runs the new tests.
- `GLOSSARY.md`: new terms App Category, App Rule, App Limit, Category Usage, Category Limit, Suspended App.
