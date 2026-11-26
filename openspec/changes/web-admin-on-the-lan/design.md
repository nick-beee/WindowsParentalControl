# Design

## Context

See proposal.md (Why) for the problem, and `specs/admin-network/spec.md` and `specs/admin-access/spec.md` for the required behaviour.

Current state that shapes the approach:
- `WebServerHost` runs one Kestrel host. Since SF3 of `require-admin-sign-in` (D16), it binds one socket on `[::]:5050` in dual mode with `SO_EXCLUSIVEADDRUSE` (`CreateExclusiveListenSocket`). It binds before the data folder step, and a middleware answers 503 until the start gate opens. On a failed bind, `OnBindFailedAsync` reads the listener rows of the TCP table (`GetExtendedTcpTable`, `TCP_TABLE_OWNER_PID_LISTENER`), logs the program, writes the registry value `WebPortBlockedBy`, and sends one Telegram alert after the gate.
- The Session Agent uses `http://127.0.0.1:5050/api/agent/*` (`ActivityTracker.cs`, `SessionEndWatcher.cs`, `AgentController.cs`). The widget opens `http://localhost:5050/request` in the child's browser. The scheme and the port are in the code of the agent.
- `LoginSource.Of` (D6) gives the source `local` for a loopback address and for each address of this PC. The addresses come from `NetworkInterface.GetAllNetworkInterfaces()` (`GetAdaptersAddresses`) with a cache of 60 seconds.
- `LocalClientPolicy` (D13) refuses a login and each admin request with an Admin Session from a process of a Restricted User, from a process in the session of a Restricted User, or from a service account. It finds the process from the TCP table, so it works on each port.
- `AdminRoutePolicy.Decide` (D8) accepts an `Origin` only if it equals `"http://" + Host`.
- The login sets the cookie `pc_admin_session` with `HttpOnly` and `SameSite=Strict`, without `Secure` (D7: "while the server uses HTTP (#7)").
- The installer adds no firewall rule. The Built-in Updater runs the installer silently, so `CurStepChanged(ssPostInstall)` runs at each install and each upgrade.
- The Admin app runs as an administrator. It already draws QR codes (for the TOTP setup in `SettingsViewModel`) and reads the admin-only registry key `HKLM\SOFTWARE\ParentalControl`.
- README line 30, `CONTEXT.md` ("Remote Admin Interface") and ADR 0001 say that the web admin is reachable "over the local network or Tailscale" on `http://0.0.0.0:5050`.

Threat model, the same as in `require-admin-sign-in`: the child (10 years, a standard account) on this PC, and the child's other devices on the same home LAN. The child knows the Wi-Fi password and can run scripts. Other devices of guests can be on the LAN. The parent uses a phone or a laptop on the LAN, or the parent's own Windows account on this PC.

## Goals / Non-Goals

**Goals:**
- No secret of the parent (the Admin Session cookie, the TOTP code, the admin data) goes in clear text over the LAN.
- A phone on the home LAN can reach the web admin after a normal install, without a manual firewall rule.
- No device outside the local subnet reaches the web admin, also if the Windows Firewall is off or a third-party firewall replaces it.
- No change to the Session Agent, the child request page or the SF3 protection of port 5050.
- The policy logic (the source check, the allowed paths, the certificate renewal, the list of addresses) runs and is tested on the Mac.

**Non-Goals:**
- A certificate that the phone trusts without a warning.
- Access from outside the local subnet (Tailscale, VPN, port forward).
- The home server of stage 4.

## Decisions

Each decision below was decided by the orchestrator under the user's delegation (2026-11-26).

