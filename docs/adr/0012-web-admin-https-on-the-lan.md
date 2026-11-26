# 12. The Web Admin on the LAN: HTTPS on a Second Port with a Self-Signed Certificate

## Status
Accepted (2026-11-26). Decided by the orchestrator under the user's delegation. The OpenSpec change `web-admin-on-the-lan` implements it. Changes point 2 of [ADR 0001](0001-embedded-kestrel-and-wts-notifications.md) (access over Tailscale).

## Context
The web server listens on all addresses of the PC over plain HTTP on port 5050 (#7). The installer opens no firewall port, so on a fresh install no phone reaches the web admin (#39). Since [ADR 0010](0010-web-admin-always-requires-totp.md) and change `require-admin-sign-in`, the PC refuses a sign-in from the child's session, so the parent needs a phone or a laptop on the LAN.

The child shares the home LAN and knows the Wi-Fi password. The TOTP code works one time only, but the Admin Session cookie is valid for 12 hours, and the policy against the child's session does not stop a replay from another device of the child. Over HTTP, the cookie goes in clear text.

The Session Agent and the widget have `http://127.0.0.1:5050` and `http://localhost:5050` in their code. Traffic on loopback does not leave the PC.

## Decision
1. **Two ports.** Port 5050 stays plain HTTP and answers only this PC: the Session Agent, the child request page, and the parent in the own Windows session. Port 5051, the LAN Admin Endpoint, is HTTPS only and serves only the admin page and `/api/admin/*`.
2. **Bind on all interfaces, limit by source.** Each port is one exclusive dual-mode socket on `[::]`, as in SF3 of `require-admin-sign-in`. The service answers port 5051 only for this PC and for the on-link subnets of its interfaces. It takes the source from the TCP connection, never from a header.
3. **Firewall.** The installer adds one inbound rule: TCP 5051, Private profile, remote addresses LocalSubnet, the service program only. It replaces the rule at each install and removes it at uninstall. It never opens 5050 and never the Public profile.
4. **Self-signed certificate made by the service.** ECDSA P-256, DNS names of the PC only (no IP addresses), valid for 825 days, renewed 30 days before the end. The key is a non-exportable machine key that only SYSTEM and Administrators can use, in `LocalMachine\My`. The fingerprint stays the same until the renewal, and the Admin app shows it. The service sends a Telegram notice at a renewal.
5. **Cookie and headers.** The cookie gets `Secure` over HTTPS. The `Origin` check takes the scheme from the connection. No HSTS, because with a self-signed certificate HSTS blocks the page without the option to continue.
6. **No access from outside the local subnet.** Tailscale, VPN and port forwards stop. A later setting can add extra ranges to the source check and to the firewall rule.

### Rejected alternatives
- **Plain HTTP with a session bound to the client IP**: the cookie still goes in clear text, and a device of the child can take the IP address of the phone.
- **HTTPS on port 5050**: the Session Agent and the widget would need a change and a trusted certificate, and the child's browser would warn on the request page.
- **A bind to the addresses of the Private adapters only**: the addresses change with DHCP, and the bind would give up the SF3 protection against a program of the child that takes the port.
- **A local CA that the parent installs on the phone**: no warnings, but many steps on a phone, and a leaked CA key with full trust lets an attacker read each HTTPS site on the parent's phone.
- **A public certificate for an own domain**: no warnings, but it needs a domain and a DNS API token on the child's PC. It fits the home server of stage 4.
- **No web admin on the LAN**: the Telegram bot does not have all functions, and the parent mostly uses a phone.

## Consequences
- No secret of the parent goes in clear text over the LAN. A passive capture gives nothing.
- At the first visit, each browser of the parent shows a warning one time, and some browsers show it again after a time. The parent should compare the fingerprint at the first visit. If the parent accepts a later warning without a check, an active attacker on the LAN can read the session.
- The phone must be in the same subnet as the PC, and the network of the PC must have the category Private. The Admin app warns if it is Public.
- Access over Tailscale stops until a later change adds extra ranges.
- For stage 4, the admin page (ADR 0009) moves to the home server without a change. The home server is the place for a certificate that the phone trusts. If the server calls the LAN Admin Endpoint, it is one LAN source and needs its own authentication.
- A downgrade leaves the firewall rule and the certificate in place. They are harmless, because the older service does not listen on 5051.
