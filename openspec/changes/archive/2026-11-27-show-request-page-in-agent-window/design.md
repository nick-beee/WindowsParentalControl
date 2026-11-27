# Design

## Context

See proposal.md for the motivation and `specs/request-window` for the required behaviour.

The current code has these properties:

- `WidgetWindow.RequestBtn_Click` and the tray item `tray_request` in `AgentController` call `Process.Start("http://localhost:5050/request?user=<name>", UseShellExecute = true)`. The shell starts the default browser. A new database has 15 blocked browser rules (`blocked-programs`), so the service closes this browser as a Blocked Start. Found on VM 102 during the check of PR #42.
- `blocked-programs` never blocks or suspends an exe with the file name `msedgewebview2.exe`, an exe below the Windows folder, or an exe below the install folder.
- The agent is a WPF and WinForms app (`net8.0-windows`, self-contained `win-x64` in `build.ps1`). It has no WebView2 reference today.
- The agent texts are in `AgentTexts` (Domain, "de", "en", "id"). The parent cannot edit them. The service sends them in `/api/agent/status`.
- The agent hides the widget when `/api/agent/status` says `isRestricted: false`, but the tray item `tray_request` stays.
- `/request` takes the user from `?user=`, else from the console session. It redirects an unrestricted console user without `?user=` to `/admin`. `/request` and `/api/request` are open child-facing routes (`admin-access`).
- The web server listens on `[::]:5050` in dual mode with `SO_EXCLUSIVEADDRUSE` (`WebServerHost`), so `127.0.0.1:5050` belongs to the service while it runs.
- PR #42 (merged) moved the request page to React: `/request` serves `request.html` (`RequestSpa`), the assets come from `/admin/assets/`, the page posts to `/api/request`, and the response has a strict CSP (`default-src 'self'; script-src 'self'`, `img-src 'self' data: blob:`, `frame-ancestors 'none'`). The page loads no resource from another origin.
- PR #40 (merged) added the Control Time Zone and the storage lockdown. The Agent Window Budget uses no time of day, so the Control Time Zone has no effect on it.
- The installer (`installer/ParentalControl.iss`) stops the service at `ssInstall` and starts it at `ssPostInstall`. It has no `SetupLogging`, and the updater (`UpdateService.LaunchInstaller`) runs it with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` and without `/LOG`. Inno Setup runs each `[Run]` entry without `postinstall` before `ssPostInstall` (`Setup.MainForm.pas`: `ProcessRunEntries`, then `SetStep(ssPostInstall)`).
- Category Usage on main comes from the agent sample (`ActivityTracker`, `GetForegroundWindow`). A WebView2 control draws in child windows of the host window, so `GetForegroundWindow` gives the top-level window of the agent. The default App Rules put the install folder in System, so the agent counts nothing.
- PR #43 (`session-probe-and-agent-channel`, a proposal, not merged) replaces the agent sample with a Session Probe that runs as SYSTEM. In it (D5), a trusted `msedgewebview2.exe` (below the Evergreen folders of `%ProgramFiles(x86)%` or `%ProgramFiles%`) counts as its creator, the Session Agent counts in Unknown whatever the App Rules say, and a counted program that is never suspended locks the session after 60 seconds in 10 minutes if its category is exhausted. Only a parent prompt stops this count. The service adds the interval of a sample to each category at most once (D5 rule 6), and the agent is often counted only as the creator of the `msedgewebview2.exe` that has the focus (D5 rule 3). The widget gets `WS_EX_NOACTIVATE` so that a click does not make it the Foreground Window.

Each decision below was decided by the orchestrator under the user's delegation (2026-11-27). The revision of 2026-11-27 answers "Review 1" on PR #54 (S1–S7, N1–N11). Each new rule of it is decided by the orchestrator under the user's delegation, and each changed decision names the finding.

## Goals / Non-Goals

**Goals:**
- The child can send a Grace Request on a PC with all default browsers blocked.
- The fix gives the child no general browser and no way around the block list without a script.
- The block list and the blocked-start logic stay unchanged.
- The same page as `/request` in a browser, so PR #42 needs no change, and a later page change needs no agent change.

**Non-Goals:**
- Protection against code that the child injects into its own agent process (PR #43 D13 lists this class as accepted).
- A request while the session is locked.
- A WebView2 for other agent windows (prompt dialog, settings).

## Decisions

### D1. The request page in a WebView2 window of the agent
The agent opens a WPF window, the Request Window, with the `Microsoft.Web.WebView2.Wpf.WebView2` control, and loads `http://127.0.0.1:5050/request?user=<Environment.UserName>`.