### D1. Two ports: loopback HTTP on 5050, HTTPS for the LAN on 5051
Decided by the orchestrator under the user's delegation (2026-11-26).
- Port 5050 stays plain HTTP. It answers only the source `local` (`LoginSource.Of`): a loopback address or an address of this PC. Each other source gets 403 with a German text that names `https://<address>:5051/admin`. The source check runs before the 503 gate of SF3 and before routing, and it needs no database.
- New port 5051 (the **LAN Admin Endpoint**) is HTTPS only. It serves `/admin` (also `/admin/`), `/admin/assets/{file}` (the Vite bundle, `base: '/admin/'`) and the routes below `/api/admin/`. A check of `web/admin/src` and of the routes in `WebServerHost` on 2026-11-26 found no other path that the admin page uses: the screen captures are below `/api/admin/captures/`, and the update routes are below `/api/admin/update/`. No Telegram text builds a link to port 5050. A pure class `LanEndpointRoutes` in Domain decides the allowed paths; each other path gets 404. The same gates as on 5050 run on it: the 503 gate, the admin gate (`AdminRoutePolicy`), the login limit (`LoginThrottle`) and `LocalClientPolicy`.
- Traffic on loopback does not leave the PC, so HTTP on 5050 does not expose a secret on the LAN. The parent in the own Windows session keeps `http://localhost:5050/admin` without a certificate warning.
- *Alternative: HTTPS on 5050.* Rejected: the Session Agent and the widget have `http://…:5050` in their code, the agent would need to trust the certificate, and the child's browser would show a warning on the request page.
- *Alternative: HTTP and HTTPS on the same port with protocol detection.* Rejected: Kestrel has no such mode, and a custom detection adds a parser in front of the security gates.
- *Alternative: a redirect from HTTP 5050 to HTTPS for a LAN source.* Rejected: the firewall rule does not open 5050, so a LAN device sees the redirect only when the firewall is off. A redirect over HTTP can be changed by an attacker on the LAN. A 403 with the address as text is enough.
- *Alternative: port 443.* Rejected: other software (IIS, a game launcher, a dev tool) can use it, and the address `https://<IP>/admin` gives no gain for a QR code. Port 5051 is fixed and not a setting, so the firewall rule and the address always agree.

### D2. Binding: all interfaces, one exclusive socket per port, two independent hosts
Decided by the orchestrator under the user's delegation (2026-11-26).
- Port 5051 uses the same `CreateExclusiveListenSocket` as 5050: one socket on `[::]` with `DualMode = true` and `SO_EXCLUSIVEADDRUSE`. Port 5050 does not change.
- Each port is its own Kestrel host (`WebApplication`) with its own `RunWithRetryAsync`. The 5051 host binds without a certificate (D6), so a missing certificate never frees the port. A taken port 5051 or a missing certificate does not stop port 5050, so the Session Agent samples, and with them the Category Usage, keep working.
- The start worker waits for the first bind try of both hosts, together at most 5 seconds (the SF3 wait), before the data folder step.
- *Alternative: bind only the addresses of the adapters in a Private network.* Rejected: the addresses change with DHCP and with a new Wi-Fi, so the service must watch the network and bind again. A bind to specific addresses also gives up the SF3 guarantee: a program of the child can then bind `127.0.0.1` or another address of the port. The firewall rule (D4) and the source check (D3) limit the exposure instead.

### D3. Source check on the LAN Admin Endpoint
Decided by the orchestrator under the user's delegation (2026-11-26).
- A pure class `LanSourcePolicy` in Domain gets the remote address of the TCP connection and the list of (address, prefix length) of the active interfaces of this PC. It allows the source `local`, an address in the on-link prefix of an interface of the same family, and an IPv6 link-local address (`fe80::/10`). It refuses each other address, and a connection without a remote address.
- The service reads the list from `NetworkInterface.GetAllNetworkInterfaces()` (`GetAdaptersAddresses`): interfaces with `OperationalStatus.Up`, not `Loopback`, with `UnicastIPAddressInformation.PrefixLength`. It uses a cache of 60 seconds. If an address is not in a known subnet, it reads the list again before it refuses, as D6 does for `local`.
- The address comes from `HttpContext.Connection.RemoteIpAddress` only. The service uses no forwarded headers middleware.
- A refused request gets 403 before the login code is read, so it counts no failed login.
- Why a check in the service in addition to the firewall rule: the parent can turn the Windows Firewall off, and a third-party security suite can replace it. Then the rule of D4 has no effect.
- *Alternative: rely on the firewall only.* Rejected for the reason above. The check costs one pure class.
- *Alternative: allow all private ranges (RFC 1918, `fc00::/7`, `100.64.0.0/10`).* Rejected: a private range is not the home LAN. A second network, a VPN or a Tailscale peer would get access without a decision of the parent.

