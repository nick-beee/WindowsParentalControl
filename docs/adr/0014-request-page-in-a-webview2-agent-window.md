# 14. The Child Request Page in a WebView2 Window of the Session Agent

## Status
Accepted (change `show-request-page-in-agent-window`, issue #52, approved on PR #54 at d3569e9). Decided by the orchestrator under the user's delegation (2026-11-27). Revised for "Review 1" on PR #54 the same day.

## Context
The Session Agent opened the child request page (`/request`) in the default browser. Since stage 2 (`blocked-programs`), each default browser is a Blocked Program, and the service closes it at its start. So the child cannot send a Grace Request on a new install.

The child is 10 years old and has a standard account. It can set its own user environment variables, write below its profile, and run any program in its session. The block list must stay a strict list: the command line of a process must not change whether a Blocked Program is closed.

## Decision
1. **A WebView2 window in the agent.** The Session Agent shows the request page in its own window with an embedded WebView2 control. `msedgewebview2.exe` is never a Blocked Program, so the block list does not change.
2. **One URL only, and fail closed.** The window loads only `http://127.0.0.1:5050/request` as a document and resources only from that origin, also in frames. It opens no new window, no download, no external protocol and no dropped item. It has no developer tools, no context menu and no browser shortcuts. It starts its first navigation only after each setting, each event handler and the resource filter are in place; if any step fails, or the first page does not load, it shows a text of the Language Preset.
3. **The machine-wide Evergreen runtime only.** The agent removes the `WEBVIEW2_*` environment variables from its own process and gives the folder of the machine-wide Evergreen runtime explicitly, built from the Program Files known folder, not from an environment variable. It uses no proxy. It does not use a per-user runtime or a Fixed Version runtime.
4. **The installer provides the runtime.** The setup packs the signed Evergreen Bootstrapper and runs it if the runtime is missing, after it has started the service again, and writes a setup log. Without a runtime, the service writes a warning event and the window shows a text of the Language Preset. The agent never starts a browser.
5. **No site data.** Each open uses a new, empty data folder below `%LOCALAPPDATA%\ParentalControl\WebView2`, with an InPrivate profile.
6. **An Agent Window Budget.** In any 60 minutes of unlocked time (a sliding window), at most 5 minutes in which the Session Agent is a counted process of a Session Probe sample, also as the creator of its WebView2 process, add no Category Usage for the agent and do not count toward the lock of a program that is never suspended. Only the agent's own time is exempt; another process in the same sample still counts, and the Allowance Time, Continuous Use, Breaks and the Schedule Window do not change. Only probe samples and the lock state decide the budget, never data of the Session Agent, so it has no effect before the Session Probe (ADR 0011 of PR #43). It keeps the probe, which counts the agent in Unknown, from locking a child who types a request.

### Rejected alternatives
- **A native WPF form** that posts to `/api/request`: a second UI for the texts, limits, decline banner, week strip and schedule requests of the page, with a change in two places for each page change.
- **An allowed browser start for the request URL**: the child controls the command line, and the browser can navigate anywhere after the start. It breaks the requirement "Process data for a block is untrusted" of `blocked-programs`.
- **An Edge URL allow-list policy** for the request page: a full browser for one site, policy keys for the installer to own, and one policy error opens the internet.
- **A Fixed Version runtime in the install folder**: about 170 MB more per download and its own security updates.

## Consequences
- The agent depends on the WebView2 Runtime and the `Microsoft.Web.WebView2` package. A PC without the runtime that was offline during the setup gets the fallback text until the next setup or a manual runtime install.
- The trust of the Session Probe in `msedgewebview2.exe` (only the Evergreen folders below Program Files) and the runtime choice of the agent are coupled. A change of one needs a check of the other.
- The Request Window is a window of the agent. Code that the child injects into its agent can navigate the web view anywhere. This is the accepted class of injected code (PR #43 D13). After PR #43, a game that such code draws in the agent window gets the budget (at most 5 minutes in any 60 minutes of unlocked time) and, on top of it, the 60 seconds in 10 minutes that PR #43 allows before its lock. So the bound is not 5 minutes per hour in total.
- A child who keeps the Request Window in front while it waits for the answer of the parent uses the budget, and with Unknown exhausted it is then locked after about one more minute. This cost is accepted; the answer comes in the widget.
- A later page change needs no agent change, as long as the page stays at `/request` and loads only same-origin resources.
- To go back to a browser needs a change of `blocked-programs`.
