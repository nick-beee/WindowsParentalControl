# Proposal

## Why

The button "Mehr Zeit wünschen" of the Session Agent opens `http://localhost:5050/request?user=<name>` in the default browser. Since stage 2 (`blocked-programs`, PR #33), each default browser is a Blocked Program, so the service closes the browser at its start, and the child cannot send a Grace Request (issue #52). The request page is the only way for the child to ask for more time, so this breaks a core function on each new install.

## What Changes

- The Session Agent shows the child request page in its own window, the Request Window, with an embedded WebView2 control. `msedgewebview2.exe` is never a Blocked Program, so no block rule is touched and no browser is allowed.
- The Request Window shows only the request page of the local service (`http://127.0.0.1:5050/request`). It refuses each other navigation, new windows, downloads, external protocols, dropped links and files, the developer tools, the context menu and the browser shortcuts. It gives the child no general browser.
- The window fails closed: it starts its first navigation only after each setting, each event handler and the resource filter are in place. If any step of this setup fails, or the first page does not load for any reason, it shows a text of the Language Preset.
- The agent uses only the machine-wide Evergreen WebView2 Runtime from the Program Files known folder, ignores the WebView2 environment variables and the proxy of the user, and uses a new, empty data folder below `%LOCALAPPDATA%` for each open, so no site data and no planted file survives.
- The installer installs the Evergreen WebView2 Runtime if it is missing and the PC is online. It runs the runtime setup only after it has started the service again, and it writes a setup log. If the runtime is still missing 15 minutes after the service start, the service writes a warning event, and the agent shows a text of the Language Preset instead of the page. The agent never starts a browser.
- The agent offers the request button only while the last status from the service says that the user is a Restricted User.
- The Agent Window Budget: in any 60 minutes of unlocked time (a sliding window), at most 5 minutes in which the Session Agent is a counted process of a Session Probe sample add no Category Usage for the agent and do not count toward the lock of a program that is never suspended. This includes the agent as the creator of its WebView2 process. Another process in the same sample still counts, and the Allowance Time, Continuous Use, Breaks and the Schedule Window do not change. The budget reads only probe samples, never data of the Session Agent. Today the agent counts nothing, because the install folder is in System, so the budget has no effect before the Session Probe change (PR #43), which counts the agent in Unknown. Without it, a child who types a request while Unknown is exhausted is locked after about one minute.
- Decided by the orchestrator under the user's delegation (2026-11-27): the WebView2 window. Rejected: a native WPF form, and a browser start that is allowed for the request URL only. See design.md D1. The revision after "Review 1" on PR #54 is decided the same way (design.md, D2 to D8 and Risks).

## Capabilities

### New Capabilities

- `request-window`: how the Session Agent shows the child request page: the window, the URL restriction, the setup order, the WebView2 runtime and data, the fallback text, the installer, and the Agent Window Budget. The budget is a rule of `category-limits`; it is here so that this change validates against the specs of main, and it moves to `category-limits` with PR #43 (task 6.1).

### Modified Capabilities

None. The specs `blocked-programs` and `child-messages` stay as they are: `msedgewebview2.exe` is already never blocked, and the requirement "Language Presets" already requires a text in each preset for each text of the Session Agent.

## Out of scope

- The content of the request page. PR #42 moves it to React; this change shows whichever page `/request` serves.
- Authentication of `/request` and `/api/request`. They stay open child-facing routes (`admin-access`).
- An exception in the block list for a browser and the request URL.
- A request for more time while the session is locked (Enforced Workstation Lock).
- A Fixed Version WebView2 Runtime in the install folder.
- The counting of the Session Agent itself. PR #43 (`session-probe-and-agent-channel`) owns it. This change only adds the budget as pure logic, and task 6.1 carries it into PR #43, which wires it into the probe counting.
- Any use of the budget with the unauthenticated agent samples of `POST /api/agent/activity`.
- An automatic close of the Request Window after a request (design.md, Risks).

## Impact

- `src/ParentalControl.Agent`: a new `RequestWindow` (WPF with the `Microsoft.Web.WebView2` control), a pure navigation filter, `WidgetWindow.RequestBtn_Click` without `Process.Start`. New NuGet package `Microsoft.Web.WebView2` (BSD license, ships `WebView2Loader.dll`).
- `src/ParentalControl.Domain`: the pure URL filter, the runtime folder decision and the Agent Window Budget, with xUnit tests. No service code calls the budget before PR #43.
- `src/ParentalControl.Service` and `src/ParentalControl.Core`: a warning event at the start of the service if the runtime is missing, and `/LOG` for the setup that the updater runs.
- `ParentalControl.Domain/Messages`: two new agent text keys in "de", "en" and "id".
- `installer/ParentalControl.iss` and `build.ps1`: the Evergreen WebView2 bootstrapper, a check for the runtime in the 32-bit registry view, `SetupLogging=yes`, and the run of the bootstrapper after the service start.
- `docs/adr/0014-request-page-in-a-webview2-agent-window.md`, `GLOSSARY.md` (Request Window, Agent Window Budget).
- PR #43: its D5, its widget exemption, its requirement "A program that is never suspended still has a limit" with the two scenarios "locked within 2 minutes", and its launch environment of the agent (without `WEBVIEW2_*`) get the budget and the changes of task 6.1.
