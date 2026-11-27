# Tasks

## 1. Dependency

- [ ] 1.1 Add the NuGet package `Microsoft.Web.WebView2` with a pinned current stable version to `src/ParentalControl.Agent/ParentalControl.Agent.csproj` (design D1, Risks). Verify that `dotnet build -p:EnableWindowsTargeting=true` passes and that the `win-x64` publish output of `build.ps1` has `WebView2Loader.dll` (or the loader is linked statically) next to `ParentalControl.Agent.exe`.

## 2. Domain: pure logic

- [ ] 2.1 Add `RequestWindowUrlPolicy` with `IsAllowedDocument(Uri)` and `IsAllowedResource(Uri)` (design D2). Verify with xUnit tests: `http://127.0.0.1:5050/request`, `/request?user=Kid`, `/REQUEST` allowed; `/admin`, `/request/x`, `/requestx`, `https://127.0.0.1:5050/request`, `http://localhost:5050/request`, `http://127.0.0.1:5051/request`, `http://127.0.0.1.example.com:5050/request`, a user-info URL `http://x@127.0.0.1:5050/request`, and each scheme of D2 refused as a document; `/admin/assets/x.js` and `/api/request` allowed as resources; `https://example.com/x.png` refused.
- [ ] 2.2 Add `AgentWindowBudget` (design D8): input the sample interval, whether the Session Agent is a counted process and whether the session is unlocked; output the exempt seconds; a sliding window of 60 minutes of unlocked time with at most 5 minutes exempt. Verify with xUnit tests: 3 minutes exempt; 7 minutes in 60 gives 5 exempt and 2 counted; the budget is free again after 60 more unlocked minutes; locked time does not move the window; a sample without the agent uses nothing.
- [ ] 2.3 Add the keys `request_window_title` and `request_window_unavailable` to `AgentTexts` in "de", "en" and "id" with the texts of design D6. Verify that the existing test "each key in each preset" passes and add an assert for the German texts of the spec scenarios.
- [ ] 2.4 Add a source test in `tests/ParentalControl.Domain.Tests` that scans `src/ParentalControl.Agent` and fails on a `Process.Start` or `ProcessStartInfo` with an `http` URL or `UseShellExecute` for a URL. Verify that it fails on the current `WidgetWindow.xaml.cs` and passes after task 3.4.

## 3. Agent: the Request Window

- [ ] 3.1 Add the runtime lookup of design D4: remove each `WEBVIEW2_*` variable from the agent process, read `pv` from the 32-bit view of `HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`, build the folder and check `msedgewebview2.exe`. Put the folder decision (`pv` empty, `0.0.0.0`, a version with `..`) in a pure Domain function. Verify with xUnit tests for the pure part and with task 7.6.
- [ ] 3.2 Add `RequestWindow` (WPF) with the WebView2 control: `CoreWebView2Environment.CreateAsync` with the folder of 3.1 and the data folder `%LOCALAPPDATA%\ParentalControl\WebView2`, `CoreWebView2ControllerOptions.IsInPrivateModeEnabled = true`, the settings of design D3 and the event handlers of design D2 with `RequestWindowUrlPolicy`. Dispose the web view when the window closes. Verify that the build passes and with tasks 7.2 to 7.5.
- [ ] 3.3 Add the fallback of design D6: a text block with `request_window_unavailable` for no runtime, a failed creation, a failed main navigation that passed the policy (by `NavigationId`, not for `WebErrorStatus` `OperationCanceled`) and `ProcessFailed`; the title `request_window_title`. Verify with task 7.4 (a refused link keeps the page) and task 7.8.
- [ ] 3.4 Replace both `Process.Start` calls (`WidgetWindow.RequestBtn_Click`, the tray item `tray_request` in `AgentController`) with one method that opens the Request Window or activates the open one. Verify with task 2.4 and task 7.2.
- [ ] 3.5 Hide the tray item `tray_request` and close an open Request Window when the status says `isRestricted: false` (design D6). Verify with task 7.9.

## 4. Service: the Agent Window Budget on main

- [ ] 4.1 In the handler of the agent sample (`POST /api/agent/activity`), apply `AgentWindowBudget` for each user when the sampled exe is the agent exe below the install folder, before `CategoryUsageRepository.AddSeconds` (design D8). If the probe counting of PR #43 has landed and the route is gone, apply the budget in the probe counting instead. Verify with an assert in `tests/ParentalControl.Tests` (install folder mapped to Unknown: the first 5 minutes add nothing, the next minute adds 60 seconds) and that `scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED`.

## 5. Installer

