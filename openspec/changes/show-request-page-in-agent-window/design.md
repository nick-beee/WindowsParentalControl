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
- The current C# request page (`RequestPage.cs`) loads no resource from another origin. PR #42 (about to merge) moves the page to React: `/request` serves `request.html`, the assets come from `/admin/assets/`, the page posts to `/api/request`, and the response has a strict CSP (`script-src 'self'`, `frame-ancestors 'none'`).
- Category Usage on main comes from the agent sample (`ActivityTracker`, `GetForegroundWindow`). A WebView2 control draws in child windows of the host window, so `GetForegroundWindow` gives the top-level window of the agent. The default App Rules put the install folder in System, so the agent counts nothing.
- PR #43 (`session-probe-and-agent-channel`, a proposal, not merged) replaces the agent sample with a Session Probe that runs as SYSTEM. In it (D5), a trusted `msedgewebview2.exe` (below the Evergreen folders of `%ProgramFiles(x86)%` or `%ProgramFiles%`) counts as its creator, the Session Agent counts in Unknown whatever the App Rules say, and a counted program that is never suspended locks the session after 60 seconds in 10 minutes if its category is exhausted. Only a parent prompt stops this count. The widget gets `WS_EX_NOACTIVATE` so that a click does not make it the Foreground Window.

Each decision below was decided by the orchestrator under the user's delegation (2026-11-27).

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

The agent applies the policy in these WebView2 events:
- `CoreWebView2.NavigationStarting` and `CoreWebView2.FrameNavigationStarting`: `Cancel = true` for a refused URL. These events also fire for a redirect and for a script navigation.
- `CoreWebView2.WebResourceRequested` with the filter `"*"` and `CoreWebView2WebResourceContext.All`: for a refused origin, the response is `CreateWebResourceResponse(null, 403, "Forbidden", "")`. The CSP of the page already refuses other origins; this is a second layer that does not depend on the page.
- `CoreWebView2.NewWindowRequested`: `Handled = true` and no `NewWindow`. If the event is not handled, WebView2 opens its own popup window, which can show any URL.
- `CoreWebView2.DownloadStarting`: `Cancel = true`.
- `CoreWebView2.LaunchingExternalUriScheme`: `Cancel = true`.
- `CoreWebView2.PermissionRequested`: `State = Deny`.
- `CoreWebView2.BasicAuthenticationRequested`: `Cancel = true`.

The page uses `127.0.0.1`, not `localhost`. The filter then compares one fixed host, and the name resolution of `localhost` (IPv4 or IPv6) has no effect.

### D3. The settings of the web view
`CoreWebView2Settings`: `AreDevToolsEnabled = false`, `AreDefaultContextMenusEnabled = false`, `AreBrowserAcceleratorKeysEnabled = false` (F12, Ctrl+P, Ctrl+F, Ctrl+U, F5 and others; the edit keys and copy and paste stay), `IsStatusBarEnabled = false`, `AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false`, `IsGeneralAutofillEnabled = false`, `IsPasswordAutosaveEnabled = false`, `IsSwipeNavigationEnabled = false`, `IsBuiltInErrorPageEnabled = false`. `CoreWebView2Controller.AllowExternalDrop = false`, so a dropped link or file does not start a navigation. Browser extensions stay off (`AreBrowserExtensionsEnabled` is false by default).

The page needs no host object and no web message, so the page does not change.