### D4. Firewall rule
Decided by the orchestrator under the user's delegation (2026-11-26).
- In `CurStepChanged(ssPostInstall)`, before `StartService()`, the installer runs:
  - `netsh advfirewall firewall delete rule name="Parental Control Web Admin (HTTPS)"` (the result is ignored: the rule can be absent), then
  - `netsh advfirewall firewall add rule name="Parental Control Web Admin (HTTPS)" dir=in action=allow protocol=TCP localport=5051 profile=private remoteip=localsubnet program="{app}\service\ParentalControl.Service.exe" edge=no enable=yes`.
- `netsh … add rule` makes a second rule with the same name each time. The delete before the add keeps exactly one rule, also after many silent upgrades.
- `program=` ties the rule to the service. A program of the child that takes port 5051 (D2, SF3) gets no connection from the LAN, because a standard user cannot add a firewall rule.
- In `CurUninstallStepChanged(usUninstall)`, the uninstaller runs the same `delete rule`.
- The installer does not open port 5050. Issue #39 proposed port 5050; this change opens only the HTTPS port, because port 5050 now serves only this PC (D1).
- Only the Private profile. The Public profile is for networks that the user does not trust (a café, a hotel). The Domain profile is for a network that an organization manages, which a home PC does not have.
- `remoteip=localsubnet` covers the IPv4 and the IPv6 subnets of the PC. It is the firewall form of D3.
- *Alternative: the service adds the rule at each start.* Rejected: the installer already runs as an administrator at each install and upgrade, and a service that changes the firewall is a larger attack surface. If the parent removes the rule, that is a decision of the parent.
- *Alternative: PowerShell `New-NetFirewallRule`.* Rejected: it starts PowerShell for one rule, and the execution policy can block a script. `netsh` is on each Windows 10 and 11.

### D5. HTTPS with a self-signed certificate: the phone UX against the gain
Decided by the orchestrator under the user's delegation (2026-11-26).

What the parent sees on the phone:
- At the first visit, the browser shows a full-page warning that the connection is not private. The parent must open the details and choose to continue. After that, the page works.
- The browser remembers this choice for that certificate. Some browsers remember it only for a limited time or until a restart, and then show the warning again.
- The address bar marks the connection as not secure.
- A check of the fingerprint on a phone is possible in some browsers, but it takes several steps. Most parents will not do it.

What HTTPS gives:
- The TOTP code alone has little value for an attacker: it works one time only (D5 of `require-admin-sign-in`). The asset is the Admin Session cookie: it is valid for 12 hours, and `LocalClientPolicy` does not stop a replay from another device of the child on the LAN.
- Without HTTPS, each device that sees the traffic reads the cookie. On a Wi-Fi with WPA2-Personal, a device that knows the Wi-Fi password and records the connection start of the phone can decrypt the traffic of the phone. The child knows the Wi-Fi password.
- With HTTPS, a passive capture gives nothing, also if the parent never checks the fingerprint.
- An active attack (for example ARP spoofing with a tool) shows the parent a new warning. If the parent accepts it without a check, the attack works. If the parent checked the fingerprint at the first visit and refuses a later, new warning, the attack fails. The Admin app text tells the parent this (D8).

Decision: HTTPS with a self-signed certificate on the LAN Admin Endpoint. One warning per browser is a small cost against a 12-hour admin cookie in clear text on a LAN that the child shares. The design keeps the fingerprint stable (D6), so a warning is rare and a new warning is a signal.

