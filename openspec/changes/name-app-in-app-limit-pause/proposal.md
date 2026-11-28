# Proposal

## Why

When an App Limit is reached, the suspension message names the category, not the app (issue #34). On VM 102 the rule `TestGame.exe` had an App Limit of 120 minutes inside Games (180 minutes). At 120 minutes the child got "Games time is over … paused", but Games still had 60 minutes left and the other games kept running. The message also used the daily slot of Games, so the real Games message at 180 minutes did not come.

## What Changes

- A suspension because of an App Limit sends the suspension message with the name of the app (`{name}`), as the App Limit warning does. A suspension because of a Category Limit names the category, as today.
- The rule "one message per day" applies per limit (a Category Limit or an App Limit), not per category. An App Limit message and a Category Limit message of the same category can both come on one day.
- The event of the parent says which limit was reached: "Games limit reached: programs paused" or "TestGame App Limit reached (Games): programs paused".
- No new message key. The message key `msg_category_suspended` serves both limits, as `msg_category_warn` already does: `{name}` is the app or the category, `{category}` is always the category. The default texts in "de", "en" and "id" stay the same.
- Decided by the orchestrator under the user's delegation (2026-11-28): reuse the message key. Rejected: a new key `msg_app_suspended`. See design.md D1.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `category-limits`: the requirement "Tell the child about a suspension" names the limit that suspends the program and counts the daily message per limit.

## Out of scope

- The texts of the messages. The default texts stay; the parent can still edit them.
- The labels of the message fields in the web admin and the Admin app. They still say "Kategorie" (also the label of the warning). A later change can update them after PR #43, which changes `web/admin/src/i18n/de.ts`.
- A suspension because of a block. It keeps its own message (`msg_program_blocked`).
- A retry of the suspension message when all sessions of the child are locked. The behaviour stays as today.
- The warnings before a limit ends. They already name the app.

## Impact

- `src/ParentalControl.Domain/Categories`: a new pure type `SuspensionNotice` (which limit, which name, the daily key) and `AppRule.DisplayName`, with xUnit tests.
- `src/ParentalControl.Service/CategoryEnforcer.cs`: `Suspend` gets the limit of the exe from the limits of the tick and uses `SuspensionNotice`. `SendWarnings` uses `AppRule.DisplayName`.
- No change of the database, the settings, the web admin, the Admin app or the files of PR #43.
