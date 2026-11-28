# Tasks

## 1. Domain: pure logic

- [x] 1.1 Add `AppRule.DisplayName`: the file name without ".exe" for a file rule, the last folder name for a folder rule (design D4). Verify with xUnit tests.
  - Done: `AppRule.DisplayName`, with the theory `DisplayName_of_a_rule` in `SuspensionNoticeTests`.
- [x] 1.2 Add `SuspensionNotice.For(category, limit, categoryName)` (design D2). Verify with xUnit tests: a Category Limit gives `cat:Games` and the category name; an App Limit gives `rule:<id>` and the app name; two exes of one App Limit give one key; an App Limit and the Category Limit of the same category give two keys; a null limit falls back to the category.
  - Done: `Domain/Categories/SuspensionNotice`, with `SuspensionNoticeTests`.

## 2. Service

- [x] 2.1 In `CategoryEnforcer`, pass the `DayLimits` of the tick and the resolver to `Suspend`. Use `SuspensionNotice` for the daily key `suspended:{userId}:{Key}`, the `{name}` and `{category}` of `msg_category_suspended`, and the event text of design D5. Use `AppRule.DisplayName` in `SendWarnings`. Verify that `dotnet build -p:EnableWindowsTargeting=true` passes.
  - Done. The build has 0 warnings and 0 errors.

## 3. Verification

- [x] 3.1 On the Mac: `dotnet test tests/ParentalControl.Domain.Tests` passes, and `openspec validate name-app-in-app-limit-pause --strict` passes.
  - Done: 1074 tests pass; the change is valid.
- [x] 3.2 On VM 102 (snapshot `test-base-net10`, console session of `Kid`, Language Preset "de"):
  1. Install the build of this branch.
  2. Give Games a weekday and weekend limit of 10 minutes. The Games usage of `Kid` today must be 0 (a new database), because an App Limit compares against the Category Usage.
  3. Add the file rule `TestGame.exe` in Games with an App Limit of 2 minutes today, and a second Games exe without an App Limit.
  4. As `Kid`, start `TestGame.exe` and keep it in front for 2 minutes.
  5. Expect: TestGame is suspended, the other game keeps running, and the child gets one message "Deine Zeit für TestGame ist für heute um. Der Computer hat TestGame pausiert, nicht geschlossen. …". The event list has "TestGame App Limit reached (Games): programs paused".
  6. Start a second `TestGame.exe` and bring it to the front. Expect: it is suspended and no second message comes.
  7. Use the other game until the Games limit ends. Expect: one message that names Games.
    - **Result 3.2 (VM 102, 2026-11-28 CET, build of e24a16d, `Kid` in the console, preset "de"): PASS.** Setup: rollback to `test-base-net10`, `deploy.ps1`, Games 10 minutes on each day, the rule `TestGame.exe` → Games with an App Limit of 2 minutes, the rule `Other.exe` → Games without an App Limit, Breaks off, Games usage 0. `Other.exe` started at 12:11:2x, `TestGame.exe` (pid 1932) in front from 12:12:00. The App Limit warnings named TestGame (12:11:47 "2 min left", 12:13:47 "1 min left"). Pending Suspension at 12:14:48.290, "Suspended … TestGame.exe (pid 1932, Games, Limit)" at 12:14:58.829. The console showed one message "Deine Zeit für TestGame ist für heute um. Der Computer hat TestGame pausiert, nicht geschlossen. Morgen oder mit Extra-Minuten geht es weiter." The event list had "TestGame App Limit reached (Games): programs paused" (12:14:58.831). `Other.exe` kept running (its clock went on). A second `TestGame.exe` (pid 7140) in front from 12:15:50 was suspended at 12:15:58.876 with no second message and no second event.
