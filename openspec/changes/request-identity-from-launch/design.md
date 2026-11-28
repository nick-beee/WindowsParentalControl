# Design

## Context

See proposal.md for the motivation and the spec deltas for the required behaviour.

The current code has these properties:

- `WebServerHost` maps `GET /request` and `POST /api/request` without an Admin Session (admin-access, "Child-facing routes stay open").
- `GET /request` selects the user in this order: the Restricted User of `?user=`; else the user of the console session (an Administrator gets a redirect to `/admin`); else the first Restricted User, with the admin test banner and the names of all Restricted Users (`RequestPageAdminTest`).
- `POST /api/request` selects the user in this order: the Restricted User of `username` in the body; else the first Restricted User with an active session; else the first Restricted User.
- The Request Window (request-window, ADR 0014) opens `http://127.0.0.1:5050/request?user=<Environment.UserName>`. Its URL filter allows `/request` with any query.
- Since PR #43 the service starts each Session Agent itself and keeps a launch record: the session of the start and the user SID from `TokenUser` of the token that started the agent (session-agent-channel, design D7). The service takes the identity of each Agent Channel message from this record (design D8). `AgentLaunchRegistry.Running(sessionId)` gives the record of the running agent of a session; a channel fault marks the record as ended, and the end of the session removes it.

Each decision below is decided by the orchestrator under the user's delegation (2026-11-28).

## Goals / Non-Goals

**Goals:**
- The service decides the user and the session of `/request` and `/api/request` from an authenticated source, never from data of the request.
- No list of user names on a page that needs no sign-in.
- The Request Window still sends a Grace Request that reaches the parent, with the same approval flow.

