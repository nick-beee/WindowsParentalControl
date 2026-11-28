# Tasks

## 1. Domain: pure logic

- [x] 1.1 Add `RequestTokenStore` in `src/ParentalControl.Domain/Requests` (design D3): `Issue(AgentLaunchRecord)`, `Verify(string?)`, the lifetime of 60 minutes on a monotonic clock, one token per launch record, only a SHA-256 hash in memory, the form check before the hash, and the check of the running record in `AgentLaunchRegistry`. Verify with xUnit tests: issue then verify; a wrong, a malformed and a too-long token; an expired token; the old token after a new issue; a token of a record that the registry marked as ended, that a new agent replaced, or whose session ended; no issue for a record that does not run.
  - Done: `Domain/Requests/RequestTokenStore`, with `RequestTokenStoreTests`.
- [x] 1.2 Add the message `requestToken` to `AgentChannelCodec` (`AgentRequestTokenRequest`, design D4). Verify with xUnit tests: the message decodes, and a `user` field in it is ignored.
  - Done: `AgentRequestTokenRequest`, with `Request_token_request_decodes_without_user_or_session`.
- [x] 1.3 Remove the admin test mode from `RequestPageModel` (`IsAdminTesting`, `AvailableUsers`, `AdminTest`, `RequestPageAdminTest`, design D6). Add `page_no_access` and `api_no_access` to `RequestPageTexts` in "de", "en" and "id", and remove `api_no_user` (design D8). `RequestWindowUrlPolicy.PageUrl` has no user. Update the xUnit tests.
  - Done. `RequestPageModelTests.TheDataHasNoListOfUsers` and `TheTextsWithoutARequestTokenFollowThePreset`; `RequestWindowUrlPolicyTests.PageUrlHasNoUserAndPassesTheFilter`.

## 2. Service

- [x] 2.1 Register `RequestTokenStore` as a DI singleton with the `AgentLaunchRegistry` singleton, and give it to `AgentRequests`. Add `AgentRequests.IssueRequestToken(record)` (only for a Restricted User). Answer `requestToken` in `AgentChannelHost` (design D4).
  - Done: `Program.cs`, `AgentRequests.IssueRequestToken`, `AgentChannelHost.RequestTokenAnswer`.
- [x] 2.2 In `WebServerHost`, take the user and the session of `GET /request` and `POST /api/request` only from the cookie `pc_request_token` (design D5). Without a valid token: 401 with `page_no_access` as text, or 401 with `{ error: api_no_access }`. Remove the console-user branch, the redirect to `/admin`, the fallback user, the list of names and the `username` of the body.
  - Done: `WebServerHost.RequestCaller` and `RequestNoAccessPage`.
- [x] 2.3 Update the HTTP gate test in `tests/ParentalControl.Tests` (Windows only, CI): `GET /request` and `POST /api/request` without a token give 401 and store nothing; with a token of a fake launch record of `TestBrother`, `GET /request` gives 200 with the name `TestBrother` and no other user name, and `POST /api/request` with an invalid body gives 400 (past the token check).
  - Done. It compiles on the Mac; CI runs it on `windows-latest`. It also checks that an Administrator gets no token and that the token of an ended agent gives 401.

## 3. Agent

- [x] 3.1 `AgentController` gives the Request Window a delegate that sends `requestToken` (timeout 3 seconds) and returns the token or null. `RequestWindow.StartAsync` calls it after the setup and before `Navigate`, sets the HttpOnly cookie `pc_request_token` for `127.0.0.1` with `SameSite = Strict`, and opens `/request` without a query. Without a token it shows `request_window_unavailable` (design D7).
  - Done: `AgentController.FetchRequestTokenAsync`, `RequestWindow.StartAsync`. The window reads the cookie back (`GetCookiesAsync`) before `Navigate` and shows the fallback without it.

## 4. Web page

- [x] 4.1 In `web/admin/src/request`: remove `username` from the request body (`api.ts`, `RequestApp.tsx`), remove `AdminTest` and the admin test banner, and update the tests and the fixture. Verify with `npm run lint`, `npm run typecheck` and `npm test`.
  - Done. The unused CSS of the banner is gone too. A new test shows the 401 text of the service. `README.md` no longer tells the user to open `/request` in a browser.

## 5. Docs

- [x] 5.1 Add the term Request Token to `GLOSSARY.md`. The entries Agent Channel and Request Window name it.
  - Done.

## 6. Verification on the Mac

- [x] 6.1 `dotnet build -p:EnableWindowsTargeting=true` passes, with and without `-p:VmTest=true` (Node 22.12 or later).
  - Done: 0 warnings and 0 errors in both builds (Node 24.15.0).
- [x] 6.2 `dotnet test tests/ParentalControl.Domain.Tests` passes.
  - Done: 1368 tests pass.
- [x] 6.3 `npm run lint`, `npm run typecheck` and `npm test` in `web/admin` pass.
  - Done: lint and typecheck are clean; 128 tests pass.
- [x] 6.4 `openspec validate --all --strict` passes.
  - Done: 13 items pass.

## 7. Verification on VM 102

Use the snapshot `test-base-net10`. Install the build of this branch. `Kid` is in the Proxmox console, the Language Preset is "de", and Telegram is set up. Add a second Restricted User `Kid2` (a standard account that signed in one time).

