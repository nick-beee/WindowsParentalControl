# Proposal

## Why

The web server listens on all addresses of the PC over plain HTTP on port 5050 (issue #7). On the home LAN, the TOTP code and the Admin Session cookie go in clear text. The cookie is valid for 12 hours, and the policy against the child's session does not stop a replay from another device of the child. Also, the installer does not open a port in the Windows Firewall (issue #39). On a fresh install, the parent's phone cannot reach the web admin at all. Since change `require-admin-sign-in`, the PC refuses a sign-in from the child's session, so the parent needs the phone or a laptop on the LAN.

## What Changes

- New **LAN Admin Endpoint**: the service serves the Remote Admin Interface over HTTPS on TCP port 5051 on all interfaces. It serves only `/admin`, its static files and the routes below `/api/admin/`.
- New **Web Admin Certificate**: the service makes a self-signed certificate for the LAN Admin Endpoint at the first start, keeps it, and makes a new one before it expires. The private key cannot be exported, and only SYSTEM and the Administrators group can use it.
- Port 5050 stays plain HTTP for the Session Agent, the child request page and the parent on this PC. **BREAKING**: port 5050 answers only requests from this PC. A request from another device gets 403 and a text with the HTTPS address.
- The LAN Admin Endpoint answers only a source on this PC or in a local subnet of this PC. **BREAKING**: access over Tailscale, another VPN or another subnet stops (README and `CONTEXT.md` promise Tailscale).
- The admin cookie gets the `Secure` flag when the service issues it over HTTPS. The `Origin` check takes the scheme from the connection. The service sends no HSTS.
- The installer adds one inbound firewall rule: TCP 5051, Private profile only, remote addresses LocalSubnet, for the service program only. It replaces the rule at each install and upgrade, and it removes the rule at uninstall. It never opens port 5050 and never opens a Public profile. Issue #39 proposed port 5050; this change opens only the HTTPS port.
- The Admin app shows the LAN address of the web admin (text and QR code), the SHA-256 fingerprint of the Web Admin Certificate, and a warning if the network of the PC is Public.
- The handle of a taken port (log, Admin app warning, Telegram alert) covers port 5051 too. The two ports start and retry independently.
- The service sends a Telegram notice when it makes a new Web Admin Certificate, so the parent expects one new browser warning.
- New ADR 0012 records HTTPS with a self-signed certificate on a second port.

## Capabilities

### New Capabilities
- `admin-network`: which networks and which transport reach the Remote Admin Interface: the two ports, the source checks, the Web Admin Certificate, the firewall rule and the address that the Admin app shows.

### Modified Capabilities
- `admin-access`: the `Origin` check uses the scheme of the connection; the policy against the child's session also covers the LAN Admin Endpoint; the handle of a taken port covers both ports.

## Out of scope

- The home server of stage 4 that hosts the panel. design.md explains how this design fits it.
- Access from outside the local subnet (Tailscale, VPN, port forward). design.md gives a later path.
- A certificate that the phone trusts without a warning (a local CA on the phone, or a public certificate for an own domain).
- HTTPS for the Session Agent and the child request page. They stay on loopback HTTP.
- An automatic change of the Windows network profile from Public to Private.
- The channel between the Session Agent and the service (#6).

## Impact

- `src/ParentalControl.Service/Web/WebServerHost.cs`: a second Kestrel host for port 5051 with HTTPS, a source check on both ports, the `Secure` cookie flag, the port squat handle for both ports.
- `src/ParentalControl.Domain/AdminAccess/AdminRoutePolicy.cs`: the expected origin uses the scheme of the connection.
- New pure classes in `src/ParentalControl.Domain/Web/`: `LanSourcePolicy`, `LanEndpointRoutes`, `WebAdminCertificatePolicy`, `LanAdminUrls`.
- New platform code in `src/ParentalControl.Core/Platform/`: the certificate store and the CNG key (`WebAdminCertificateStore`), the network category (`NetworkCategoryReader`).
- `src/ParentalControl.Admin`: the "Web admin sign-in" card and the dashboard banner show the LAN address, the QR code, the fingerprint and the Public network warning.
- `installer/ParentalControl.iss`: the firewall rule at install, upgrade and uninstall.
- Registry values below `HKLM\SOFTWARE\ParentalControl`: `WebAdminCertThumbprint`, `WebLanPortBlockedBy`.
- Docs: ADR 0012, `GLOSSARY.md`, `CONTEXT.md`, `README.md`, `docs/vm.md`.
- No change to the Session Agent, to the child request page or to the database schema.
