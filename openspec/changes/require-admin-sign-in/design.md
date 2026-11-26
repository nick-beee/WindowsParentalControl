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
- *Alternative: a password and TOTP.* Rejected for now: a second secret to keep and to recover, for one parent on a home network. The limit in D6 makes a TOTP guess too slow. This is an open question for the user.
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
- The Remote Admin Interface is set up when `totp_enabled` is `"true"` and `totp_secret` is a valid Base32 secret of at least 16 bytes.
- Not set up means: `/admin` shows the setup text, each admin route answers 401, and the login refuses each code.
- *Alternative: a new key such as `admin_totp_enrolled`.* Rejected: the old key already has the right value on each install where TOTP was on, so the upgrade needs no data migration for that case. A downgrade to the old version also keeps TOTP on.

### D4. One gate for all admin routes
A middleware (`app.Use`) before the endpoints checks each request below `/api/admin/`. The 30 `CheckAuth` calls go away.
- A pure class `AdminRoutePolicy` in `ParentalControl.Domain` classifies a method and a path as `Open`, `Login`, `Logout` or `Admin`. Each path below `/api/admin/` that is not the login or the logout is `Admin`. The match uses whole path segments and ignores case.
- For `Admin`: the middleware checks the `Origin` (D8), then the Admin Session (D7). If one check fails, it answers 401 (or 403 for the `Origin`) and the endpoint does not run.
- `GET /admin` itself shows one of three pages: the setup text, the login form, or the dashboard.
- A test on the VM builds the endpoints and lists them through `EndpointDataSource`. It asserts that each route is in the open list (`/`, `/request`, `/api/request`, `/api/agent/*`, `/admin`) or below `/api/admin/`.
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
- The source is `HttpContext.Connection.RemoteIpAddress`, never `X-Forwarded-For`. An IPv4-mapped IPv6 address becomes IPv4. Each loopback address and each address of this PC becomes the source `local`. The service gets the addresses of this PC from `NetworkInterface.GetAllNetworkInterfaces()` (it calls `GetAdaptersAddresses`), with a cache of 60 seconds.
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
- The store keeps at most 20 sessions and removes the oldest one first. It removes expired sessions at each login.
- A restart of the service clears the store. The tokens are not written to disk.
- The logout becomes `POST /api/admin/logout`, and the dashboard sends it from a form.
- The cookie stays HttpOnly and SameSite=Strict with an expiry of 12 hours. It gets no `Secure` flag while the server uses HTTP (#7).
- *Alternative: tokens in SQLite, so a restart keeps the sessions.* Rejected: a token on disk is a further secret to protect, and a new login after a restart is a small cost.

### D8. `Origin` check
For each POST, PUT and DELETE below `/api/admin/` (the login and the logout included): if the request has an `Origin` header and it is not `http://` plus the `Host` header of the request, the middleware answers 403.
- SameSite=Strict does not help here, because `localhost:8000` and `localhost:5050` are the same site. A page that the child serves on another port is the case to stop.
- A request without `Origin` passes this check. A browser always sends `Origin` on a cross-origin POST, and a client that is not a browser has no cookie of the parent.
- GET routes below `/api/admin/` change no data, with one exception: `GET /api/admin/logout`, which D7 changes to a POST.

### D9. Access rules of the data folder at each start
`DatabaseManager.Initialize` sets the access rules of the data folder at each start, not only when it creates it, and only when `PARENTAL_CONTROL_DATA_DIR` is not set (the tests use that variable).
- Calls: `DirectoryInfo.GetAccessControl`, `DirectorySecurity.SetAccessRuleProtection(true, false)`, a `FileSystemAccessRule` with full control for `WellKnownSidType.LocalSystemSid` and for `WellKnownSidType.BuiltinAdministratorsSid` (container and object inherit), `DirectoryInfo.SetAccessControl` (it calls `SetNamedSecurityInfoW`, and Windows applies the inherited rules to `data.db` and the other files).
- An error goes to the log at the level Error. It does not stop the start.
- The Admin app also calls `Initialize`. It runs as an administrator, so the same code works there.

### D10. Telegram messages
Two new methods in `TelegramBotService`. Both use `SendTextMessageAsync` and put the message into the Offline Notification Queue when the PC is offline, as `SendAdminSignInAlertAsync` does.
- `SendAdminLoginBlockedAlertAsync(source, blockedUntil)`: when a source or all sources reach the limit (D6).
- `SendAdminSetupNeededNoticeAsync()`: one time, at a start when the Remote Admin Interface is not set up and Telegram is configured. The new setting `admin_setup_notice_sent` stops a second message.

### D11. Admin app: the sign-in section
The TOTP card in `SettingsView.xaml` becomes "Web admin sign-in":
- The state: "Not set up" or "Set up on <date>" (new setting `totp_setup_at`).
- "Set up sign-in" (or "Set up a new secret"): a dialog with the QR code, the Base32 text and a code field. On a valid code, one SQLite transaction writes `totp_secret`, `totp_last_step`, `totp_setup_at`, `totp_enabled = "true"`, increments `admin_session_epoch`, and deletes all rows of `admin_login_failures`. `SettingsRepository` gets a method that writes several keys in one transaction.
- "Sign out all web sessions": increments `admin_session_epoch`.
- "Clear login limit": deletes all rows of `admin_login_failures`.
- The dashboard of the Admin app shows a banner while the sign-in is not set up: "The web admin is locked until you set up the sign-in."

### D12. ADR
ADR 0001 point 3 made TOTP optional. ADR 0009 records that the Remote Admin Interface always requires TOTP, set up only in the Admin app. This is hard to reverse for the parent, so it gets an ADR.

### New terms
- **Admin Session**: the signed-in state of the parent in the Remote Admin Interface, a token with an issue time and an epoch. It goes into `GLOSSARY.md`.

## Risks / Trade-offs

- [A script of the child guesses TOTP codes] → Each guess has a chance of 3 in 1,000,000, because the server accepts three time steps. With 10 guesses a day from this PC, that is 3,650 guesses a year, a chance of about 1.1 % a year. The parent gets a Telegram message each day that the child reaches the limit. For comparison, 5 guesses a minute would give 7,200 guesses a day and about 2 % a day.
- [The child has a second source, for example a tablet on the LAN or a VM] → Each source adds 10 guesses a day, but the global limit stops at 30 a day (about 3.2 % a year). Then the child can also block the parent's login from the phone for 24 hours. The parent clears the limit in the Admin app, and the Telegram bot still approves Grace Requests without the web.
- [The child blocks the source `local`] → The parent cannot sign in from the browser of this PC for up to 24 hours. The parent uses the phone or clears the limit in the Admin app.
- [Stored XSS (#5) runs a script in the signed-in page of the parent] → This change does not fix it. The fix of #5 must merge before or with this change. A Content Security Policy is an open question.
- [A device on the LAN reads the cookie over HTTP (#7)] → The session is valid for 12 hours at most. "Sign out all web sessions" ends it. #7 handles the transport.
- [The child sees the parent type a code] → The code works one time only (D5).
- [The child changes the clock to make old codes valid] → A standard account has no right to change the system time. Clock Tampering Detection also sends an alert.
- [The child reboots the PC to sign the parent out] → The parent signs in again. The failure counts stay, because they are in SQLite.
- [The parent never does the setup] → The Remote Admin Interface stays locked. The Telegram bot and the Admin app still work. This is the safe default.
- [A secret that the old Admin app showed is still on a photo or in a screenshot] → The Telegram notice and the release notes tell the parent to set up a new secret if the old one was visible to others.

## Migration Plan

1. The update installs as before (installer or Built-in Updater). The installer does not change.
2. At the first start, the service runs a one-time check:
   - `totp_enabled = "true"` and a valid secret: no change. The parent signs in as before.
   - `totp_enabled = "true"` and no valid secret: the service sets `totp_enabled = "false"`. Today such an install accepts each code.
   - `totp_enabled` is not `"true"`: no change of data. The Remote Admin Interface is locked until the setup.
   - Not set up and Telegram configured: the service sends the setup notice one time (D10).
3. `DatabaseManager.Initialize` creates the table `admin_login_failures` and sets the access rules of the data folder (D9).
4. Rollback: install the earlier version. It ignores the new table and the new settings. If the setup is complete, `totp_enabled` is `"true"` and the old version keeps TOTP on. If not, the old version is open again, as before this change.

## Open Questions

- The text of the setup page, of the login messages and of the two Telegram messages. It follows the language preset; the default language changes in #11.
- The limit of 20 Admin Sessions in memory. Any small number works.