Why: `msedgewebview2.exe` is already never a Blocked Program, so no block rule changes. The window shows the real page, so each function of the page works without a second UI: the countdown, the decline banner, the week strip, the request for more time and the request for a schedule change. The agent is in the install folder, so its window counts in System today, as the widget does.

Rejected alternatives:
- **A native WPF form that posts to `/api/request`.** It needs no runtime, but it duplicates the page: the texts, the request limits, the decline banner, the week strip and the schedule request. Each change of the page (PR #42 and later) needs a second change in the agent. Its only advantage is the case without a runtime, which D6 and D7 cover.
- **Allow a browser start whose only URL is the request page.** The child controls the command line of a process that it starts, and the browser can navigate anywhere after the start. The spec `blocked-programs` says in "Process data for a block is untrusted" that the command line SHALL NOT change whether a Blocked Program is closed. This option breaks that requirement.
- **Allow the browser for the request URL with a browser policy** (`URLAllowlist` and `URLBlocklist` for Edge). It needs a change of `blocked-programs` for `msedge.exe`, policy keys that the installer must own, and it gives the child a full browser for one site. A later policy error opens the internet.

### D2. The URL filter
A pure class `RequestWindowUrlPolicy` (Domain) decides each URL:
- A document (the main frame or any frame) is allowed only if the scheme is `http`, the host is `127.0.0.1`, the port is `5050`, and the path is `/request` (exact, case-insensitive). A query is allowed. Each other scheme is refused, for example `https`, `file`, `data`, `javascript`, `about`, `edge`, `view-source`, `blob`.
- Each other resource is allowed only from the origin `http://127.0.0.1:5050`.

The agent applies the policy in these WebView2 events. It adds each handler and the filter before the first navigation (D6a):
- `CoreWebView2.NavigationStarting` and `CoreWebView2.FrameNavigationStarting`: `Cancel = true` for a refused URL. These events also fire for a redirect and for a script navigation. A redirect keeps the `NavigationId` of the first event (Microsoft Learn, `NavigationStarting`). The agent records each `NavigationId` that it cancelled, at the first event or at a redirect (D6).
- `CoreWebView2.WebResourceRequested` with the filter `AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All)` (Review 1, S4). Microsoft Learn calls the two-argument overload deprecated: it fires only for the main frame and its same-origin frames. The page of the three-argument overload says that a filter must be added before the main page navigation to get all events. For a refused origin, the response is `CreateWebResourceResponse(null, 403, "Forbidden", "")`. The CSP of the page already refuses other origins; this is a second layer that does not depend on the page.
- `CoreWebView2.NewWindowRequested`: `Handled = true` and no `NewWindow`. If the event is not handled, WebView2 opens its own popup window, which can show any URL.
- `CoreWebView2.DownloadStarting`: `Cancel = true`.
- `CoreWebView2.LaunchingExternalUriScheme`: `Cancel = true`.
- `CoreWebView2.PermissionRequested`: `State = Deny`.
- `CoreWebView2.BasicAuthenticationRequested`: `Cancel = true`.

The page uses `127.0.0.1`, not `localhost`. The filter then compares one fixed host, and the name resolution of `localhost` (IPv4 or IPv6) has no effect.

### D3. The settings of the web view
`CoreWebView2Settings`: `AreDevToolsEnabled = false`, `AreDefaultContextMenusEnabled = false`, `AreBrowserAcceleratorKeysEnabled = false` (F12, Ctrl+P, Ctrl+F, Ctrl+U, F5 and others; the edit keys and copy and paste stay), `IsStatusBarEnabled = false`, `AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false`, `IsGeneralAutofillEnabled = false`, `IsPasswordAutosaveEnabled = false`, `IsSwipeNavigationEnabled = false`, `IsBuiltInErrorPageEnabled = false`. Browser extensions stay off (`AreBrowserExtensionsEnabled` is false by default).

The page of `AreBrowserAcceleratorKeysEnabled` names F12 and Ctrl+Shift+C for the developer tools (Review 1, N4). `AreDevToolsEnabled = false` covers each key of the developer tools, also Ctrl+Shift+I; task 7.4 tests F12, Ctrl+Shift+I and Ctrl+Shift+C.

`CoreWebView2Controller.AllowExternalDrop = false`. Microsoft Learn describes this setting for dropped files only (Review 1, N3). A dropped link or text starts a navigation, and the document filter of D2 cancels it. Task 7.4 tests both.

Microsoft Learn (`CoreWebView2Settings`): a setting change after `NavigationStarting` applies only from the next top-level navigation, except the two autofill settings. So D6a sets each setting before the first navigation.

The page needs no host object and no web message, so the page does not change.

### D4. Only the machine-wide Evergreen runtime, without user overrides
The WebView2 loader reads overrides from the environment variables `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER`, `WEBVIEW2_USER_DATA_FOLDER`, `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, `WEBVIEW2_CHANNEL_SEARCH_KIND`, `WEBVIEW2_RELEASE_CHANNELS` and others with the prefix `WEBVIEW2_` (for example `WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER`), and from the registry, first `HKLM` and then `HKCU`. An override replaces the folder parameters of `CreateCoreWebView2EnvironmentWithOptions`, and extra browser arguments are appended. The child can set its own user environment variables, for example `--remote-debugging-port`, and a debug port gives full control of the web view.

So, before the agent creates the environment:
1. It removes each variable whose name starts with `WEBVIEW2_` from its own process (`Environment.SetEnvironmentVariable(name, null)`). The variables of the user stay as they are. After PR #43, the service also removes them from the launch environment of the agent (task 6.1, Review 1 N10).
2. It reads the version `pv` of the Evergreen runtime from `HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` in the 32-bit view (`RegistryView.Registry32`). Only administrators can write this key.
3. It builds `<ProgramFilesX86>\Microsoft\EdgeWebView\Application\<pv>` and checks that `msedgewebview2.exe` exists there. `<ProgramFilesX86>` comes from the known folder of Windows, `Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)`, never from the variable `%ProgramFiles(x86)%` (Review 1, S5). The environment of the agent comes from `CreateEnvironmentBlock` or Explorer, which include `HKCU\Environment`. If a user variable `ProgramFiles(x86)` overrides the system value, a folder from the variable would be a folder of the child. This override is not verified on Windows; it is the same class as the known override of `windir`. Task 7.6 tests it.
4. It calls `CoreWebView2Environment.CreateAsync(browserExecutableFolder: <that folder>, userDataFolder: <D5>, options)`. The options have `AdditionalBrowserArguments = "--no-proxy-server"` (Review 1, N8): the child can set a WinINet proxy or a PAC file for its account without a script, and Chromium can send a loopback request to a proxy if a bypass rule `<-loopback>` is set (not verified). The page needs no proxy. The same holds for the agent itself (added after the VM check, task 3.7): each HTTP client of the Session Agent comes from `AgentHttp.Create` with `UseProxy = false`. On VM 102, a proxy `127.0.0.1:8888` with `<-loopback>` in the Internet Options of `Kid` took the agent requests to `127.0.0.1:5050`, so the agent got no status (no widget, no request item) and sent no samples and no session end hold.

The registry overrides are the keys `BrowserExecutableFolder`, `UserDataFolder`, `AdditionalBrowserArguments`, `ChannelSearchKind` and `ReleaseChannels` below `HKLM` or `HKCU\Software\Policies\Microsoft\Edge\WebView2`, with the exe name, the AppUserModelID or `*` as the value name (Microsoft docs: `CreateCoreWebView2EnvironmentWithOptions`, "Set the browser executable folder"). By default only administrators can write `HKCU\Software\Policies`, so the agent cannot and need not remove them. Task 7.6 proves on the VM that the loader reads this path and that the child cannot write it.

Why the explicit folder: the default search can pick a per-user runtime below `%LOCALAPPDATA%` that the child installed and can change. PR #43 trusts only the Evergreen folders below Program Files. An untrusted `msedgewebview2.exe` would count by its own App Rule (Unknown).

Rejected alternatives:
- **The default runtime search.** See above.
- **A Fixed Version runtime in the install folder.** About 170 MB more in the setup, its own security updates, and PR #43 does not trust it.

### D5. The data folder and InPrivate
The data folders are below `%LOCALAPPDATA%\ParentalControl\WebView2`. The default folder is next to the exe in the install folder, which the child cannot write, so the default fails. The controller uses `CoreWebView2ControllerOptions` with `IsInPrivateModeEnabled = true`, so the profile keeps no cookies, storage or history after the window closes. The agent disposes the web view when the window closes; the browser process then ends.

Each open uses a new, empty subfolder with a random name (a GUID) (Review 1, N7). The child can write the data folder with Notepad, also `Local State` and `Preferences`, without a script. No known pref turns on the developer tools or a debug port against the API settings, but this is not tested, and a new folder removes the question. A browser process can still hold the old folder for a short time after a close, so a delete of one fixed folder before each open would fail on a quick second open. So at each open the agent first deletes each old subfolder that it can delete, and ignores an error: a folder that is still in use goes at a later open. The agent runs as the child, so this delete can remove nothing that the child cannot remove. Task 7.5 tests a planted `Preferences` and `Local State` file.

Rejected alternatives:
- **One fixed folder, deleted before each open.** It fails on a quick second open while the old browser process still runs.
- **One fixed folder that the agent keeps.** The first version of this design chose it, because the protection is in the agent. Review 1 (N7) showed that the child can plant files in it without a script.
- **A folder below `%TEMP%`.** Windows can clean it while the browser process runs.

### D6. The window, the fallback and the user check
- One Request Window per agent. A second click calls `Activate()` on the open window. The window is a normal window with a taskbar button. It is not `Topmost`, because the child must be able to switch to another window. It takes the activation, because the child types in it (unlike the widget of PR #43).
- The title is `request_window_title`. New keys in `AgentTexts`: `request_window_title` ("Mehr Zeit wünschen", "Ask for More Time", "Minta Tambahan Waktu") and `request_window_unavailable` ("Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt.", "The request window does not work right now. Ask your parents directly.", "Jendela permintaan sedang tidak bisa dibuka. Tanyakan langsung ke orang tuamu.").
- Fallback: the window shows `request_window_unavailable` in a WPF text block and closes the web view in these cases:
  - Step 2 or 3 of D4 finds no runtime.
  - A step of the setup in D6a throws, for example `CreateAsync`, `EnsureCoreWebView2Async` (`WebView2RuntimeNotFoundException`), a setting, or the subscription to an event that the installed runtime does not have (Review 1, S3).
  - The first navigation of the window completes with `IsSuccess = false`, for any reason (Review 1, N1). This also covers a first navigation whose redirect the filter cancelled, for example a redirect of `/request` to `/admin` after a change of the user status or a name mismatch. Without this rule, the window would stay empty.
  - A later main-document navigation completes with `IsSuccess = false`, and the agent did not cancel its `NavigationId` (D2).
  - `ProcessFailed` reports a failed browser process.
- A navigation that the agent cancelled after the first one shows no fallback, whatever its `WebErrorStatus` is. So a refused link keeps the page. This rule does not depend on the status that WebView2 reports for a cancelled navigation, which Microsoft Learn does not document for `NavigationStarting` (Review 1, N2). Task 7.4 records that status.
- The next open creates a new web view. The agent has no code path that starts a browser: both `Process.Start` calls go.
- The widget is already hidden for an unrestricted user. The tray item `tray_request` is hidden too, and an open Request Window closes, when the last status that the agent received says `isRestricted: false`. Before the agent has received a status, it offers no request (Review 1, N5). So the redirect of `/request` to `/admin` does not show an empty window in the normal case, and the first-navigation rule covers the rest.

### D6a. The setup order: fail closed (Review 1, S3)
Microsoft Learn says that a setting change after `NavigationStarting` applies only from the next top-level navigation. And the SDK can throw when the agent subscribes to an event that the installed runtime does not have, also after the creation succeeded. A partial lockdown must not show the page. So the Request Window sets up the web view in this fixed order:
1. The XAML of the window has no `Source` on the WebView2 control. Microsoft Learn: for .NET, the environment must be given before the `Source` property takes effect. A `Source` before `EnsureCoreWebView2Async(environment, options)` would start the control with the default environment and skip D4 and D5.
2. `CoreWebView2Environment.CreateAsync` (D4) and `EnsureCoreWebView2Async` with the `CoreWebView2ControllerOptions` of D5.
3. Each setting of D3, `AllowExternalDrop = false`, each event handler of D2 and D6, and the resource filter of D2.
4. Only then `CoreWebView2.Navigate("http://127.0.0.1:5050/request?user=<name>")`.

One `try` block covers steps 2 to 4. Each exception shows `request_window_unavailable` and closes the web view, and step 4 does not run. There is no other code path that gives the web view a URL.

### D7. The installer installs the Evergreen runtime
`build.ps1` downloads the Evergreen Bootstrapper `MicrosoftEdgeWebview2Setup.exe` (about 2 MB) and checks its Authenticode signature (`Get-AuthenticodeSignature`: status `Valid`, signer "Microsoft Corporation") before it packs it. The URL is one constant in `build.ps1`: the link that the "Get the Link" button of the Evergreen Bootstrapper on the WebView2 download page gives (Review 1, N6). The fwlink `https://go.microsoft.com/fwlink/p/?LinkId=2124703` is the expected value, but the distribution page does not name it, so task 5.1 copies the link from the button. The signature check is the control, not the URL. The bootstrapper may be redistributed with an app.