Rejected alternatives:
- *Plain HTTP, and bind the Admin Session to the client IP.* The cookie still goes in clear text. A device of the child on the same LAN can take the IP address of the phone when the phone leaves. IPv6 temporary addresses and a switch between IPv4 and IPv6 would end the session of the parent without a reason.
- *A local CA that the parent installs on the phone.* No warnings and full protection, but about eight steps on an iPhone (download a profile, install it, then turn on full trust in a hidden menu). A CA with full trust on the phone of the parent is a large risk: if its key leaks from this PC, an attacker can read each HTTPS site on that phone. Name constraints limit this, but browser support for name constraints on IP addresses is not reliable. A later option for the home server (stage 4), not for this PC.
- *A public certificate for an own domain (ACME with a DNS challenge).* No warnings, but it needs a domain, an API token of the DNS provider on the child's PC, and internet access for the renewal. It fits the home server of stage 4.
- *No web admin on the LAN; the parent uses only Telegram and the PC.* The Telegram bot does not have all functions (Category Limits, App Rules, the timeline), and the parent mostly uses a phone.
- *Encryption in the page (for example a key exchange in JavaScript over HTTP).* An attacker on the LAN can change the JavaScript itself. Only TLS protects the code of the page.

### D6. The Web Admin Certificate
Decided by the orchestrator under the user's delegation (2026-11-26).
- **Who makes it.** The service, not the installer. At the start of the 5051 host and once a day, the pure class `WebAdminCertificatePolicy` in Domain decides whether a new certificate is necessary: none exists, it is not valid yet or any more, it expires in 30 days or less, its names differ from the wanted names (a new computer name), or its key is not ECDSA P-256. The service makes it, so an upgrade of an existing install gets a certificate without an installer step, and the renewal needs no installer. The first start after the install makes it, so in practice it exists at the end of the install.
- **Content.** ECDSA P-256 key. Subject `CN=Parental Control Web Admin (<COMPUTERNAME>)`. Subject Alternative Names: DNS `<COMPUTERNAME>`, DNS `<COMPUTERNAME>.local`, DNS `localhost`. Extended Key Usage: server authentication. Basic Constraints: not a CA. Valid from 1 hour before the creation for 825 days. Apple platforms refuse a TLS server certificate that is valid for more than 825 days.
- **No IP addresses in the names.** A self-signed certificate gives a warning in any case, so an IP address in the names does not remove a warning. An IP address in the names would force a new certificate at each new DHCP address, and each new certificate gives a new warning and a new fingerprint. A stable fingerprint is worth more (D5).
- **Creation.** `CngKey.Create(CngAlgorithm.ECDsaP256, "ParentalControl-WebAdmin-<guid>", …)` with `CngKeyCreationOptions.MachineKey`, `CngExportPolicies.None`, and the property `Security Descr` (`NCRYPT_SECURITY_DESCR_PROPERTY`, set with `DACL_SECURITY_INFORMATION`) with the DACL `D:P(A;;GA;;;SY)(A;;GA;;;BA)`: SYSTEM and Administrators only. Then `new ECDsaCng(key)`, `CertificateRequest.CreateSelfSigned`, and `X509Store(StoreName.My, StoreLocation.LocalMachine).Add`. The certificate in the store links to the named machine key.
- **Where it lives.** `LocalMachine\My`. A standard user cannot add, change or remove a certificate there, and the key is not exportable. The service writes the SHA-1 thumbprint to the admin-only registry value `HKLM\SOFTWARE\ParentalControl\WebAdminCertThumbprint` (REG_SZ) and finds the certificate by it.
- **Use.** Kestrel gets the certificate through `HttpsConnectionAdapterOptions.ServerCertificateSelector`, which returns the current certificate from a holder. A renewal takes effect at the next connection, without a restart of the host.
- **Renewal.** The service makes the new certificate, writes the new thumbprint, switches the holder, and then removes the old certificate and deletes its key (`CngKey.Delete`). If `WebAdminCertThumbprint` had a value before, it queues one Telegram notice with the SHA-256 fingerprint of the new certificate (D9).
- **Failure.** The 5051 host does not wait for the certificate. It binds port 5051 at once with only a `ServerCertificateSelector` (Kestrel accepts an HTTPS endpoint with a selector and no fixed certificate; the selector runs once per connection). While the holder has no certificate, the selector returns no certificate and the TLS handshake fails, so the socket stays bound and no program of the child can take the port in the gap. The certificate step has its own retry loop: it logs the error and tries again after a wait that grows to at most 60 seconds. Port 5050 and the limits are not affected. A VM check confirms that a handshake without a certificate fails and the host keeps the socket.
- New classes: `WebAdminCertificatePolicy` (Domain, pure: the decision and the list of names), `WebAdminCertificateStore` (Core/Platform: CNG, the store, the registry value).
- *Alternative: the installer makes the certificate with `New-SelfSignedCertificate`.* Rejected: the renewal needs the service anyway, and two code paths make the same certificate.
- *Alternative: a PFX file in the data folder.* Rejected: a move of an Untrusted Data Folder (D14 of `require-admin-sign-in`) would make a new certificate and a new warning, and the key would be a file. The machine key store keeps the key out of the data folder and makes it not exportable.
- *Alternative: RSA 2048.* Possible, but larger and slower. Each current browser supports ECDSA P-256.
- *Alternative: a validity of 10 years.* Rejected: Apple platforms refuse it.