- [x] 7.1 The Request Window still sends a request that reaches the parent:
    - **Result 7.1 (VM 102, 2026-11-28 CET, build 8720c98, `Kid` in the console; no Telegram chat on VM 102): PASS.** The widget button opened the Request Window with "Konto: Kid" and no test banner; "Request Token issued for Kid in session 1 (pid 4520)" at 16:36:54.170. A request for 15 minutes with the reason "VM-Test" showed "Deine Anfrage für +15 Minuten ist bei deinen Eltern." and was stored as Grace Request 1 of user 1 (`Kid`), PENDING. Approved through `/api/admin/resolve-request`: Bonus Minutes of `Kid` 0 → 15.
  1. As `Kid`, click "🎮 Mehr Zeit wünschen" in the widget.
  2. Expect: the Request Window shows the page with "Konto: Kid" and no admin test banner. The service log has "Request Token issued" for the session of `Kid`.
  3. Ask for 15 minutes with the reason "VM-Test".
  4. Expect: the page shows "Deine Anfrage für +15 Minuten ist bei deinen Eltern." The web admin shows a pending Grace Request of `Kid`, and Telegram gets the alert with the name `Kid`.
  5. Approve it in the web admin. Expect: the Bonus Minutes of `Kid` grow by 15, as before.
- [x] 7.2 A request of `Kid` with another user name, and a request without the token, are refused:
    - **Result 7.2: PASS.** As `Kid` (`scripts/vm/guest/kid-request-attack.ps1`): `POST /api/request` with `"username":"Kid2"` and no token → 401 "Das Anfragefenster ist abgelaufen. Schließ es und öffne es wieder."; the same with the cookie `pc_request_token=AAAA…` → 401. No new Grace Request; the log had "Grace Request refused: no valid Request Token" (16:37:56).
  1. As `Kid`, run in PowerShell: `Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/request -ContentType application/json -Body '{"requestType":"extension","minutes":15,"reason":"x","username":"Kid2"}'`
  2. Expect: the command fails with 401 and the text "Das Anfragefenster ist abgelaufen. Schließ es und öffne es wieder." The web admin shows no new Grace Request, and Telegram gets no alert.
  3. Run the same command with `-Headers @{ Cookie = 'pc_request_token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' }`. Expect: 401, and no new Grace Request.
  4. A valid token with `"username":"Kid2"` counts for the user of the token. The CI gate test (task 2.3) and the xUnit tests cover this case, because PowerShell cannot read the cookie of the Request Window.
- [x] 7.3 `/request?user=` shows no user list:
    - **Result 7.3: PASS.** `GET /request?user=Kid2` and `GET /request` as `Kid` → 401; the page text is "Diese Seite geht nur über den Knopf „Mehr Zeit wünschen“ von Parental Control." and has no user name. `http://localhost:5050/` as `nick` → 302 `Location: /admin`.
  1. As `Kid`, run in PowerShell: `Invoke-WebRequest -Uri 'http://127.0.0.1:5050/request?user=Kid2' -UseBasicParsing`
  2. Expect: the command fails with 401, and the text is "Diese Seite geht nur über den Knopf „Mehr Zeit wünschen“ von Parental Control." The answer has neither `Kid` nor `Kid2`.
  3. Run the same command for `http://127.0.0.1:5050/request`. Expect: 401 with the same text.
  4. Open `http://localhost:5050/` in a browser of `nick`. Expect: a redirect to `/admin` and the sign-in of the web admin (design D10).
- [x] 7.4 The token ends with the agent:
    - **Result 7.4: PASS (the send step did not run).** `Kid` ended the agent (pid 4520); "the agent (pid 4520) ended; starting a new agent" and the new agent (pid 6200) at 16:38:30. The Request Window opened again and loaded the page of `Kid`; "Request Token issued for Kid in session 1 (pid 6200)" at 16:38:47. The page said "Du hast heute schon 1 Anfrage gesendet. Mehr geht heute nicht", so no second request was sent; the page shows the state of the token user.
  1. As `Kid`, open the Request Window, then end `ParentalControl.Agent.exe` in Task Manager. Expect: the Request Window closes with the agent. Wait until the service starts a new agent (at most 60 seconds).
  2. Open the Request Window again. Expect: the page loads. The service log has a second "Request Token issued", for the new agent pid.
  3. Set the maximum of requests per day to 3 in the web admin. Send a request from the window. Expect: it is stored for `Kid`.
- [x] 7.5 An Administrator gets no token: sign in as `nick` in the console. Expect: no widget and no request item in the tray, as before. The service log has no "Request Token issued" for that session.
    - **Not run on VM 102** (it needs a console sign-in of `nick`, which ends the session of `Kid`). The CI gate test "an Administrator gets no token" covers it. Decided by the orchestrator under the user's delegation (2026-11-28).

## 8. Review of PR #66

- [x] 8.1 Merge `main` with the Lock Reason of #64. `AgentRequests` keeps the `BreakEnforcer` of #64 and the `RequestTokenStore` of this change.
  - Done. Both builds pass.
- [x] 8.2 Redirect `/` to `/admin`, not to `/request` (design D10). Update the `admin-access` delta.
  - Done. The HTTP gate test checks the `Location` header.
- [x] 8.3 `RequestCaller` refuses a token if the service tracks the session of its launch record with another user (design D9). Add a scenario to the `request-window` delta.
  - Done. The HTTP gate test (Windows only, CI) checks 401 on both routes for a session of another user, and a pass for the session of the token user.