- [ ] 5.1 In `build.ps1`, download the Evergreen Bootstrapper and stop the build unless `Get-AuthenticodeSignature` gives `Valid` with the signer "Microsoft Corporation" (design D7). Verify with a local run on the VM or in the release workflow: the bootstrapper is in the build folder and a changed byte stops the build.
- [ ] 5.2 In `installer/ParentalControl.iss`, add the bootstrapper to `[Files]` (`{tmp}`, `deleteafterinstall`), the `[Run]` entry `/silent /install` with `Check: not IsWebView2RuntimeInstalled`, and the Pascal check of design D7. Log the exit code; do not stop the setup on a failure. Verify with task 7.8.
- [ ] 5.3 Check that `.github/workflows/release.yml` builds the installer with the bootstrapper. Verify with a run of the workflow on a test tag or with `workflow_dispatch`, if the workflow has it.

## 6. Coupling and documentation

- [ ] 6.1 Run `/opsx:update` on `session-probe-and-agent-channel` (PR #43) so that its D5, its widget exemption and its requirement "A program that is never suspended still has a limit" use the Agent Window Budget (design D8). If PR #43 is already archived, propose a follow-up change that modifies `category-limits`. Verify that `openspec validate --strict` passes for the changed change.
- [ ] 6.2 Add "Request Window" and "Agent Window Budget" to `GLOSSARY.md`. Verify that the terms match `specs/request-window`.
- [ ] 6.3 Set the status of ADR 0014 to Accepted after the approval, and add a line for issue #52 to `docs/ROADMAP.md`. Verify by reading both files.
- [ ] 6.4 Add to the release notes that the request button opens a window of Parental Control and that the installer can install the WebView2 Runtime. Verify by reading the release notes draft.

## 7. Check on test VM 102 (console, not RDP)

Before the install, check that the snapshot `test-base` exists. Roll back to it at the end.

- [ ] 7.1 Before the install, record the WebView2 state of `test-base`: `reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv`. Expected: either a version and a folder below `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\`, or no key. Write the result into this task.
- [ ] 7.2 Install the build with the default browser rules. As `Kid`, click "🎮 Mehr Zeit wünschen" in the widget, then the tray item. Expected: the Request Window shows the request page of `Kid` with the title "Mehr Zeit wünschen"; the second click activates the same window; the service log has no `Closed Blocked Start` line and `blocked_starts` has no new row; `Get-Process msedge` finds nothing.
- [ ] 7.3 In the Request Window, ask for 15 minutes with a reason, then for a schedule change. Expected: both requests are in the web admin; the parent gets the Telegram alert; the page shows the success message.
- [ ] 7.4 With two Restricted Users, click a `/request?user=` link of the admin test banner. Expected: the window loads that page. Then, in the Request Window, press F12, Ctrl+Shift+I, Ctrl+P, Ctrl+F, Ctrl+U and F5, right-click the page, Ctrl+click and middle-click a link, and drag a link from Notepad and an `.html` file from Explorer onto the window. Expected: no developer tools, no menu, no print dialog, no new window, no other page, no fallback text, no `msedge.exe`. Typing, Tab, Enter and Ctrl+V in the reason field work.
- [ ] 7.5 Expected after 7.2: the folder `C:\Users\Kid\AppData\Local\ParentalControl\WebView2` exists and the install folder has no new file. Close the Request Window. Expected: no `msedgewebview2.exe` of `Kid` runs after 10 seconds, and the data folder has no `Cookies` file and no `Local Storage` folder with data of `127.0.0.1` (InPrivate profile).
- [ ] 7.6 As `Kid`, set the user variables `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` and `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER=C:\Users\Kid\wv`, sign out and in, open the Request Window. Expected: `netstat -ano | findstr 9222` shows nothing; each `msedgewebview2.exe` of `Kid` has its path below `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\`. Then, as administrator, `reg add HKLM\Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments /v ParentalControl.Agent.exe /d --remote-debugging-port=9223` and open the Request Window as `Kid`. Expected: port 9223 listens (this proves the path that the loader reads). Delete the value. As `Kid`, the same `reg add` below `HKCU\Software\Policies\...` fails with "Access is denied".
- [ ] 7.7 Keep the Request Window in front for 5 minutes. Expected: Category Usage of each category of `Kid` (web admin) does not increase; the session is not locked.
- [ ] 7.8 As administrator, uninstall the WebView2 Runtime (or roll back to a snapshot without it, if 7.1 found none). As `Kid`, click the request button. Expected: the window shows "Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt." and no browser starts. Then disable the network adapter and run the installer. Expected: the setup completes and its log has the exit code of the bootstrapper. Enable the network and run the installer again. Expected: the runtime is below `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\` and the Request Window shows the page. Stop the service and open the window. Expected: the fallback text.
- [ ] 7.9 Sign in as `nick` (Administrator) in the console. Expected: no widget and no request item in the tray menu of the agent.
- [ ] 7.10 Roll back to `test-base`. Expected: SSH works and Parental Control is not installed.

## Workflow follow-up

- `/opsx:verify` with the VM results of group 7, then `/opsx:archive` after the merge.
- Final check on the child's PC: the request button opens the Request Window with all browsers blocked.