### D7. Cookie, `Origin` and HSTS
Decided by the orchestrator under the user's delegation (2026-11-26).
- The login sets `Secure = ctx.Request.IsHttps` on the cookie. Over HTTPS the browser then never sends the cookie over HTTP, also not to `http://<same IP>:5050`. Over loopback HTTP the cookie has no `Secure` flag, so the parent's sign-in on `http://localhost:5050` keeps working.
- The cookie keeps its name `pc_admin_session`. A cookie does not depend on the port, but the host differs (`localhost` against the LAN IP), and the `Secure` flag keeps the HTTPS cookie off HTTP.
- `AdminRoutePolicy.Decide` gets the scheme as a new parameter. The service gives `ctx.Request.IsHttps ? "https" : "http"`, which comes from the connection. The expected origin is `scheme + "://" + Host`. The service uses no `X-Forwarded-Proto`.
- No `Strict-Transport-Security` header. Browsers ignore HSTS for an IP address. For a name (for example `<COMPUTERNAME>.local`), HSTS makes a certificate error a hard block without the option to continue, and the parent could not open the page at all.
- *Alternative: the cookie prefix `__Host-` over HTTPS.* Rejected: two cookie names for two schemes make the gate more complex, and `Secure` with no `Domain` gives the same protection here.

### D8. Admin app: the LAN address, the QR code, the fingerprint and the network category
Decided by the orchestrator under the user's delegation (2026-11-26).
- The "Web admin sign-in" card in `SettingsView.xaml` gets a section "Open on your phone":
  - `https://<IPv4>:5051/admin` for each adapter with `OperationalStatus.Up`, not loopback, not a tunnel, whose network has the category Private. A QR code of the first address (the same QR code as for TOTP in `SettingsViewModel`).
  - `http://localhost:5050/admin` for the parent's own Windows account on this PC.
  - The SHA-256 fingerprint of the Web Admin Certificate, in groups of two hex digits, and its end date. The app reads `WebAdminCertThumbprint` and the certificate from `LocalMachine\My`.
  - A text: "The first time, your browser warns that the connection is not private. Compare the fingerprint, then continue. If the warning comes back later and this app shows no new fingerprint, do not sign in."
  - A text that a fixed IP address for this PC in the router keeps the address the same.