### D4. Only the machine-wide Evergreen runtime, without user overrides
The WebView2 loader reads overrides from the environment variables `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER`, `WEBVIEW2_USER_DATA_FOLDER`, `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, `WEBVIEW2_CHANNEL_SEARCH_KIND` and `WEBVIEW2_RELEASE_CHANNELS`, and from the registry, first `HKLM` and then `HKCU`. An override replaces the folder parameters of `CreateCoreWebView2EnvironmentWithOptions`, and extra browser arguments are appended. The child can set its own user environment variables, for example `--remote-debugging-port`, and a debug port gives full control of the web view.

So, before the agent creates the environment:
1. It removes each variable whose name starts with `WEBVIEW2_` from its own process (`Environment.SetEnvironmentVariable(name, null)`). The variables of the user stay as they are.
2. It reads the version `pv` of the Evergreen runtime from `HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` in the 32-bit view (`RegistryView.Registry32`). Only administrators can write this key.
3. It builds `%ProgramFiles(x86)%\Microsoft\EdgeWebView\Application\<pv>` and checks that `msedgewebview2.exe` exists there.
4. It calls `CoreWebView2Environment.CreateAsync(browserExecutableFolder: <that folder>, userDataFolder: <D5>, options)`.

The registry overrides are below `Software\Policies\Microsoft\Edge\WebView2` (per Microsoft docs). By default only administrators can write `HKCU\Software\Policies`. Task 7.6 checks this on the VM.

Why the explicit folder: the default search can pick a per-user runtime below `%LOCALAPPDATA%` that the child installed and can change. PR #43 trusts only the Evergreen folders below Program Files. An untrusted `msedgewebview2.exe` would count by its own App Rule (Unknown).

Rejected alternatives:
- **The default runtime search.** See above.
- **A Fixed Version runtime in the install folder.** About 170 MB more in the setup, its own security updates, and PR #43 does not trust it.

### D5. The data folder and InPrivate
The user data folder is `%LOCALAPPDATA%\ParentalControl\WebView2`. The default folder is next to the exe in the install folder, which the child cannot write, so the default fails. The controller uses `CoreWebView2ControllerOptions` with `IsInPrivateModeEnabled = true`, so the profile keeps no cookies, storage or history after the window closes. The agent disposes the web view when the window closes; the browser process then ends.

The child can write the data folder. That is accepted: the filter and the settings are in the agent process, not in the folder.

Rejected alternatives:
- **A new random folder at each open.** A slower start, and no gain, because the protection is in the agent.
- **A folder below `%TEMP%`.** Windows can clean it while the browser process runs.

### D6. The window, the fallback and the user check
- One Request Window per agent. A second click calls `Activate()` on the open window. The window is a normal window with a taskbar button. It is not `Topmost`, because the child must be able to switch to another window. It takes the activation, because the child types in it (unlike the widget of PR #43).
- The title is `request_window_title`. New keys in `AgentTexts`: `request_window_title` ("Mehr Zeit wünschen", "Ask for More Time", "Minta Tambahan Waktu") and `request_window_unavailable` ("Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt.", "The request window does not work right now. Ask your parents directly.", "Jendela permintaan sedang tidak bisa dibuka. Tanyakan langsung ke orang tuamu.").
- Fallback: the window shows `request_window_unavailable` in a WPF text block if step 2 or 3 of D4 finds no runtime, if `CreateAsync` or `EnsureCoreWebView2Async` throws (for example `WebView2RuntimeNotFoundException`), if `NavigationCompleted` of the main document has `IsSuccess = false`, or if `ProcessFailed` reports a failed browser process. The next open creates a new web view. The agent has no code path that starts a browser: both `Process.Start` calls go.
- The widget is already hidden for an unrestricted user. The tray item `tray_request` is hidden too, and an open Request Window closes, when the status says `isRestricted: false`. So the redirect of `/request` to `/admin` does not show an empty window.

### D7. The installer installs the Evergreen runtime
`build.ps1` downloads the Evergreen Bootstrapper `MicrosoftEdgeWebview2Setup.exe` (about 2 MB, from `https://go.microsoft.com/fwlink/p/?LinkId=2124703`) and checks its Authenticode signature (`Get-AuthenticodeSignature`: status `Valid`, signer "Microsoft Corporation") before it packs it. The bootstrapper may be redistributed with an app. `installer/ParentalControl.iss`:
- `[Files]`: the bootstrapper to `{tmp}` with `deleteafterinstall`.
- `[Run]`: `"{tmp}\MicrosoftEdgeWebview2Setup.exe" /silent /install`, `Flags: waituntilterminated runhidden`, `Check: not IsWebView2RuntimeInstalled`. The setup runs as an administrator, so the runtime installs for all users below `%ProgramFiles(x86)%`.
- `IsWebView2RuntimeInstalled` (Pascal script) reads `pv` of the key in D4. The runtime is present if `pv` is not empty and not `0.0.0.0`; this is the check of the Microsoft docs.
- A failure of the bootstrapper does not stop the setup. The setup log has its exit code.

Rejected alternatives:
- **The Evergreen Standalone Installer** in the setup: works offline, but adds about 170 MB to each download and each update.
- **An install of the runtime by the agent.** The agent runs as the child, so it can only install a per-user runtime (see D4).

### D8. Interaction with session-probe-and-agent-channel: the Agent Window Budget
On main, the Request Window counts nothing: the agent is the Foreground Window and is in System. After PR #43, the trusted `msedgewebview2.exe` of the window counts as its creator, the agent, and the agent counts in Unknown. The Request Window takes the activation and the keyboard, so the non-activation of the widget does not apply. A child asks for more time exactly when time runs short. If Unknown is exhausted, the rule "A program that is never suspended still has a limit" of PR #43 locks the session after about one minute of typing. And the child types in a window that is there to ask for time.