`installer/ParentalControl.iss` (Review 1, S6):
- `[Setup]`: `SetupLogging=yes`. Without it, `Log` writes nothing, and the claim "the setup log has the exit code" is not true. Inno Setup then writes `Setup Log <date> #<n>.txt` to the temp folder of the user that runs the setup.
- `[Files]`: the bootstrapper with `Flags: dontcopy`. It is not extracted when the runtime is present.
- No `[Run]` entry for the bootstrapper. Inno Setup runs each `[Run]` entry without `postinstall` before `ssPostInstall`, and the service starts only at `ssPostInstall`. A `[Run]` entry would keep the service stopped while the bootstrapper downloads the runtime, or until it gives up offline, and no limit works in that time.
- `CurStepChanged(ssPostInstall)`: after `StartService()`, if `IsWebView2RuntimeInstalled` is false, `ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe')` and `Exec` of `{tmp}\MicrosoftEdgeWebview2Setup.exe /silent /install` with `ewWaitUntilTerminated` and `SW_HIDE`. `Log` writes the result of `Exec` and the exit code. A failure does not stop the setup. The setup runs as an administrator, so the runtime installs for all users below `%ProgramFiles(x86)%`.
- `IsWebView2RuntimeInstalled` (Pascal script) reads `pv` with `RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', ...)`. The setup uses `ArchitecturesInstallIn64BitMode`, so a plain `HKLM` reads the 64-bit view and does not find the key below `WOW6432Node`. The runtime is present if `pv` is not empty and not `0.0.0.0`; this is the check of the Microsoft docs.

