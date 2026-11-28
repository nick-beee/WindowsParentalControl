# Design

## Context

See proposal.md for the motivation and the spec delta for the required behaviour.

The current code has these properties:

- `CategoryEnforcer.Suspend` sends `msg_category_suspended` after the first suspension of a category on one day. The daily key is `suspended:{userId}:{category}`. `{name}` and `{category}` are both the category name of the Language Preset.
- `SuspendAction` carries the category and the exe path, but not the limit that made the exe exhausted.
- `DayLimits.LimitFor(resolution)` gives the limit of an exe: its App Limit if its rule has one today, else its Category Limit. `LimitStatus.Key` is `cat:<Category>` or `rule:<id>`. The warnings use this key for their daily deduplication.
- `SendWarnings` names the app of an App Limit with the private `AppName(AppRule)`: the file name without ".exe", or the last folder name.
- `ChildMessageKeys` already documents `{name}` as "category or app" for both category keys.

Each decision below is decided by the orchestrator under the user's delegation (2026-11-28).

## Goals / Non-Goals

**Goals:**
- The child sees which program stopped and why: the app for an App Limit, the category for a Category Limit.
- One message per limit and day.
- No change in the files of PR #43 (`SettingsRepository.cs`, `web/admin`, `GLOSSARY.md`).

**Non-Goals:**
- New texts or a new editable message.
- A change of the warnings.

## Decisions

### D1. Reuse the key `msg_category_suspended`
The App Limit message uses the same key and text as the Category Limit message. `{name}` is the app name, `{category}` is the category name. The German default reads "Deine Zeit für TestGame ist für heute um. Der Computer hat TestGame pausiert, nicht geschlossen. Morgen oder mit Extra-Minuten geht es weiter."

Rejected: a new key `msg_app_suspended`. It needs texts in three presets, a field in the Admin app and in the web admin, and changes in `SettingsRepository.cs`, `de.ts`, `types.ts` and `SettingsDialog.tsx`. PR #43 changes all four. The App Limit warning set the precedent: it reuses `msg_category_warn`.

### D2. A pure `SuspensionNotice` decides the message
`SuspensionNotice.For(category, limit, categoryName)` in `ParentalControl.Domain.Categories` takes the limit of the exe (`DayLimits.LimitFor`) and returns:

- `Key`: the daily key of the limit, `LimitStatus.Key` (`cat:Games` or `rule:12`).
- `Name`: the app name (`AppRule.DisplayName`) for an App Limit, else the category name.
- `CategoryName` and `IsAppLimit`.

If the limit is null (the configuration changed in the 10 seconds of the Pending Suspension), it falls back to the Category Limit of the category: key `cat:<Category>`, the category name.

The enforcer prefixes the user: `suspended:{userId}:{Key}`. The existing per-day set `_sentToday` stores it and clears at the new day, as today.

### D3. `Suspend` uses the limits of the tick
`Tick` passes the `DayLimits` of the tick and the resolver to `Suspend`. `Suspend` resolves the exe path and calls `LimitFor`. `Ready` actions run in the same tick, so the limits are current. No new field on `SuspendAction` and no change of the planner.

### D4. `AppRule.DisplayName`
The private `AppName` of the enforcer moves to `AppRule.DisplayName` (Domain), so that the warnings and the suspension message use one name rule and a test covers it.

### D5. The parent event names the limit
The `LIMIT_REACHED` event text is "<Category> limit reached: programs paused" for a Category Limit and "<App> App Limit reached (<Category>): programs paused" for an App Limit. One event per message, with the same daily key.

## Risks / Trade-offs

- [The English default "TestGame time is over for today. The TestGame programs are paused" reads a little odd for one app] → The English warning has the same form today. "de" is the default preset. The parent can edit the text.
- [The field labels in the admin UIs still say "Kategorie"] → Out of scope (proposal.md); a follow-up after PR #43.
- [The suspension message of an App Limit that is longer than its Category Limit] → The Category Limit message comes first (other programs of the category stop), then the App Limit message when the app stops. This is the expected order.