**Non-Goals:**
- Trust in the data of the child. The child controls its agent and its page; the token proves only the user.
- A child page for a browser. All browsers are blocked by default, and the Request Window is the way to the page (#52).

## Decisions

### D1. A Request Token from the Agent Channel (option a)

The Request Window gets a per-open secret, the Request Token, from the service through the Agent Channel. The service maps the token to the launch record of the agent that asked for it. The request routes take the user and the session from that record.

Why this option:
- It uses the identity source that PR #43 made for the agent: the launch record. The user SID comes from the token that started the agent, and the session comes from the start. The same source answers the status poll, the prompt answers and the session-end requests (session-agent-channel, design D8), so the rules stay the same in one place.
- It gives the session of the launch record, not only the user. The page then shows the state of the session of the window (active or locked).
- It needs no new WinAPI call in the service and no lookup of a process by a pid.

The child can read the token: it is in the memory of the agent process of the child and in the cookie store of the web view. This is acceptable. The token proves only which user sends a request, and with it the child can only ask for itself. A process of another user cannot read the memory of the child's agent. A Grace Request still needs the approval of the parent, and no limit reads the token (session-agent-channel, "No limit depends on data of the Session Agent").

Rejected alternatives:
- **(b) The owner of the TCP connection** (`GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_ALL`, then `ProcessIdToSessionId` and the token user of the owner process, as `TcpConnectionOwner` does for the admin sign-in). It proves the user of the process that holds the connection. For the Request Window, that process is the network process of WebView2 (`msedgewebview2.exe`), not the agent, so it gives no link to the launch record. It also accepts each other process of the child (PowerShell, a renamed portable browser) as a valid caller. The lookup of the owner and then of the process by its pid can race with the end of the process and the reuse of its pid.
- **(c) The request through the Agent Channel.** The page would give its data to the agent, and the agent would send it on the channel. The page needs a bridge for that: `IsWebMessageEnabled` (`window.chrome.webview.postMessage`) or a host object. The lockdown of the Request Window turns both off (request-window, design D3), and a bridge adds attack surface to the web view. The page load (`GET /request`) also shows the data of the user, so it needs an identity too. A token covers both routes with one rule.
- **A secret on the command line of the agent.** The service starts the agent once per session, but the window opens many times. A token per open can expire and end with the agent; a secret per agent start cannot be renewed without a channel message anyway.

### D2. The token goes as an HttpOnly cookie

The Request Window sets the token with `CoreWebView2.CookieManager.CreateCookie("pc_request_token", token, "127.0.0.1", "/")`, with `IsHttpOnly = true` and `SameSite = Strict`, and `AddOrUpdateCookie`, before its first `Navigate`. It is a session cookie without an expiry. The web view then sends it with the page load and with each `fetch` of the page to `/api/request`. The service reads only this cookie.

- The first navigation already needs the token (`GET /request` shows the data of the user). A cookie that is in place before the navigation covers it without a token in the URL.
- The page code does not need the token and cannot read it (`HttpOnly`). The React page needs no change for the token.
- `SameSite = Strict` and the fixed origin of the window keep the cookie away from each request that another site starts.
- Each open of the Request Window has a new, empty data folder and runs in InPrivate mode (request-window, "The Request Window keeps no site data"), so the cookie ends with the window.

Rejected: a custom header that `WebResourceRequested` adds to each request. The window already has this handler, but a change of the headers of a navigation request in that event is less clear in the documentation of WebView2 than the cookie API. Rejected: the token in the query of the first URL and in the page data. The token is then in the URL of the window and in the `Referer` of each asset request, and the page code must send it.

### D3. `RequestTokenStore` (Domain, pure)

A new class `ParentalControl.Domain.Requests.RequestTokenStore`, thread-safe, with a monotonic clock (`Environment.TickCount64` in the service, a fake clock in the tests):

- `Issue(AgentLaunchRecord launch)` returns a new token, or null if `launch` is not the running record of its session in the `AgentLaunchRegistry`. The token is 32 bytes from `RandomNumberGenerator`, as base64url without padding (43 characters). The store keeps only the SHA-256 hash of the token, the launch record and the expiry. One token per launch record: a new token replaces the token that the same record had before.
- `Verify(string? token)` returns the launch record of a valid token, else null. A token is valid if:
  - it has exactly 43 base64url characters (checked before the hash, so a long cookie costs nothing);
  - its hash is equal to a stored hash (`CryptographicOperations.FixedTimeEquals`);
  - its expiry is not over: 60 minutes after the issue (`RequestTokenStore.Lifetime`);
  - its launch record is still the running record of its session (`AgentLaunchRegistry.Running(sessionId) == launch`).
- The last rule ends each token of an agent when its channel fails (the record is marked as ended), when a new agent starts in the session (a new record), and when the session ends (the record is removed). No extra hook in the channel host is necessary.
- The store removes the expired and the ended entries at each `Issue` and `Verify`. It holds at most one entry per launch record, so its size is bounded by the number of sessions.
- A restart of the service ends all tokens (the store is in memory). An open Request Window then gets 401 at its next request, and the page shows the text `api_no_access`.

The lifetime of 60 minutes bounds the use of a token that leaked. The child can open the window again for a new token at any time, so a short lifetime costs nothing.

### D4. The Agent Channel message `requestToken`

- The agent sends `{ "type": "requestToken", "id": <n> }`. The codec (`AgentChannelCodec`) gives `AgentRequestTokenRequest(Id)`. The message has no field; a `user` or a `sessionId` in it is ignored, as for each message.
- `AgentRequests.IssueRequestToken(record)` reads the user of the launch record (`UserRepository.GetBySid(record.UserSid)`). For a Restricted User it calls `RequestTokenStore.Issue(record)`. For an unknown user or a user who is not restricted it gives null.
- The host answers `{ "id", "type": "requestToken", "ok": true, "token": "<token>" }`, or `ok: false` without a token. The message counts against the rate limit of the channel (5 per second, 30 per 10 seconds), as each message does.
- The store is one DI singleton (`Program.cs`), built with the `AgentLaunchRegistry` singleton and given to `AgentRequests`. `WebServerHost` reads it from the `AgentRequests` singleton, which the channel host uses too. A `WebServerHost` without an `AgentRequests` (a test) makes a store with an empty registry: each token is then refused (fail closed).

### D5. The request routes take the user only from the token

A helper `RequestCaller(HttpContext)` in `WebServerHost` reads the cookie `pc_request_token`, calls `RequestTokenStore.Verify`, and reads the user of the launch record. It gives the user and the session only for a Restricted User.

- `GET /request`: without a caller, the answer is 401 with the text `page_no_access` of the Language Preset as `text/plain`, with the security headers of the SPA and `Cache-Control: no-store`. With a caller, the page shows the data of that user, and the session state comes from the session of the launch record (`ActiveSessions[record.SessionId]`), not from the first session of the user. `?user=` has no effect.
- `POST /api/request`: the route checks the caller before it reads the body. Without a caller, the answer is 401 with `{ "error": <api_no_access> }`. The `username` of the body has no effect. The rest stays: the checks of the minutes and the reason, the limit per day, the pending request, the connectivity check, the row in `grace_requests`, and the Telegram alert with the name of the user of the token.
- A refused `POST` logs one warning, at most once in 10 minutes (`WarningLimit`), so that a flood of the child does not fill the log.

The Request Window shows `request_window_unavailable` for a 401 of its first navigation: WebView2 reports an HTTP 4xx as `IsSuccess = false`, and the window shows the fallback for each failed first navigation (`RequestWindowNavigations`). A browser shows the text `page_no_access`.

### D6. No admin test mode

`RequestPageInput.IsAdminTesting`, `RequestPageInput.AvailableUsers`, `RequestPageModel.AdminTest` and `RequestPageAdminTest` go away, and so do the banner and the switch links of `RequestApp.tsx`. The redirect to `/admin` for an Administrator in the console goes away too: a page without a token shows only the text `page_no_access`. The parent sees the Grace Requests and the usage in the web admin.

### D7. The Request Window

- `AgentController.OpenRequestWindow` gives the window a delegate that sends `requestToken` on the channel (timeout 3 seconds) and returns the token, or null for a timeout, a dropped request, `ok: false` or a token of the wrong form.
- `RequestWindow.StartAsync` calls the delegate after it has applied each setting, each event handler and the resource filter, and before `Navigate` (request-window, "The lockdown applies before the first navigation"). Without a token it shows `request_window_unavailable` and does not navigate.
- The first URL is `http://127.0.0.1:5050/request` without a query (`RequestWindowUrlPolicy.PageUrl`).
- No new WinAPI call. The WebView2 APIs: `CoreWebView2.CookieManager`, `CoreWebView2CookieManager.CreateCookie`, `CoreWebView2Cookie.IsHttpOnly`, `CoreWebView2Cookie.SameSite`, `CoreWebView2CookieManager.AddOrUpdateCookie`.

### D8. Texts

Two new keys in `RequestPageTexts`, in "de", "en" and "id", without placeholders:
- `page_no_access`: "Diese Seite geht nur über den Knopf „Mehr Zeit wünschen“ von Parental Control." The text of `GET /request` without a valid token.
- `api_no_access`: "Das Anfragefenster ist abgelaufen. Schließ es und öffne es wieder." The error of `POST /api/request` without a valid token. The page shows it as the error of the form.

The key `api_no_user` goes away: the route no longer searches for a user.

## Risks / Trade-offs

- [The cookie is not in place at the first navigation] → `AddOrUpdateCookie` has no completion event. The first navigation then gets 401, and the window shows `request_window_unavailable`. A VM task checks that the window shows the page. If it fails, the fallback design is `NavigateWithWebResourceRequest` with a `Cookie` header for the first navigation.
- [Another process holds port 5050 while the service is down] → The squatter gets only the cookie of the child's own Request Window, and only the token of the child. That token proves only the child's identity to the real service, so the harm is nil. The port-squat warning of the service stays as it is.
- [The child reads its token and sends requests from PowerShell] → The requests count as the child's requests, under the same limits per day and with the same approval of the parent. It is the same as a click in the window.
- [A token of 60 minutes ends while the window is open] → The page shows `api_no_access`; the child closes and opens the window.
- [The service restarts while the window is open] → The same: all tokens end, and the next request gets 401.
- [No admin preview of the child page] → The parent loses the test banner. The web admin shows the same data.

## Migration Plan

- The agent and the service ship together (the installer). An old agent that survives an update sends no `requestToken`; its window opens `/request?user=…` without a cookie and shows `request_window_unavailable` until the next sign-in. No limit depends on it.
- No database change.

## Open Questions

None.