The updater (`UpdateService.LaunchInstaller`) adds `/LOG="<DatabaseManager.LogsDirectory>\setup-<yyyyMMdd-HHmmss>.log"` to its arguments. The service runs the setup as SYSTEM, so a log in the temp folder of SYSTEM is hard to find; the logs folder of the data folder is where the parent finds the service log too.

Inno Setup has no function for the Windows event log. So the setup writes only its log, and the service checks the runtime itself. It reads `pv` the same way (a pure decision in Domain, the registry read in Core) 15 minutes after its start and then once each day, and writes a warning event with the next free event ID of the service (1019 on main today) if the runtime is missing, at most one each day. The service starts before the bootstrapper, so a check at the start would warn on each first install that is online; 15 minutes give the bootstrapper time to download and install the runtime. The parent sees the event in the event log, and the Request Window shows the fallback text.

Rejected alternatives:
- **The Evergreen Standalone Installer** in the setup: works offline, but adds about 170 MB to each download and each update.
- **An install of the runtime by the agent.** The agent runs as the child, so it can only install a per-user runtime (see D4).
- **A `[Run]` entry for the bootstrapper.** It runs while the service is stopped (see above).
- **`eventcreate` from the setup.** It accepts only event IDs 1 to 1000 and can write only to a source that it created itself (not verified), so it cannot write as the service source.

