# Tasks

## 1. Domain: pure logic

- [ ] 1.1 Add `AppRule.DisplayName`: the file name without ".exe" for a file rule, the last folder name for a folder rule (design D4). Verify with xUnit tests.
- [ ] 1.2 Add `SuspensionNotice.For(category, limit, categoryName)` (design D2). Verify with xUnit tests: a Category Limit gives `cat:Games` and the category name; an App Limit gives `rule:<id>` and the app name; two exes of one App Limit give one key; an App Limit and the Category Limit of the same category give two keys; a null limit falls back to the category.

## 2. Service

- [ ] 2.1 In `CategoryEnforcer`, pass the `DayLimits` of the tick and the resolver to `Suspend`. Use `SuspensionNotice` for the daily key `suspended:{userId}:{Key}`, the `{name}` and `{category}` of `msg_category_suspended`, and the event text of design D5. Use `AppRule.DisplayName` in `SendWarnings`. Verify that `dotnet build -p:EnableWindowsTargeting=true` passes.

## 3. Verification

- [ ] 3.1 On the Mac: `dotnet test tests/ParentalControl.Domain.Tests` passes, and `openspec validate name-app-in-app-limit-pause --strict` passes.
- [ ] 3.2 On VM 102 (snapshot `test-base-net10`, console session of `Kid`, Language Preset "de"):
  1. Install the build of this branch.
  2. Give Games a weekday and weekend limit with at least 10 minutes left today.
  3. Add the file rule `TestGame.exe` in Games with an App Limit of 2 minutes today, and a second Games exe without an App Limit.
  4. As `Kid`, start `TestGame.exe` and keep it in front for 2 minutes.
  5. Expect: TestGame is suspended, the other game keeps running, and the child gets one message "Deine Zeit für TestGame ist für heute um. Der Computer hat TestGame pausiert, nicht geschlossen. …". The event list has "TestGame App Limit reached (Games): programs paused".
  6. Start a second `TestGame.exe` and bring it to the front. Expect: it is suspended and no second message comes.
  7. Use the other game until the Games limit ends. Expect: one message that names Games.
