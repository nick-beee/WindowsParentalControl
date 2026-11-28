# Proposal

## Why

The child request page and its API trust the user name from the request (issue #53, found in the review of PR #42):

- `GET /request?user=<name>` needs no sign-in. It shows the remaining time, the week and the last decline reason of the named Restricted User. Without `?user=` and without a Restricted User in the console, its fallback shows the first Restricted User and a list of the names of all Restricted Users.
- `POST /api/request` takes `username` from the JSON body. Any local process can send a Grace Request in the name of any Restricted User.

The impact today is low: one child, and a Grace Request needs the approval of the parent. But it breaks the rule of `CLAUDE.md` that the service must not trust data from the child's session without authentication. Since PR #43, the service has an authenticated source for the user of the Request Window: the launch record of the Session Agent (session-agent-channel, design D7 and D8).

## What Changes

- New term **Request Token**: a random secret that the service gives to the Session Agent through the Agent Channel for one open of the Request Window. The service maps the token to the launch record of that agent: its user SID and its session.
- The Agent Channel gets a new message `requestToken`. The service answers it only for a Restricted User, and only while the agent of the launch record runs.
- The Request Window asks for a Request Token before its first navigation and sets it as an HttpOnly cookie for `127.0.0.1`. Then the page load and each `fetch` of the page send it. Without a token, the window shows the text `request_window_unavailable`. The first page has no `?user=` any more.
- `GET /request` and `POST /api/request` take the user and the session only from a valid Request Token. They ignore `?user=` and the `username` of the body. Without a valid token, `GET /request` answers 401 with a text of the Language Preset, and `POST /api/request` answers 401 with a JSON error of the Language Preset. The page shows that error.
- The admin test mode of the request page goes away: no fallback user, no list of user names, no switch links, no redirect to `/admin` for an Administrator in the console.
- The approval flow of the parent does not change: the same Grace Request rows, the same Telegram alert, the same limits per day.
- Decided by the orchestrator under the user's delegation (2026-11-28): option (a), a Request Token through the Agent Channel. Rejected: (b) the owner of the TCP connection, and (c) the request through the Agent Channel. See design.md D1.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `request-window`: the window gets a Request Token before its first navigation; the request routes take the user only from the token; the query `?user=` has no effect; the window shows the fallback text without a token.
- `session-agent-channel`: a new message for the Request Token; the list of the uses of agent data names it.
- `admin-access`: the child-facing routes still need no Admin Session, but `/request` and `/api/request` need a valid Request Token.

## Out of scope

- A change of the approval flow of the parent, of the Telegram alert, or of the limit of requests per day.
- A sign-in for the child or a password.
- Protection against the child itself. The child owns its agent process and can read the token. The token proves only which user sends a request, and the child can only ask for itself. A Grace Request still needs the parent.
- A preview of the child page for the parent. The web admin already shows the Grace Requests and the usage of each user.
- A change of the URL filter of the Request Window. It still allows `/request` with or without a query.

## Impact

- `src/ParentalControl.Domain/Requests`: a new pure class `RequestTokenStore` (issue, verify, expiry, mapping to the launch record), with xUnit tests. `RequestWindowUrlPolicy.PageUrl` loses the user.
- `src/ParentalControl.Domain/Sessions/AgentChannelCodec.cs`: the message `requestToken`.
- `src/ParentalControl.Domain/Web/RequestPageModel.cs` and `Messages/RequestPageTexts.cs`: no admin test mode; two new texts in "de", "en" and "id".
- `src/ParentalControl.Service`: `AgentRequests` issues the token; `AgentChannelHost` answers the message; `WebServerHost` checks the token on `/request` and `/api/request`; `Program.cs` registers the store.
- `src/ParentalControl.Agent`: `AgentController` gets the token; `RequestWindow` sets the cookie before the first navigation.
- `web/admin/src/request`: no `username` in the request body, no admin test banner.
- `tests/ParentalControl.Tests`: the HTTP gate test expects 401 without a token.
- `GLOSSARY.md`: the new term Request Token; the Agent Channel and the Request Window entries name it.
- No new table, no new column, no new setting.