- The network category comes from the COM Network List Manager: `INetworkListManager.GetNetworkConnections`, then for each `INetworkConnection` `GetAdapterId` and `GetNetwork`, then `INetwork.GetCategory` (`NLM_NETWORK_CATEGORY_PRIVATE`, `_PUBLIC`, `_DOMAIN_AUTHENTICATED`). The app maps the adapter ID to `NetworkInterface.Id`. New class `NetworkCategoryReader` (Core/Platform). The pure class `LanAdminUrls` in Domain makes the list of addresses from (adapter, IPv4 addresses, category).
- If no adapter is Private, the section shows a warning: the phone cannot reach the web admin until the parent sets the network to Private in the Windows settings (Settings → Network → Properties → Private network). The dashboard banner shows the same warning.
- The app does not show `<COMPUTERNAME>.local`: not each phone resolves `.local` names, and an address that sometimes works confuses the parent.
- The texts about a taken port (`DataFolderReport`, SF3) name the port: 5050 or 5051.
- *Alternative: the app sets the network to Private.* Rejected: the category also controls file sharing and network discovery for all programs. That is the parent's decision in Windows, not a side effect of this app.

### D9. A taken port 5051 and the Telegram texts
Decided by the orchestrator under the user's delegation (2026-11-26).
- `OnBindFailedAsync` and `OnBoundAsync` get the port. For 5051 they write and clear the new admin-only registry value `HKLM\SOFTWARE\ParentalControl\WebLanPortBlockedBy` (REG_SZ, the same format as `WebPortBlockedBy`). A second value, because both ports can be taken at the same time and become free at different times.
- The Admin app treats a value in `WebLanPortBlockedBy` like one in `WebPortBlockedBy`: the warning names the port and the program, and "Set up on" does not show.
- The Telegram alert about a taken port names the port (`AdminAccessTexts`, German).
- New German Telegram text for a new certificate (D6): the computer name, the SHA-256 fingerprint, and that the browser will warn one time. It goes through the Offline Notification Queue as a new payload type in `NotificationPayloads`.
- The German 403 text of port 5050 for a LAN source (D1) is new in `AdminAccessTexts`.

### D10. Tailscale, VPN and other networks
Decided by the orchestrator under the user's delegation (2026-11-26).
- This change ends access from outside the local subnet. README, `CONTEXT.md` and ADR 0001 promise Tailscale; the tasks update them.
- Before this change, Tailscale worked only with a firewall rule that the parent added, over plain HTTP. No such rule is known on the child's PC.
- Later path, not in this change: an admin-only setting "Extra networks" (a list of CIDR ranges, for example `100.64.0.0/10` for Tailscale) that the Admin app writes. `LanSourcePolicy` adds the ranges, and the Admin app adds them to `remoteip` of the firewall rule. The remote parent then uses the same HTTPS endpoint.
- *Alternative: allow Tailscale now.* Rejected: the user did not ask for it, and each extra range is an exposure that needs its own decision.