Decision: the Agent Window Budget. In each 60 minutes of unlocked time of a Restricted User, the first 5 minutes in which the Session Agent is a counted process add no Category Usage and count toward no lock. After that, the rules apply as before. The budget uses only facts that the service has itself (the probe samples and the lock state of the session). The agent cannot start, extend or end it. A pure class `AgentWindowBudget` (Domain) keeps the exempt seconds in a sliding window of 60 minutes of unlocked time.

- The probe cannot tell the Request Window from the widget or the settings dialog, because the agent controls its window titles and classes. So the budget is for each agent window. It also lowers the accepted cost B6 of PR #43 (the settings dialog).
- If PR #43 keeps its widget exemption (at most 60 seconds per 10 minutes after the cursor is on the widget), the time of both counts against this one budget.
- The prompt exemption of PR #43 stays separate. It depends on service state (an active prompt of the parent), not on time.
- The free time is bounded: code that the child injects into its agent and that draws a game in the agent window gets at most 5 minutes per hour.
- On main, the service applies the budget to an agent sample whose exe is the agent exe. With the default rules this changes nothing (System counts nothing). It matters only if the parent maps the install folder to another category.

Rejected alternatives:
- **Accept the lock** as PR #43 accepts it for the settings dialog (B6). The settings dialog is rare. The Request Window is the one window that the child opens when time is short.
- **An exemption while the agent says that the window is open.** A limit would then depend on agent data (PR #43 D10), and a script could keep the exemption on forever.
- **An exemption after a Grace Request.** The request arrives after the typing, so the lock comes first.
- **A separate exe for the Request Window** in the install folder. It runs as the child too, so code injection into it is as easy, and PR #43 would need the same rule for it.

Order of the changes: this change adds the budget as an ADDED requirement of the new capability `request-window`, so it validates against the specs of main. Task 6.1 runs `/opsx:update` on `session-probe-and-agent-channel` so that its D5 and its requirement "A program that is never suspended still has a limit" name the budget. If PR #43 is archived first, task 6.1 changes `category-limits` in a follow-up change instead.

### D9. The CSP of the request page and PR #42
The web view enforces the CSP of the page as a browser does. The page of PR #42 loads only same-origin assets from `/admin/assets/` and posts to `/api/request`, so D2 allows it. The current C# page loads no other origin either, so the order of this change and PR #42 does not matter. `frame-ancestors 'none'` has no effect on a top-level document in a web view. The page needs no host object, no web message and no change.

## Risks / Trade-offs

- [The PC has no WebView2 Runtime and was offline at the install] → The window shows `request_window_unavailable`. The setup log has the exit code of the bootstrapper. The next install or update tries again. The parent can install the runtime by hand. Windows 10 22H2 often has the runtime (Microsoft installs it with Edge and Office), but this is not sure; task 7.1 records the state on `test-base`.
- [Code injected into the agent] → The child owns the agent process. Injected code can navigate the web view anywhere or open a debug connection. This is the accepted class "Code that the child injects" of PR #43 D13. This change adds no bypass that works without a script. The Agent Window Budget bounds the free time to 5 minutes per hour after PR #43; on main, injected code can already draw anything in the agent window, which counts in System.
- [A program holds port 5050 while the service does not run] → The window then shows the page of that program, because the filter allows the origin. While the service runs, it holds the port exclusively. While it does not run, no limit works anyway, and after PR #43 the agent exits when its channel breaks.
- [The Evergreen runtime updates while the window is open] → The old version folder stays while a process uses it. The agent reads `pv` again at each open.
- [The WebView2 SDK needs a newer runtime than the PC has] → `LaunchingExternalUriScheme` and `CoreWebView2ControllerOptions` need a recent runtime. The Evergreen runtime updates itself. If the runtime is too old, `CreateAsync` or the event subscription throws, and the window shows the fallback text. Task 1.1 pins a current stable SDK.
- [Memory] → The web view uses about 100 MB while the window is open. It ends when the window closes.
- [A game copied as `msedgewebview2.exe`] → Unchanged by this change; issue #48 covers it.

## Migration Plan

- No data migration. The new agent text keys come from `AgentTexts`; the parent cannot edit agent texts.
- The installer installs the runtime if it is missing. An update of an existing install runs the same check.
- Rollback: an older version opens the default browser again, which the block list closes.
- Release notes: the request button opens a window of Parental Control, not the browser.
