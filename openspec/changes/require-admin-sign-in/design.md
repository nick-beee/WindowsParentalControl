# Design

## Context

See proposal.md (Why) for the problem and `specs/admin-access/spec.md` for the required behavior.

Current state that shapes the approach:
- `WebServerHost` (Kestrel in the LocalSystem service) calls `ListenAnyIP(5050)` over HTTP. Each of the 30 routes below `/api/admin/` starts with `if (!CheckAuth(ctx)) return Results.Unauthorized();`. `CheckAuth` returns `true` when the setting `totp_enabled` is not `"true"`, and its default is `"false"`.
- `POST /api/admin/login` issues a token when `string.IsNullOrWhiteSpace(secret) || TotpService.VerifyCode(secret, code)`. So TOTP on with an empty secret accepts each code.
- Tokens are in a `HashSet<string>` in memory. The cookie `pc_admin_session` (HttpOnly, SameSite=Strict) expires after 12 hours, but the server never expires or removes a token, except at a logout. The logout is a GET.
- There is no limit on failed logins. `TotpService.VerifyCode` accepts the codes of the time steps −1, 0 and +1 and does not remember a used code.
- The WPF Admin app (`requireAdministrator` in `app.manifest`) writes the settings straight into SQLite. `SettingsViewModel` has a switch for TOTP, a button that makes a new secret, and it shows the secret, the `otpauth://` URI and the QR code all the time. `SaveSettings` writes `totp_enabled` and `totp_secret` with the other settings.
- The installer starts the Admin app after an interactive install (`[Run] ... postinstall skipifsilent`). The Built-in Updater installs silently, so no UI of the installer or of the Admin app shows after an update.
- `DatabaseManager.Initialize` sets the access rules of `C:\ProgramData\ParentalControl` (SYSTEM and Administrators, no inheritance) only when it creates the folder, and it ignores errors.
- The child-facing routes are `/`, `/request`, `/api/request` and `/api/agent/*`. `/api/request` only saves a pending Grace Request. The parent resolves it through the Telegram bot (checked by chat ID) or through `/api/admin/resolve-request`.
- `TelegramBotService` has `SendTextMessageAsync` and the Offline Notification Queue (`PendingNotificationRepository`).

## Goals / Non-Goals

**Goals:**
- Deny by default: a new route below `/api/admin/` is protected without an extra line of code.
- The child cannot get an Admin Session in a useful time with a script on its own PC.
- The parent can always get back in through the Admin app on the PC.
- The policy logic (route class, login limit, replay check, session expiry) runs and is tested on the Mac.