### D8. Interaction with session-probe-and-agent-channel: the Agent Window Budget
On main, the Request Window counts nothing: the agent is the Foreground Window and is in System. After PR #43, the trusted `msedgewebview2.exe` of the window counts as its creator, the agent, and the agent counts in Unknown. The Request Window takes the activation and the keyboard, so the non-activation of the widget does not apply. A child asks for more time exactly when time runs short. If Unknown is exhausted, the rule "A program that is never suspended still has a limit" of PR #43 locks the session after about one minute of typing. And the child types in a window that is there to ask for time.

Decision: the Agent Window Budget (revised for Review 1, S1 and S2).
- **Input.** Only the samples of the Session Probe of PR #43 and the lock state of the session, facts that the service has itself. The budget has no input from the HTTP agent routes. On main, before PR #43, no service code calls it (Review 1, S1): the agent samples of `POST /api/agent/activity` are unauthenticated, and the agent would claim itself that the exe is the agent exe. With the default rules this changes nothing on main, because the install folder is in System. PR #43 D11 removes that route in the release that makes the budget matter.
- **What is exempt.** The Session Agent as a counted process of a sample, in any role, also as the creator of a trusted `msedgewebview2.exe` (D5 rule 3 of PR #43). The focus of the Request Window is in the WebView2 child window, so the agent is often counted only in this way.
- **Only the agent's own contribution.** PR #43 adds the interval of a sample to each category at most once. If another counted process of the same sample is in the same category (for example a game that a script moved into the agent window with `SetParent`), that category still gets the interval. The exemption removes only the entry of the agent.
- **Which rules it touches.** Exempt time adds no Category Usage for the agent and does not count toward the lock of a program that is never suspended (60 seconds in 10 minutes, PR #43 D5). It does not change the Allowance Time, Continuous Use, a Break or the Schedule Window: these count unlocked session time and never read the sample.
- **The unit.** A sliding window: in any 60 minutes of unlocked time, at most 300 exempt seconds. The pure class `AgentWindowBudget` (Domain) keeps the exempt seconds of each sample with the unlocked time at which it was used. For each sample with the agent as a counted process, it gives the exempt part of the sample interval: the interval, or less if the last 60 minutes of unlocked time already have 300 exempt seconds. Locked time, time in sleep or hibernation (as in PR #43 S20), and time without a session do not move the window. The clock is monotonic, not the time of day, so the Control Time Zone has no effect. The state is in memory; a service start begins with an empty budget. The child cannot restart the service.
- **The other agent windows.** The probe cannot tell the Request Window from the widget or the settings dialog, because the agent controls its window titles and classes. So the budget is for each agent window. It also lowers the accepted cost B6 of PR #43 (the settings dialog).
- **The widget exemption of PR #43.** If PR #43 keeps it (at most 60 seconds per 10 minutes after the cursor is on the widget), its time counts against this one budget.
- **The prompt exemption of PR #43** stays separate. It depends on service state (an active prompt of the parent), not on time.
- **The bound for injected code (Review 1, S7).** Code that the child injects into its agent and that draws a game in the agent window gets the budget, at most 5 minutes in any 60 minutes of unlocked time, and on top of that the 60 seconds in 10 minutes that PR #43 allows before its lock. So it is not "5 minutes per hour" in total.

Rejected alternatives:
- **Accept the lock** as PR #43 accepts it for the settings dialog (B6). The settings dialog is rare. The Request Window is the one window that the child opens when time is short.
- **An exemption while the agent says that the window is open.** A limit would then depend on agent data (PR #43 D10), and a script could keep the exemption on forever.
- **The budget on main in the handler of the agent sample.** The first version of this design had this as task 4.1. Review 1 (S1) showed that it reads unauthenticated agent data, against the last SHALL of the requirement.
- **An exemption after a Grace Request.** The request arrives after the typing, so the lock comes first.
- **A separate exe for the Request Window** in the install folder. It runs as the child too, so code injection into it is as easy, and PR #43 would need the same rule for it.

Order of the changes: this change adds the budget as ADDED requirements of the new capability `request-window`, so it validates against the specs of main (Review 1, N11). The budget is a rule of `category-limits`. Task 6.1 runs `/opsx:update` on `session-probe-and-agent-channel` so that its D5, its widget exemption, its requirement "A program that is never suspended still has a limit" and its two scenarios with "locked within 2 minutes" name the budget, and so that its tasks wire `AgentWindowBudget` into the probe counting. The change that is archived second moves the two budget requirements into `category-limits`, so that the rule is in one place only. If PR #43 is archived first, task 6.1 does this in a follow-up change instead.

### D9. The CSP of the request page and PR #42
The web view enforces the CSP of the page as a browser does. The page of PR #42 loads only same-origin assets from `/admin/assets/` and posts to `/api/request`, so D2 allows it. A `data:` or `blob:` image that the CSP allows is no network request from another origin. `frame-ancestors 'none'` has no effect on a top-level document in a web view. The page needs no host object, no web message and no change.

## Risks / Trade-offs

- [The PC has no WebView2 Runtime and was offline at the install] → The window shows `request_window_unavailable`. The setup log has the exit code of the bootstrapper, and the service writes a warning event, at most one each day. The next install or update tries again. The parent can install the runtime by hand. Windows 10 22H2 often has the runtime (Microsoft installs it with Edge and Office), but this is not sure; task 7.1 records the state on `test-base`.
- [The bootstrapper takes long] → The setup waits for it, but the service already runs again (D7), so the limits work during the download.
- [Code injected into the agent] → The child owns the agent process. Injected code can navigate the web view anywhere or open a debug connection. This is the accepted class "Code that the child injects" of PR #43 D13. This change adds no bypass that works without a script. After PR #43, the free time for a game drawn in the agent window is the budget (at most 5 minutes in any 60 minutes of unlocked time) plus the 60 seconds in 10 minutes before the lock of PR #43 (D8). On main, injected code can already draw anything in the agent window, which counts in System.
- [The child waits in the Request Window for the answer of the parent] → After PR #43, with Unknown exhausted, a child who keeps the Request Window in front uses the 5 minutes of the budget and is then locked after about one more minute (Review 1, N9). Decided by the orchestrator under the user's delegation: the window stays open, and this cost is accepted. The answer of the parent comes in the widget (the decline toast, or the new remaining time), and the page does not update itself, so the child has no reason to keep the window in front; the request page shows a success message after the send. An automatic close after a successful request would need the agent to read the responses of the page (`WebResourceResponseReceived`), which adds code for a small gain. If the test on the child's PC shows that the child waits in the window, a follow-up change adds the close.
- [A program holds port 5050 while the service does not run] → The window then shows the page of that program, because the filter allows the origin. While the service runs, it holds the port exclusively. While it does not run, no limit works anyway, and after PR #43 the agent exits when its channel breaks.
- [The Evergreen runtime updates while the window is open] → The old version folder stays while a process uses it. The agent reads `pv` again at each open.
- [The WebView2 SDK needs a newer runtime than the PC has] → `LaunchingExternalUriScheme`, `CoreWebView2ControllerOptions` and the three-argument resource filter need a recent runtime. The Evergreen runtime updates itself. If the runtime is too old, `CreateAsync`, a setting or an event subscription throws, and D6a shows the fallback text without a navigation. Task 1.1 pins a current stable SDK.
- [Memory] → The web view uses about 100 MB while the window is open. It ends when the window closes.
- [A game copied as `msedgewebview2.exe`] → Unchanged by this change; issue #48 covers it.

## Migration Plan

- No data migration. The new agent text keys come from `AgentTexts`; the parent cannot edit agent texts.
- The installer installs the runtime if it is missing. An update of an existing install runs the same check.
- Rollback: an older version opens the default browser again, which the block list closes. The runtime stays installed; it is a part of Windows that other apps use.
- Release notes: the request button opens a window of Parental Control, not the browser.
