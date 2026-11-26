# Proposal

## Why

The Remote Admin Interface has no sign-in while Authenticator TOTP is off, and TOTP is off by default (issue #18). Then the child, who can send any request to `127.0.0.1:5050`, can grant Bonus Minutes to itself, remove a Category Limit, move a game to System, and change the schedule. Each limit of this project depends on this gate, so the gate must be closed before stage 2 adds more limits.

## What Changes

- **BREAKING:** The Remote Admin Interface always requires a signed-in admin. The "Enable Authenticator 2FA" switch goes away. No `/api/admin/*` route works without an Admin Session, also on a fresh install.
- The admin credential is Authenticator TOTP. The parent sets it up only in the WPF Admin app (Parental Control Admin), which runs as an administrator. The parent confirms one code from the phone before the app saves the secret. The web server has no first-run setup page.
- Before the setup, the `/admin` page shows only a short text: "Set up the sign-in in Parental Control Admin on this PC." Every admin route refuses. The child-facing routes work as before.
- The login refuses every code while no credential is set up. Today it accepts any code when the secret is empty.
- The login accepts each TOTP code one time only.
- Failed logins have a limit: at most 10 failed codes in 24 hours from one source, where all addresses of this PC count as one source. At most 30 failed codes in 24 hours from all sources together. The counters survive a restart of the service and of the PC. The service sends a Telegram alert when a source reaches the limit.
- An Admin Session has a server-side expiry of 12 hours. A restart of the service ends all Admin Sessions. A new secret or the button "Sign out all web sessions" in the WPF Admin app ends all Admin Sessions at once.
- A state-changing admin request from a page of another origin is refused (an `Origin` check). The logout becomes a POST.
- The WPF Admin app gets a recovery path: set up a new secret, clear the login limit, and sign out all web sessions. The app does not show the secret again after the setup.
- Upgrade: an install with TOTP on and a valid secret (Base32, at least 10 bytes) keeps working with the same authenticator entry. An install with TOTP off gets a locked Remote Admin Interface until the parent does the setup. If Telegram is configured, the service sends one message that tells the parent to do the setup.
- At each start the service makes the Administrators group the owner of `C:\ProgramData\ParentalControl` and replaces all access rules of the folder and its files (SYSTEM and Administrators only). Then the child cannot read the secret from `data.db`, also on an older install or when the child made the folder before the install. If this fails, the Remote Admin Interface stays locked.

## Out of scope

- The web server still listens on all IP addresses over plain HTTP (#7). So the session cookie has no `Secure` flag, and a device on the LAN can see a TOTP code or a cookie. A TOTP code is useless after about 90 seconds and after its first use.
- Authentication of the Session Agent routes (`/api/agent/*`, #6). They stay open in this change.
- The stored XSS in the Grace Request table (#5). It is a separate fix, but this change depends on it: a script in the child's text runs in the signed-in page of the parent. The fix of #5 must merge before or with this change.
- A Content Security Policy for the admin page.
- `update/apply` with a URL from the request (#8). It stays an admin-only route.
- The Telegram bot. It already checks the configured chat ID.
- A password, more than one admin account, and a sign-in from the Windows account of the parent.
- The hard rule does not change: nothing in this change closes or kills a program of the child.

## Capabilities

### New Capabilities
- `admin-access`: who can use the admin functions of the web server, how the parent sets up and recovers the admin credential, the Admin Session, the login limits, and which routes stay open for the child and the Session Agent.

### Modified Capabilities
<!-- None. The app-categories requirement "Parent edits categories and rules in the web admin" already requires a signed-in admin. This change makes that true on every install. -->

## Impact

- `src/ParentalControl.Service/Web/WebServerHost.cs`: one gate for all `/api/admin/*` routes instead of `CheckAuth` in each route, the login, the logout, the session store.
- `src/ParentalControl.Service/Web/Pages/AdminPage.cs`: a "not set up" page, a "too many attempts" message, the logout as a POST.
- `src/ParentalControl.Domain`: new pure classes for the route policy, the login limit, the TOTP replay check and the Admin Session store, with xUnit tests.
- `src/ParentalControl.Core`: `SettingsRepository` keys, a new table for login failures, the data folder access rules in `DatabaseManager`, a Telegram alert.
- `src/ParentalControl.Admin`: the settings view and the setup dialog.
- `docs/adr/0009-*.md`: the Remote Admin Interface always requires TOTP. It replaces point 3 of ADR 0001.
- No change to the installer, the Session Agent, the request page or the enforcement.