**Non-Goals:**
- Protection against a device on the LAN that reads the HTTP traffic (#7).
- Protection of the agent routes against false data (#6).
- More than one admin, roles or audit of single admin actions.

## Decisions

### D1. The credential: Authenticator TOTP only
The Remote Admin Interface uses Authenticator TOTP as its only credential. It is already in the code and in the authenticator app of a parent who turned it on.
- *Alternative: a password.* Rejected. The child can see the parent type it on the PC, and the parent can use the same password in other places. Over plain HTTP (#7) a password that a device on the LAN reads stays valid. A TOTP code is valid for about 90 seconds and, with D5, for one login only.
- *Alternative: a password and TOTP.* Rejected: a second secret to keep and to recover, for one parent on a home network. The limit in D6 makes a TOTP guess too slow. The user decided on TOTP only.
- *Alternative: sign in with the Windows account of the parent (Negotiate/NTLM).* Rejected: it does not work from the parent's phone, and the browser on the child's PC can ask the child for the credentials of another account.

### D2. Setup only in the WPF Admin app
The setup happens only in the Admin app. The app runs with administrator rights, so the child cannot open it (the UAC prompt needs the password of an administrator).
- The app makes a secret (`TotpService.GenerateSecret`, 20 random bytes), shows the QR code and the Base32 text, and asks for the current code. It saves only when the code is valid. Then a bad scan does not lock the parent out.
- After the setup the app shows "Sign-in is set up" and the date, but not the secret, the URI or the QR code.
- `SaveSettings` no longer writes `totp_enabled` or `totp_secret`. Only the setup writes them.
- *Alternative: a first-run setup page on the web.* Rejected. The first caller gets the credential, and the service starts before the parent opens the web page. The child on `127.0.0.1` can win this race.
- *Alternative: a setup page in the installer.* Rejected. A silent update has no UI, Inno Setup Pascal script cannot show a QR code without an extra DLL, and the Admin app opens after an interactive install anyway.
- *Alternative: a one-time setup code in a file that only an administrator can read.* Rejected: it needs an administrator anyway, and the Admin app is simpler for the parent.

### D3. Meaning of `totp_enabled`
The setting `totp_enabled` now means "the setup is complete". There is no switch any more.
- The Remote Admin Interface is set up when `totp_enabled` is `"true"` and `totp_secret` is a valid secret: only Base32 characters after the removal of spaces and `=`, and at least 10 bytes (80 bits) after the decode. The check does not use `FromBase32String` alone, because that method skips characters that are not Base32 without an error.
- A new setup always makes 20 bytes. The user approved the limit of 10 bytes. It is low on purpose: the old Admin app let the parent type any secret, and a stricter limit would lock out a parent with a short old secret. A secret below 10 bytes or with other characters is not valid; the upgrade then treats the install as not set up (see Migration Plan).
- Not set up means: `/admin` shows the setup text, each admin route answers 401, and the login refuses each code.
- *Alternative: a new key such as `admin_totp_enrolled`.* Rejected: the old key already has the right value on each install where TOTP was on, so the upgrade needs no data migration for that case. A downgrade to the old version also keeps TOTP on.

### D4. One gate for all admin routes
A middleware runs after `app.UseRouting()` and before the endpoints, so it sees the endpoint that routing selected. The 30 `CheckAuth` calls go away.
- A pure class `AdminRoutePolicy` in `ParentalControl.Domain` classifies a route template or a path as `Open`, `Login`, `Logout` or `Admin`. Each template or path below `/api/admin/` that is not the login or the logout is `Admin`. The match uses whole path segments, ignores case, and ignores empty segments and a trailing `/`.
- The middleware classifies the selected endpoint by its template (`ctx.GetEndpoint()` as `RouteEndpoint`, then `RoutePattern.RawText`). It also classifies the raw request path. If one of the two gives `Admin`, the request is an admin request. So a path variant that routing matches to an admin endpoint, but that the path check does not see as admin, still goes through the gate. A path below `/api/admin/` with no endpoint gets 401, not 404.
- For `Admin`: the middleware checks the `Origin` (D8), then the Admin Session (D7). If one check fails, it answers 401 (or 403 for the `Origin`) and the endpoint does not run.
- `GET /admin` itself shows one of three pages: the setup text, the login form, or the dashboard.
- A test on the VM builds the endpoints and lists them through `EndpointDataSource`. It classifies each template with the same `AdminRoutePolicy` and asserts that each route is in the open list (`/`, `/request`, `/api/request`, `/api/agent/*`, `/admin`) or `Admin`, `Login` or `Logout`.
- *Alternative: a route group with `AddEndpointFilter`.* It works too, but a route that someone maps on `app` and not on the group is open. The middleware is safe by path.
- *Alternative: keep `CheckAuth` in each route and only change its body.* Rejected: the next new route can forget it.

### D5. TOTP check with a replay guard
The TOTP code (without the QR part) moves to a pure class `Totp` in `ParentalControl.Domain`, with xUnit tests against the RFC 6238 test vectors. `TotpService` in Core keeps the QR code and calls `Totp`.
- `Totp.TryVerify(secret, code, nowUtc, lastUsedStep, out matchedStep)` accepts the steps −1, 0 and +1, but only a step greater than `lastUsedStep`.
- After a valid login the service saves the matched step in the setting `totp_last_step`. The check and the save run under one lock in the service.
- The setup in the Admin app saves the step of the confirmation code. So the confirmation code cannot log in.

### D6. Limit on failed logins
A pure class `LoginThrottle` in Domain decides from the list of failures, the source and the time whether a login may be checked.
- Limits: 10 failures from one source and 30 from all sources in a moving window of 24 hours. While a limit applies, the service does not check the code at all.
- The source is `HttpContext.Connection.RemoteIpAddress`, never `X-Forwarded-For`. An IPv4-mapped IPv6 address becomes IPv4. Each loopback address and each address of this PC becomes the source `local`. The service gets the addresses of this PC from `NetworkInterface.GetAllNetworkInterfaces()` (it calls `GetAdaptersAddresses`), with a cache of 60 seconds. The compare ignores the IPv6 `ScopeId`, so a link-local address with a zone does not give a second source. If an address is not in the cache, the service reads the addresses again before it treats the source as remote.
- A valid login deletes the failures of its source.
- New table `admin_login_failures (id INTEGER PRIMARY KEY, source TEXT NOT NULL, failed_at_utc TEXT NOT NULL)` with an index on `failed_at_utc`. The service deletes rows older than 24 hours at each login. So the counts survive a restart and a reboot.
- When a failure makes a source or all sources reach the limit, the service sends one Telegram message (D10).
- *Alternative: one global limit only.* Rejected. The child on this PC could then block the parent's login from the phone each day. With a limit per source, the child blocks only the source `local`. The global limit of 30 is a backstop: the child needs three sources to reach it.
- *Alternative: a limit per minute in memory, for example 5 per minute.* Rejected: that gives 7200 guesses a day, about 2 % chance per day (see Risks). The child can also reset a limit in memory with a reboot.
- *Alternative: a delay that doubles after each failure.* The fixed daily limit is simpler to explain and to test, and it gives a lower bound per year.

### D7. Admin Session store
A pure class `AdminSessionStore` in Domain keeps the sessions in memory. It gets the time from `TimeProvider`.
- A session has a token (32 random bytes, hex, as today), the issue time and the session epoch at the time of the login.
- A session is valid when its age is less than 12 hours and its epoch equals the current value of the setting `admin_session_epoch`. The middleware reads this setting at each admin request, as `CheckAuth` reads `totp_enabled` today.
- The Admin app increments `admin_session_epoch` at a new setup and at "Sign out all web sessions". The service then refuses all older sessions without a restart and without a channel between the two processes.
- The store keeps at most 20 sessions (approved by the user) and removes the oldest one first. It removes expired sessions at each login.
- A restart of the service clears the store. The tokens are not written to disk.
- The logout becomes `POST /api/admin/logout`, and the dashboard sends it from a form.
- The cookie stays HttpOnly and SameSite=Strict. It is a session cookie without `Expires` (security review S4): the browser drops it when it closes, and the server still ends the Admin Session after 12 hours. It gets no `Secure` flag while the server uses HTTP (#7).
- *Alternative: tokens in SQLite, so a restart keeps the sessions.* Rejected: a token on disk is a further secret to protect, and a new login after a restart is a small cost.

### D8. `Origin` check
For each POST, PUT and DELETE below `/api/admin/` (the login and the logout included): if the request has an `Origin` header and it is not `http://` plus the `Host` header of the request, the middleware answers 403.
- SameSite=Strict does not help here, because `localhost:8000` and `localhost:5050` are the same site. A page that the child serves on another port is the case to stop.
- A request without `Origin` passes this check. A browser always sends `Origin` on a cross-origin POST, and a client that is not a browser has no cookie of the parent.
- GET routes below `/api/admin/` change no data, with one exception: `GET /api/admin/logout`, which D7 changes to a POST.

### D9. Owner and access rules of the data folder at each start
The service secures the data folder at each start, before it opens the database or a log file, and only when `PARENTAL_CONTROL_DATA_DIR` is not set (the tests use that variable). The code is `DataFolderGuard` in Core; the trust decision is the pure class `DataFolderTrust` in Domain.
- Threat: a standard user can create a folder, a junction or a symbolic link in `C:\ProgramData`, and the child can act before the install. The owner of a folder can always change its rules again, and an open handle keeps its access after a change of the rules.
- Each check and each change uses a handle that `CreateFileW` opens with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`, so no step follows a link and the item cannot change between the check and the change (S1). The service enables `SeTakeOwnershipPrivilege`, `SeRestorePrivilege` and `SeBackupPrivilege`; LocalSystem has them.
- A link as the data folder (B1): the service opens the link itself, checks that it is the same object (volume serial and file index), and deletes it with `FileDispositionInfo`. The target does not change. Then it creates a new folder.
- A new folder: `CreateDirectoryW` with a security descriptor, so the folder has its owner and rules from the first moment.
- Untrusted content (B2): the service reads the owner and the DACL of the folder and of each item below it (links skipped). An item is trusted only if SYSTEM or the Administrators group owns it and no other account has an allow entry with a write right (`FILE_WRITE_DATA`, `FILE_APPEND_DATA`, `FILE_WRITE_EA`, `FILE_DELETE_CHILD`, `FILE_WRITE_ATTRIBUTES`, `DELETE`, `WRITE_DAC`, `WRITE_OWNER`, `GENERIC_WRITE`, `GENERIC_ALL`). Inherit-only, deny and audit entries are ignored; an entry of an unknown type is not trusted. Read rights are trusted: they leak, but they do not change the content, and the reset removes them.
- If an item is not trusted, the service renames the folder through its handle to `ParentalControl.untrusted-<guid>`, locks the moved folder itself (not its content: a walk by path in a folder of the child could follow a link), and creates a new folder. The service then starts with a new database. The rename fails if a process holds a handle to the folder or to an item in it without delete sharing; then the start fails (fail closed). This also applies to an existing install on an upgrade.
- A trusted folder: the service writes owner Administrators and a protected DACL (full control for SYSTEM and Administrators, inherited by all items) on the handle. For each item it writes owner Administrators and the inherited entries of the folder itself, with `SetKernelObjectSecurity`, so the result does not depend on the inheritance step of Windows.
- Links inside the folder (S2): the service deletes each junction and symbolic link (the link only). The folders that it needs (`logs`, `captures`) are created again as real folders. A file with more than one name (a hard link) gets the locked rules first (so the other names lose access too) and is then replaced by a copy with one name. A link therefore never locks the Remote Admin Interface.
- Fail closed: if a step fails, `DatabaseManager.Initialize` throws `DataFolderUnsafeException`. The service then writes an Error event to the Windows event log (source `ParentalControl.Service`) and the state to the registry, and stops with exit code 1 before it opens the database or a log file. The Service Control Manager starts it again (`sc failure`: 10 s, 30 s, then each 60 s), and each start tries again.
- The logger writes only to `logs` in the secured data folder. The fallback to the TEMP folder is gone: `C:\Windows\Temp` is writable for each user, so a link there could send the writes of LocalSystem to any file.
- The state for the Admin app (S3): the service writes `DataFolderState` (`Secured` or `Failed`), the error, the time and the last moved folder to `HKLM\SOFTWARE\ParentalControl`. Only an administrator or SYSTEM can write there. The registry is used because the database is not usable when the step fails. The Admin app shows "Set up on <date>" only when the state is `Secured` and the sign-in is set up; else it shows the error. It shows a warning while a moved folder exists. The dashboard banner uses the same state.
- The Admin app does not change the rules. Before it opens the database it checks the folder and `data.db` with the same handle-based code: a link, a file with more than one name, or untrusted content stops the app with a message. It creates a missing folder with the final rules, as the service does.
- The Telegram alert about a moved folder: right after the move the database is new and has no Telegram configuration. The service sends the alert at the first start after the parent configures Telegram again (setting `data_folder_quarantine_notified`). The event log and the Admin app show the move at once.

### D10. Telegram messages
Two new methods in `TelegramBotService`. Both use `SendTextMessageAsync` and put the message into the Offline Notification Queue when the PC is offline, as `SendAdminSignInAlertAsync` does.
- `SendAdminLoginBlockedAlertAsync(source, blockedUntil)`: when a source or all sources reach the limit (D6).
- `SendAdminSetupNeededNoticeAsync()`: one time, at a start when the Remote Admin Interface is not set up and Telegram is configured. The new setting `admin_setup_notice_sent` stops a second message.
- The two messages are in German, because the parent speaks German. They do not follow the language preset (`id` or `en`); issue #11 changes the default language of the child texts separately.
- There is no Telegram command that clears the login limit. The parent clears it in the Admin app (D11).

### D10a. Language of the new texts
- The texts that the web server shows to the parent are new German texts: the setup text on `/admin`, the login errors ("Ungültiger Code."), and the block message with its end time. They are fixed texts in `AdminPage`, not language preset messages.
- The setup text is: "Die Web-Verwaltung ist gesperrt. Richte die Anmeldung in Parental Control Admin auf diesem PC ein."
- The new texts of the WPF Admin app (D11) stay in English, as all other texts of that app.

### D11. Admin app: the sign-in section
The TOTP card in `SettingsView.xaml` becomes "Web admin sign-in":
- The state: "Not set up", "Set up on <date>" (new setting `totp_setup_at`), or "Locked" with the error of the data folder step (D9). "Set up on <date>" shows only when the service secured the data folder.
- "Set up sign-in" (or "Set up a new secret"): a dialog with the QR code, the Base32 text and a code field. On a valid code, one SQLite transaction writes `totp_secret`, `totp_last_step`, `totp_setup_at`, `totp_enabled = "true"`, increments `admin_session_epoch`, and deletes all rows of `admin_login_failures`. `SettingsRepository` gets a method that writes several keys in one transaction.
- "Sign out all web sessions": increments `admin_session_epoch`.
- "Clear login limit": deletes all rows of `admin_login_failures`.
- The dashboard of the Admin app shows a banner while the web admin does not work: the sign-in is not set up, or the data folder step failed (D9).

### D13. No admin sign-in from the session of the child
The child must not use a session that the parent opens on the child's desktop, and the child must not use a stolen cookie from its own processes (security review S4).
- For a connection from this PC (the source `local` of D6), the service finds the client process: `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_CONNECTIONS` (IPv4 or IPv6), the row whose local end is the remote end of the request and whose remote end is the local end of the request. Then `ProcessIdToSessionId`, the user of the process token (`OpenProcess` with `PROCESS_QUERY_LIMITED_INFORMATION`, `TokenUser`) and the user of that session (WTS).
- The pure class `LocalClientPolicy` in Domain refuses the request if the process user or the session user is a Restricted User (from the table `users`), if the process user is a service account (LocalSystem, LocalService, NetworkService: such a process can send requests for each user), or if the owner is not found. A connection from another device needs no check.
- The check runs at the login and at each admin request with an Admin Session (`IsSessionValid`). A refused login gets 403 with a German text, counts no failure, and does not check the code. A refused admin request gets 401. `/admin` shows the German text with 403.
- The parent signs in with the Admin app, from the own Windows account on this PC, or from a phone or a laptop on the LAN.

### D12. ADR
ADR 0001 point 3 made TOTP optional. ADR 0010 records that the Remote Admin Interface always requires TOTP, set up only in the Admin app. This is hard to reverse for the parent, so it gets an ADR.

### New terms
- **Admin Session**: the signed-in state of the parent in the Remote Admin Interface, a token with an issue time and an epoch. It goes into `GLOSSARY.md`.

## Risks / Trade-offs

- [A script of the child guesses TOTP codes] → Each guess has a chance of 3 in 1,000,000, because the server accepts three time steps. With 10 guesses a day from this PC, that is 3,650 guesses a year, a chance of about 1.1 % a year. The parent gets a Telegram message each day that the child reaches the limit. For comparison, 5 guesses a minute would give 7,200 guesses a day and about 2 % a day.
- [The child has a second source, for example a tablet on the LAN or a VM] → Each source adds 10 guesses a day, but the global limit stops at 30 a day (about 3.2 % a year). Then the child can also block the parent's login from the phone for 24 hours. The parent clears the limit in the Admin app, and the Telegram bot still approves Grace Requests without the web.
- [The child blocks the source `local`] → The parent cannot sign in from the browser of this PC for up to 24 hours. The parent uses the phone or clears the limit in the Admin app.
- [Stored XSS (#5) runs a script in the signed-in page of the parent] → PR #23 fixed it on `main`, separately from this change. A Content Security Policy is out of scope and tracked as issue #25.
- [A device on the LAN reads the cookie over HTTP (#7)] → The session is valid for 12 hours at most. "Sign out all web sessions" ends it. #7 handles the transport.
- [The child sees the parent type a code] → The code works one time only (D5).
- [The child changes the clock to make old codes valid] → A standard account has no right to change the system time. Clock Tampering Detection also sends an alert.
- [The child reboots the PC to sign the parent out] → The parent signs in again. The failure counts stay, because they are in SQLite.
- [The child creates `C:\ProgramData\ParentalControl` before the install, as a folder or as a junction] → D9 deletes a link and moves an untrusted folder away at each start, then starts with a new database. The event log and the Admin app show it.
- [The child holds a handle to its folder, so the service cannot move it] → The service stops (fail closed) and does not enforce limits until a start succeeds. The SCM starts it again each 60 seconds. At the next boot the service starts before the child logs on, so no process of the child holds a handle. The event log and the Admin app show the error.
- [An existing install has a data folder that another account can change] → The service moves it away and the parent loses the settings, the limits and the sign-in. This is the safe choice: the child could have changed that data. The Admin app tells the parent to set up again.
- [The owner of an item is an administrator account, not the Administrators group] → The check counts it as not trusted and moves the folder. This happens only when the policy "Default owner for objects created by members of the Administrators group" is set to "Object creator"; the default is the Administrators group.
- [The moved folder keeps files of the child, or an old database] → The service locks the moved folder itself, but not the items in it. With "Bypass traverse checking", the child can still open an item by its full path if the rules of the item allow it. The secret in such a database was readable before. The warning tells the parent to delete the moved folder, and to set up a new secret.
- [The parent signs in on the child's desktop] → D13 refuses it. The parent uses the own Windows account, the Admin app, or a phone.
- [A program of the parent in the parent's session sends requests for the child] → D13 cannot see this. The child cannot start a program in the parent's session.
- [WSL2 and new IPv6 addresses] → A WSL2 VM of the child connects through a NAT address, and Windows makes new temporary IPv6 addresses. Each such address is a new remote source for D6, so the child can reach the global limit of 30 failures and block the parent's remote login for 24 hours. The per-source limit still applies, the parent gets the Telegram alert, and "Clear login limit" in the Admin app ends the block. A standard user cannot install WSL without an administrator.
- [A parent typed a short secret in the old Admin app] → A secret of 10 bytes or more keeps working. A shorter one needs a new setup after the upgrade, and the setup notice tells the parent.
- [The parent never does the setup] → The Remote Admin Interface stays locked. The Telegram bot and the Admin app still work. This is the safe default.
- [A secret that the old Admin app showed is still on a photo or in a screenshot] → The Telegram notice and the release notes tell the parent to set up a new secret if the old one was visible to others.

## Migration Plan

1. The update installs as before (installer or Built-in Updater). The installer does not change.
2. At the first start, the service runs a one-time check:
   - `totp_enabled = "true"` and a valid secret: no change. The parent signs in as before.
   - `totp_enabled = "true"` and no valid secret (empty, other characters, or fewer than 10 bytes): the service sets `totp_enabled = "false"`. Today an empty secret accepts each code. The parent then does the setup again.
   - `totp_enabled` is not `"true"`: no change of data. The Remote Admin Interface is locked until the setup.
   - Not set up and Telegram configured: the service sends the setup notice one time (D10).
3. The service secures the data folder (D9) before it opens the database: it deletes links, moves an untrusted folder away, and sets the owner and the rules. Then `DatabaseManager.Initialize` creates the table `admin_login_failures`.
4. Rollback: install the earlier version. It ignores the new table and the new settings. If the setup is complete, `totp_enabled` is `"true"` and the old version keeps TOTP on. If not, the old version is open again, as before this change.

## Decisions of the user

The user answered the open questions of the proposal:
1. TOTP only, no password (D1).
2. The limits as proposed: 10 failed codes per source and 30 from all sources in 24 hours, an Admin Session of 12 hours, at most 20 sessions (D6, D7).
3. #5 is fixed separately in PR #23, merged before this change.
4. A Content Security Policy is out of scope (issue #25).
5. The one-time Telegram notice on upgrade: yes. A Telegram command to clear the login limit: no (D10).
6. The term "Admin Session" goes into `GLOSSARY.md`.
7. A minimum of 10 bytes for an existing secret (D3).
8. The new texts of the web page and of Telegram are in German (D10, D10a).

## Decisions after the security review

The orchestrator decided these points on 2026-11-26, under the delegation of the user, after the security review of PR #21. Threat model: a 10-year-old with a standard Windows account on the same PC, who can act before the install.
- B1 (blocker): a junction as the data folder made the service create `data.db` in the child's folder. Decision: delete the link itself, create a real folder, never open the database through a link, and fail closed for the database too (D9).
- B2 (blocker): a folder that the child made before the install kept its planted content. Decision: check the owner and the DACL of the folder and of each item; move untrusted content to `ParentalControl.untrusted-<guid>`, start with a new database, log it, send a Telegram alert when Telegram is configured, and show it in the event log and the Admin app. Also on an upgrade (D9).
- S1: check and change the folder through a handle opened with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS` (D9).
- S2: delete links inside the folder, replace files with more than one name, and never lock the web admin because of a link. No TEMP fallback for the log (D9).
- S3: the Admin app shows the real state of the folder step, from the registry (D9, D11).
- S4: refuse a login and an Admin Session from a process of a Restricted User on this PC, and refuse when the owner is not known. The cookie becomes a session cookie (D7, D13).
- Recorded as a risk only: WSL2 NAT addresses and new IPv6 temporary addresses count as new remote sources (Risks).
- Out of scope here: the agent routes (#6) and the update URL (#8, PR #29).
