# Tasks

## 1. Domain

- [ ] 1.1 Add `DayLimits.DueWarnings(IEnumerable<CategoryResolution>)` in `CategoryLimitCalculator.cs` (design D2) and verify that `dotnet test tests/ParentalControl.Domain.Tests` passes.
- [ ] 1.2 Add xUnit tests in `CategoryLimitCalculatorTests.cs` for the scenarios: app not running gives no App Limit warning, a running app gives it, a Category Limit warning comes without a running program, a folder rule covers an exe below its folder, and a file-name rule that wins over a folder rule takes the folder rule's warning away. Verify that the tests fail before 1.1 and pass after it.

## 2. Service

- [ ] 2.1 In `CategoryEnforcer.Tick`, list the processes before `SendWarnings`, resolve the exe paths of each user's sessions, and send only `DueWarnings` (design D3). Verify with `dotnet build -p:EnableWindowsTargeting=true`: 0 errors.

## 3. Check on the test VM (Windows only)

Use `TestGame` and `VmTool` from `scripts/vm/` (see `scripts/vm/README.md`). `VmTool set-usage` only increases the usage, so do the steps in this order.

- [ ] 3.1 Publish `TestGame` to `C:\Games\TestGame`. In the web admin, set the Games Category Limit to 180/180 and add the file rule `TestGame.exe` → Games with the App Limit 120/120. Log on as `Kid` in the Proxmox console, with no `TestGame` open. Run `VmTool set-usage Kid Games 115`. Expected: after two ticks, no message for TestGame, and the service log has no `Category warning rule:` line.
- [ ] 3.2 Run `VmTool set-usage Kid Games 117`. As `Kid`, start `TestGame` and bring it to the front. Expected: within 60 seconds, one message that TestGame ends in 3 minutes, and one log line `Category warning rule:<id> (3 min left)`. No second 5-minute message on the next ticks.
- [ ] 3.3 Close `TestGame`. Run `VmTool set-usage Kid Games 119`. Expected: no 1-minute message for TestGame.
- [ ] 3.4 As `Kid`, start `TestGame` again. Expected: within 60 seconds, one 1-minute message for TestGame. Run `VmTool set-usage Kid Games 120`. Expected: `TestGame` is suspended and still exists (`state.ps1`).
- [ ] 3.5 Run `VmTool set-usage Kid Games 175`. Expected: one message that Games ends in 5 minutes, as before this change.

## Workflow follow-up

- Run `/opsx:verify` after the VM check, then `/opsx:archive`.
