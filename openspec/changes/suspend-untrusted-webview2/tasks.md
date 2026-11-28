# Tasks

## 1. Code and tests (Mac)

- [x] 1.1 Move `TrustedWebView2` from `Sessions/CountedProcesses.cs` to `src/ParentalControl.Domain/Categories/TrustedWebView2.cs` (design D1). Add the constant `RuntimeRelativeFolder` and the property `Folders`. The test does not change.
- [x] 1.2 `ProtectedPrograms`: a required constructor parameter `TrustedWebView2`, the property `TrustedWebView2`, and `IsProtected` tests the path with it, not the file name. Keep `WebView2FileName` and `IsProtectedFileName` for the refusal of a blocked file rule (D5).
- [x] 1.3 `SuspensionPlanner`: a required `PlannerInput` parameter `TrustedWebView2` after `WindowsFolder`; `IsExcluded` tests the path with it. Remove `SuspensionPlanner.WebView2FileName`. Change 1.2 and 1.3 in the same commit (the invariant of D1).
- [x] 1.4 Wiring: `CategoryRepository.LocalProtectedPrograms()` makes the one instance from the Program Files (x86) and Program Files known folders. `CategoryEnforcer` gives `BlockEnforcer.ProtectedPrograms.TrustedWebView2` to the planner. `SessionProbeHost` uses `_protected.TrustedWebView2` for the creator walk and `CountedProcesses.Select`.
- [x] 1.5 xUnit tests in `tests/ParentalControl.Domain.Tests`:
  - `Blocking/TrustedWebView2Tests`: both Program Files folders, the letter case, `\\?\`, `/`, quotes, a `..` inside and outside the runtime folder, `Application2`, the Downloads copy, a per-user and a Fixed Version runtime, another drive, a UNC path, 8.3 short names, paths that are not absolute, and the folder list.
  - `ProtectedProgramsTests`: a copy in `D:\Apps\` and in Downloads is not protected; the runtime in both Program Files folders is.
  - `SuspensionPlannerTests`: the trusted runtime is protected and not suspended; the Downloads copy is not protected and is suspended (reason Limit, category Unknown); with time left it runs; a recorded copy stays suspended.
  - `BlockedStartDeciderTests`: a copy below a blocked Downloads folder gives `Close` (and `Suspend` after 11 s); the runtime below a blocked `C:\Program Files (x86)\Microsoft\` gives `Ignore`.
  - `NeverSuspendedAllowanceTests`: the Downloads copy gives no fallback; the generic tests use `C:\Windows\notepad.exe` as the exe that is never suspended.
  - `CountedProcessesTests`: the Downloads copy is a Foreground Window exe (`ExesToRecord`).
- [x] 1.6 On the Mac: `dotnet build -p:EnableWindowsTargeting=true`, the same with `-p:VmTest=true`, and `dotnet test tests/ParentalControl.Domain.Tests` pass.

## 2. Spec and docs

- [x] 2.1 Spec deltas: `blocked-programs` "Programs that are never blocked"; `category-limits` "Only the child's processes" and "WebView2 counts with the program that started it" (the scenario "Game renamed as WebView2" expects a suspension, not a lock; two Fixed Version scenarios).
- [x] 2.2 `GLOSSARY.md`: the new term "Trusted WebView2 Runtime"; "Blocked Program" and "Counted Process" use it.
- [x] 2.3 ADR 0011: the consequence about the copy as `msedgewebview2.exe` names this change. The archived design `session-probe-and-agent-channel` D5 gets a note (design D7).
- [x] 2.4 `openspec validate --all --strict` passes.

## 3. Check on the test VM (VM 102)

Setup for 3.1 to 3.3: roll back to the snapshot `test-base-net10`. Deploy this branch with `-p:VmTest=true`. Restrict `Kid` (`VmTool restrict Kid 300`). Turn the Breaks off for `Kid`. Set the Unknown limit to 1 minute (`VmTool limit Unknown 1`), then restart the service, because `VmTool limit` writes the database of another process and the service reads a new limit only after a restart. Sign in `Kid` in the Proxmox console, not over RDP. Copy the test game (`TestGame.exe`) as `C:\Users\Kid\Downloads\wv\msedgewebview2.exe` as `Kid`. No App Rule covers `C:\Users\Kid\Downloads\`.

- [ ] 3.1 Copy suspended at the limit, probe locks off and on. With `probe_locks_enforced` off (the default), start the copy as `Kid` and keep it in front until Unknown is exhausted. Expected: the service logs a suspension of `C:\Users\Kid\Downloads\wv\msedgewebview2.exe` with the reason Limit within 70 seconds after the exhaustion; the process still exists and is suspended; no `PROBE_WOULD_LOCK` event for that exe; the session is not locked. Then add 5 Bonus Minutes in the web admin: the copy resumes. Switch the probe locks on in the web admin and repeat until Unknown is exhausted again. Expected: the same suspension within 70 seconds, and no Enforced Workstation Lock for 3 minutes after it.
- [ ] 3.2 Request Window. With Unknown exhausted and the probe locks on, `Kid` clicks the request button and types in the Request Window for 2 minutes. Expected: the `msedgewebview2.exe` processes of the window run from `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\<version>\`; the service never suspends them (no suspension log line for that path); the window takes the input and sends the request; the session is not locked within the Agent Window Budget. List each `msedgewebview2.exe` path of the session with `Get-Process msedgewebview2 | Select Path` and record it here.
- [ ] 3.3 Block of a copy. End the copy from 3.1 first. Add a folder rule `C:\Users\Kid\Downloads\` with the block on (category Unknown). Give Unknown time again with Bonus Minutes in the web admin. `Kid` starts `C:\Users\Kid\Downloads\wv\msedgewebview2.exe`. Expected: the service closes it within 5 seconds after its start (a Blocked Start log line). A file rule `msedgewebview2.exe` with the block on is still refused in the web admin. The Request Window still opens.

## 4. Archive

- [ ] 4.1 After 3.1 to 3.3 pass: `/opsx:archive`. Then `openspec validate --all --strict` passes, and task 7.4 of `session-probe-and-agent-channel` (the release prerequisite) can be ticked.