### D11. How this design fits stage 4 (the home server)
Decided by the orchestrator under the user's delegation (2026-11-26).
- The admin page is a single-page app that uses only `/api/admin/*` (ADR 0009). It can move to the home server without a change.
- The home server is the right place for a certificate that the phone trusts (a public certificate for an own domain, or a local CA). Then the warning of D5 goes away for the parent.
- Two ways for the server to reach the PC, to decide in stage 4:
  - The PC connects out to the server (preferred: no open port on the child's PC).
  - The server calls the LAN Admin Endpoint. Then the server is one LAN source: one bucket of the login limit, and D6 still ignores forwarded headers, so the server cannot pass the address of the phone. The server needs its own authentication (for example a pinned certificate of the server), not the TOTP of the parent.
- When the parent uses the server, a setting can turn off the LAN Admin Endpoint, and the uninstall of the firewall rule stays as it is.

### D12. ADR
HTTPS on a second port with a self-signed certificate is hard to reverse: the parent's browsers accept the certificate, and the address of the web admin changes. It gets ADR 0012 (`docs/adr/0012-web-admin-https-on-the-lan.md`). The number 0012 avoids a clash with a parallel change that can take 0011; the orchestrator fixes the numbers at merge. ADR 0012 replaces the Tailscale part of point 2 of ADR 0001.

### D13. The login limit and a device of the child on the LAN
Decided by the orchestrator under the user's delegation (2026-11-26).
- Before this change, no firewall rule existed, so in practice only the source `local` reached the login. Now a device of the child on the LAN reaches it too.
- Such a device can use more than one source (a new IPv4 address that it sets by hand, or many IPv6 addresses of its /64), so it can reach the global limit of 30 failed logins and block the login of the parent's phone for 24 hours. It cannot guess a code in that time (D6 of `require-admin-sign-in`).
- Decision: accept this risk and keep D6 as it is. The parent gets the Telegram alert on blocked logins (existing) and can select "Clear login limit" in the Admin app, or sign in from the own Windows account on this PC.
- *Alternative: group IPv6 sources by /64.* Rejected: it does not help against a new IPv4 address that the device sets by hand, and it changes the spec of the login limit for a small gain.

### New terms
`GLOSSARY.md` gets "LAN Admin Endpoint" and "Web Admin Certificate". `CONTEXT.md` "Remote Admin Interface" changes from "local network or Tailscale" to "this PC and the local subnet".

## Risks / Trade-offs

- [The home network has the category Public. Many PCs choose Public when they join a new Wi-Fi.] → The rule does not apply and the phone cannot connect. The Admin app shows a warning with the steps to set the network to Private (D8).
- [The phone is in a guest Wi-Fi or another VLAN.] → Another subnet: the firewall and D3 refuse it. The Admin app text says that the phone must be in the same network as the PC.
- [A third-party security suite replaces the Windows Firewall.] → The rule of D4 has no effect. D3 still limits the access to the local subnet. The suite can also block port 5051; the parent must allow it there.
- [The parent accepts a certificate warning without a check.] → An active attacker on the LAN can then read the session. Passive capture still gets nothing. The Admin app text asks for the check at the first visit and says that a later warning without a new fingerprint is an attack. The TOTP code works one time only, and "Sign out all web sessions" ends a stolen session.
- [A browser forgets the accepted certificate and warns again.] → An annoyance, not a security gap. The fingerprint stays the same, so the parent can compare it again.
- [The key gets other access rules than planned, for example by a Windows default for machine keys.] → The child could read the key and act as the server. The service sets the DACL itself at the creation (D6), and a VM check opens the key as `Kid`.
- [The IP address of the PC changes.] → The QR code and the bookmark stop working. The certificate stays the same (no IP in the names). The Admin app shows the new address and recommends a fixed address in the router.
- [A new computer name or the end of the certificate makes a new certificate.] → One new warning per browser. The Telegram notice (D9) and the new fingerprint in the Admin app tell the parent to expect it.
- [Two Kestrel hosts use more memory.] → A few MB. The second host serves only the admin routes.
- [A device of the child on the LAN blocks the login of the phone with failed codes.] → Accepted (D13). Telegram alert, "Clear login limit" in the Admin app, sign-in on this PC.
- [Access over Tailscale stops.] → Documented as **BREAKING**, with a later path (D10).

## Migration Plan

1. The installer of the new version adds the firewall rule (D4). The service makes the certificate at its first start (D6). An upgrade through the Built-in Updater does the same, without UI.
2. The parent opens the Admin app, scans the QR code, compares the fingerprint, and accepts the warning one time per browser.
3. A bookmark to `http://<IP>:5050/admin` on another device stops working. If a manual firewall rule for 5050 exists, the page answers 403 with the new address.
4. On test VM 102: delete the manual test rule for port 5050 from the checks of PR #21.

Rollback: an older installer does not know the rule or the certificate. The rule stays, but it opens only port 5051 for the service program, and the older service does not listen on 5051. The certificate stays in `LocalMachine\My`. To remove both by hand: `netsh advfirewall firewall delete rule name="Parental Control Web Admin (HTTPS)"`, and remove the certificate `CN=Parental Control Web Admin (…)` in `certlm.msc`. A later upgrade replaces both.
