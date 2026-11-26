# Tasks

## 1. Domain

- [x] 1.1 Add `DayLimits.DueWarnings(IEnumerable<CategoryResolution>)` in `CategoryLimitCalculator.cs` (design D2) and verify that `dotnet test tests/ParentalControl.Domain.Tests` passes.
- [x] 1.2 Add xUnit tests in `CategoryLimitCalculatorTests.cs` for the scenarios: app not running gives no App Limit warning, a running app gives it, a Category Limit warning comes without a running program, a folder rule covers an exe below its folder, and a file-name rule that wins over a folder rule takes the folder rule's warning away. Verify that the tests fail before 1.1 and pass after it.

## 2. Service

- [x] 2.1 In `CategoryEnforcer.Tick`, list the processes before `SendWarnings`, resolve the exe paths of each user's sessions, and send only `DueWarnings` (design D3). Verify with `dotnet build -p:EnableWindowsTargeting=true`: 0 errors.

## 3. Check on the test VM (Windows only)

Use `TestGame` and `VmTool` from `scripts/vm/` (see `scripts/vm/README.md`). `VmTool set-usage` only increases the usage, so do the steps in this order.

- [x] 3.1 Publish `TestGame` to `C:\Games\TestGame`. In the web admin, set the Games Category Limit to 180/180 and add the file rule `TestGame.exe` → Games with the App Limit 120/120. Log on as `Kid` in the Proxmox console, with no `TestGame` open. Run `VmTool set-usage Kid Games 115`. Expected: after two ticks, no message for TestGame, and the service log has no `Category warning rule:` line.
  - Result: Verified. On VM 102 (2026-11-26), Kid had 20 Bonus Minutes today. The service adds them to each Category Limit and App Limit, so each `set-usage` value was 20 above the value in the task. Games was set to 180/180 and the rule `TestGame.exe` → Games 120/120 (rule 17) was added through the web admin API (`PUT /api/admin/categories`, `POST /api/admin/app-rules`). TestGame was closed, and `set-usage Kid Games 135` gave 5 minutes left on the App Limit. Over the ticks at 15:58:28 and 15:59:28, no message came and the log had no `Category warning rule:` line.
- [x] 3.2 Run `VmTool set-usage Kid Games 117`. As `Kid`, start `TestGame` and bring it to the front. Expected: within 60 seconds, one message that TestGame ends in 3 minutes, and one log line `Category warning rule:<id> (3 min left)`. No second 5-minute message on the next ticks.
  - Result: Verified. `set-usage Kid Games 137`, then TestGame started as Kid at 16:00:34. At 16:01:29 the log had `Category warning rule:17 (3 min left)`, and one popup said "Waktu TestGame akan habis dalam 3 menit." (the preset of the VM was "id"). The tick at 16:02:29 ran with TestGame open and 2 minutes left, and no second message came. Usage went to 139 by itself while TestGame ran.
- [x] 3.3 Close `TestGame`. Run `VmTool set-usage Kid Games 119`. Expected: no 1-minute message for TestGame.
  - Result: Verified. TestGame was closed at 16:02:31. `set-usage Kid Games 139` (no change, usage was already 139). Over the ticks at 16:03:29 and 16:04:29, no message came and the log had no new `Category warning rule:` line.
- [x] 3.4 As `Kid`, start `TestGame` again. Expected: within 60 seconds, one 1-minute message for TestGame. Run `VmTool set-usage Kid Games 120`. Expected: `TestGame` is suspended and still exists (`state.ps1`).
  - Result: Verified. TestGame started again at 16:04:52. At 16:05:29 the log had `Category warning rule:17 (1 min left)`, and one popup said "Waktu TestGame akan habis dalam 1 menit." After `set-usage Kid Games 140`, the log had `Suspended C:\Games\TestGame\TestGame.exe (pid 5808, "Games") of Kid`. `state.ps1` showed `TestGame pid=5808 threads=8 suspended=8`, and the window stayed open with its clock at 00:01:37.
- [x] 3.5 Run `VmTool set-usage Kid Games 175`. Expected: one message that Games ends in 5 minutes, as before this change.
  - Result: Verified. `set-usage Kid Games 195` (Games limit 180 + 20 bonus, 5 minutes left). At 16:07:29 the log had `Category warning cat:Games (5 min left)`, and one popup said "Waktu Games akan habis dalam 5 menit." The next tick sent no second message.

## Workflow follow-up

- Run `/opsx:verify` after the VM check, then `/opsx:archive`.
