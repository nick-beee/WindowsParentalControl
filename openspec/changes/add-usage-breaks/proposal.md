# Proposal

## Why

The parent wants the rule "N minutes of use, then a break of M minutes" (`docs/ROADMAP.md`, stage 2). Today the service has a daily Allowance Time, a Schedule Window and Category Limits, but no break. Time Boss has breaks, but it closes all programs at a break. This fork must keep the programs open (the hard rule in `CLAUDE.md`).

## What Changes

- New: one Break Rule per Restricted User for the whole session. After N minutes of Continuous Use, a Break of M minutes starts. A Restricted User without a stored Break Rule gets the default rule: 45 minutes of use, 10 minutes of break, on.
- New: the service counts Continuous Use itself, from the same unlocked minutes that it counts for the Allowance Time. It never uses data from the Session Agent for this.
- New: a Rest of at least M minutes (locked or disconnected session, signed out, sleep, PC off) sets Continuous Use to 0.
- New: warnings 5 minutes and 1 minute before a Break.
- New: at the start of a Break, the child gets a message that says when the Break ends. Then the service locks the session with the Enforced Workstation Lock. The programs stay open.
- New: until the Break ends, the service locks the session again after each unlock and after each sign-in. It never signs the child out.
- New: a Break never lasts past the end of the Schedule Window. If the Allowance Time or the Schedule Window ends at the same time as a Break starts, the Allowance Time or the Schedule Window wins and no Break starts.
- New: Bonus Minutes do not skip or end a Break.
- New: the parent can end a running Break from the Remote Admin Interface (API) and from Telegram (command `/break`).
- New: the Break Rule and the state of the Break survive a service restart.
- New: API routes `GET /api/admin/breaks`, `PUT /api/admin/breaks/{userId}` and `POST /api/admin/breaks/{userId}/end`, behind the Admin Session.
- New: child message keys `msg_break_warn`, `msg_break_start` and `msg_break_running` with German, English and Indonesian default texts, in the message catalog of `child-messages` (`src/ParentalControl.Domain/Messages`), and a fixed session text `break_title` for the title of the break messages.
- New: an optional Telegram notice to the parent at each Break start, off by default.
- No change to the Allowance Time, the Schedule Window, the Category Limits or the Suspended Apps.

## Capabilities

### New Capabilities

- `usage-breaks`: the Break Rule of a Restricted User, the count of Continuous Use, the Rest, the warnings, the Break as an Enforced Workstation Lock, its order with the Allowance Time and the Schedule Window, the end of a Break by the parent, the admin API routes, the child texts and the Telegram functions of a Break.

### Modified Capabilities

None. The requirement "Session limits stay unchanged" of `category-limits` stays true. The requirements of `child-messages` apply to each child message key and each fixed session message (same placeholders in each preset, word forms, preset switch, update, Telegram language), so the new keys need no change of `child-messages`. The German break texts are in `usage-breaks`. The break routes are in `usage-breaks`, because the capability `admin-access` (PR #21) is not on `main` yet.

## Dependencies

- PR #21 (`require-admin-sign-in`) must merge before the API routes of this change. On `main` today, the web admin has no sign-in while TOTP is off (issue #18). Without #21, a process of the child can call `POST /api/admin/breaks/{userId}/end` and end its own Break.
- PR #20 (`add-german-child-texts`) is on `main` (capability `child-messages`). The break texts use its catalog, the "de" preset and the word forms `{minutes|Minute|Minuten}`.
- Issue #26 (React admin, ADR 0009): the page for the break settings is a part of the React admin. It is not a tracked task of this change, so it does not block the archive. Until #26, the default rule applies, and the parent uses Telegram or the API.

## Out of Scope

- Breaks per App Category (suspend the programs of one category for a break). This is a follow-up change.
- A countdown to the next Break in the Adaptive Widget and in `/api/agent/status`.
- A Telegram `/status` line for the Break.
- Idle detection. Time away from the keyboard in an unlocked session counts as use, as for the Allowance Time.
- A button for the child to start a Break early or to move a Break.
- A "skip the next Break" function for the parent. The parent can turn the rule off or end a running Break.
- German Telegram texts for the parent (`child-messages` has no German Telegram texts).
- The break settings in the WPF Admin app.
- A change of the lock state in `SessionTracker`. PR #30 fixed issue #9 (the poll reads the Windows lock flag). Issue #14 (the re-lock after an unlock is not verified) stays open.

## Impact

- Code: `src/ParentalControl.Domain` (new folder `Breaks`: rule, state, planner), `src/ParentalControl.Service` (`SessionTracker`, `UsageMonitorWorker`, a new `BreakEnforcer`, `Web/WebServerHost.cs`), `src/ParentalControl.Core` (`DatabaseManager`, new `BreakRepository`, `EventType`, `TelegramBotService`), `src/ParentalControl.Domain/Messages` (`ChildMessageKeys`, `MessageCatalog`, `SessionTexts`).
- Data: new tables `break_rules` and `break_state`, new settings key `telegram_notify_break`, new event types `BREAK_STARTED` and `BREAK_ENDED`.
- API: three new routes below `/api/admin/`.
- Tests: xUnit tests in `tests/ParentalControl.Domain.Tests`, and manual checks on test VM 102.
- Docs: new terms in `GLOSSARY.md`.
