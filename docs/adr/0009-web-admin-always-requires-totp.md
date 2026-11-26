# 9. The Web Admin Interface Always Requires TOTP, Set Up Only in the Admin App

## Status
Accepted. Replaces point 3 of [ADR 0001](0001-embedded-kestrel-and-wts-notifications.md).

## Context
ADR 0001 made Authenticator TOTP an optional protection of the Web Admin Interface, and the switch was off by default. While TOTP was off, each route below `/api/admin/` worked without a sign-in. The Restricted User can send any request to `127.0.0.1:5050`, so the child could grant Bonus Minutes, remove a Category Limit, or change the schedule (issue #18). Each limit of the project depends on this gate.

The web server runs over plain HTTP on all addresses (#7), and the child uses the same PC as the parent. A credential that the child sees once, or that a device on the LAN reads once, must not stay useful.

## Decision
1. **TOTP is always on.** The Remote Admin Interface always requires an Admin Session. The "Enable Authenticator 2FA" switch is removed. The setting `totp_enabled` now means "the setup is complete".
2. **Setup only in the Admin app.** The parent sets up the TOTP secret only in the WPF Admin app, which runs as an administrator. The app saves the secret only after the parent confirms one current code, and it does not show the secret again. No web route creates, shows or changes the secret.
3. **Locked until the setup.** Before the setup, `/admin` shows only a setup text, the login refuses each code, and each admin route answers 401.
4. **One gate, deny by default.** A middleware protects every route below `/api/admin/` except the login and the logout. A new admin route is protected without an extra line of code.
5. **Hard to guess, easy to recover.** A code works one time only. Failed codes have a limit of 10 per source and 30 from all sources in 24 hours, stored in SQLite. The Admin app can set up a new secret, clear the login limit, and end all Admin Sessions.

### Rejected alternatives
- **A password** instead of TOTP: the child can see the parent type it, the parent can reuse it in other places, and over plain HTTP a password that a LAN device reads stays valid.
- **A password and TOTP**: a second secret to keep and to recover, for one parent on a home network. The login limit already makes a TOTP guess too slow.
- **A sign-in with the Windows account of the parent** (Negotiate/NTLM): it does not work from the parent's phone, and the browser on the child's PC can ask the child for the credentials.
- **A first-run setup page on the web**: the first caller gets the credential, and the child on `127.0.0.1` can win this race.
- **A setup page in the installer**: a silent update has no UI, and Inno Setup cannot show a QR code without an extra DLL.
- **A one-time setup code in a file for administrators only**: it needs an administrator anyway, and the Admin app is simpler for the parent.

## Consequences
- An upgrade with TOTP on and a valid secret keeps working. An upgrade with TOTP off gives a locked Remote Admin Interface until the parent does the setup in the Admin app. The service sends one Telegram notice about it.
- The parent can always get back in through the Admin app on the PC. A lost phone needs a new setup, not a reinstall.
- A downgrade to an older version keeps TOTP on if the setup is complete. If the setup is not complete, the older version is open again.
- The service makes the Administrators group the owner of the data folder at each start, so the child cannot read the secret from `data.db`.
