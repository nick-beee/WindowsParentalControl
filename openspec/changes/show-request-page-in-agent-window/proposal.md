# Proposal

## Why

The button "Mehr Zeit wünschen" of the Session Agent opens `http://localhost:5050/request?user=<name>` in the default browser. Since stage 2 (`blocked-programs`, PR #33), each default browser is a Blocked Program, so the service closes the browser at its start, and the child cannot send a Grace Request (issue #52). The request page is the only way for the child to ask for more time, so this breaks a core function on each new install.

## What Changes

- The Session Agent shows the child request page in its own window, the Request Window, with an embedded WebView2 control. `msedgewebview2.exe` is never a Blocked Program, so no block rule is touched and no browser is allowed.
- The Request Window shows only the request page of the local service (`http://127.0.0.1:5050/request`). It refuses each other navigation, new windows, downloads, external protocols, dropped links and files, the developer tools, the context menu and the browser shortcuts. It gives the child no general browser.
- The agent uses only the machine-wide Evergreen WebView2 Runtime, ignores the WebView2 environment variables of the user, keeps its WebView2 data in a folder below `%LOCALAPPDATA%` and stores no site data between two opens.
- The installer installs the Evergreen WebView2 Runtime if it is missing and the PC is online. If the runtime is still missing, the agent shows a text of the Language Preset instead of the page. The agent never starts a browser.
- The agent offers the request button only to a Restricted User.
- A time budget for the agent window: in each 60 minutes of unlocked time, the first 5 minutes in which the Session Agent is the counted process add no Category Usage and no lock time. Today the agent counts nothing, because the install folder is in System. The budget matters after the Session Probe change (PR #43), which counts the agent in Unknown. Without it, a child who types a request while Unknown is exhausted is locked after about one minute.
- Decided by the orchestrator under the user's delegation (2026-11-27): the WebView2 window. Rejected: a native WPF form, and a browser start that is allowed for the request URL only. See design.md D1.

## Capabilities

### New Capabilities

- `request-window`: how the Session Agent shows the child request page: the window, the URL restriction, the WebView2 runtime and data, the fallback text, and the time budget of the agent window.

### Modified Capabilities

None. The specs `blocked-programs` and `child-messages` stay as they are: `msedgewebview2.exe` is already never blocked, and the requirement "Language Presets" already requires a text in each preset for each text of the Session Agent.

## Out of scope

- The content of the request page. PR #42 moves it to React; this change shows whichever page `/request` serves.
- Authentication of `/request` and `/api/request`. They stay open child-facing routes (`admin-access`).
- An exception in the block list for a browser and the request URL.
- A request for more time while the session is locked (Enforced Workstation Lock).
- A Fixed Version WebView2 Runtime in the install folder.
- The counting of the Session Agent itself. PR #43 (`session-probe-and-agent-channel`) owns it. This change only adds the budget, and task 6.1 carries it into PR #43.

## Impact

- `src/ParentalControl.Agent`: a new `RequestWindow` (WPF with the `Microsoft.Web.WebView2` control), a pure navigation filter, `WidgetWindow.RequestBtn_Click` without `Process.Start`. New NuGet package `Microsoft.Web.WebView2` (BSD license, ships `WebView2Loader.dll`).
- `src/ParentalControl.Domain`: the pure URL filter and the budget of the agent window, with xUnit tests.
- `ParentalControl.Domain/Messages`: two new agent text keys in "de", "en" and "id".
- `installer/ParentalControl.iss` and `build.ps1`: the Evergreen WebView2 bootstrapper and a check for the runtime.
- `docs/adr/0014-request-page-in-a-webview2-agent-window.md`, `GLOSSARY.md` (Request Window, Agent Window Budget).
- PR #43: its requirement "A program that is never suspended still has a limit" gets the budget (task 6.1).
