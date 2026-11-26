# Tasks

## 1. Pure policy classes (Domain, tested on the Mac)

- [ ] 1.1 Add `LanSourcePolicy` in `src/ParentalControl.Domain/Web/` (D3): allow `local`, an address in the on-link prefix of an interface of the same family, and `fe80::/10`; refuse each other address and a null address. Verify with xUnit tests in `tests/ParentalControl.Domain.Tests` for IPv4, IPv6, IPv4-mapped IPv6, a scope ID, a /32 and a /128 interface, and `100.101.102.103` against `192.168.1.63/24` (refused).
- [ ] 1.2 Add `LanEndpointRoutes` (D1): allow `/admin`, `/admin/`, `/admin/assets/{file}` and the paths below `/api/admin/`; refuse `/`, `/request`, `/api/request`, `/api/agent/*` and the path variants of the spec (`//api/admin/…`, other letter case). Verify with xUnit tests.
- [ ] 1.3 Add `WebAdminCertificatePolicy` (D6): the list of names for a computer name, and the decision "new certificate necessary" (none, not yet valid, expired, 30 days or less left, other names, other key algorithm). Verify with xUnit tests that use a `TimeProvider`, also for the limit of exactly 30 days and for a validity of 825 days.
- [ ] 1.4 Add `LanAdminUrls` (D8): from (adapter ID, IPv4 addresses, category, up, type) make the list of `https://<IPv4>:5051/admin`, only for Private adapters that are up and not loopback or tunnel. Verify with xUnit tests, also the case with no Private adapter (empty list).
- [ ] 1.5 Change `AdminRoutePolicy.Decide` to take the scheme (D7) and compare `Origin` with `scheme + "://" + Host`. Verify with xUnit tests: `https` origin on HTTPS passes, `http` origin on HTTPS is refused, the old `http` case still passes.
- [ ] 1.6 Add the German texts to `AdminAccessTexts` (D9): the 403 text of port 5050 with the HTTPS address, the Telegram notice of a new certificate with the fingerprint, and the port in the alert about a taken port. Verify with xUnit tests that each text contains the address, the fingerprint or the port.

## 2. Certificate and network platform code (Core)

- [ ] 2.1 Add `WebAdminCertificateStore` in `src/ParentalControl.Core/Platform/` (D6): `CngKey.Create` (machine key, not exportable, `Security Descr` DACL `D:P(A;;GA;;;SY)(A;;GA;;;BA)`), `CertificateRequest.CreateSelfSigned` with the names, EKU server authentication and 825 days, `X509Store` `LocalMachine\My`, the registry value `WebAdminCertThumbprint`, and the removal of the old certificate and `CngKey.Delete` at a renewal. Verify: `dotnet build -p:EnableWindowsTargeting=true` passes, and the VM checks 5.2 and 5.3.
- [ ] 2.2 Add `NetworkCategoryReader` in `src/ParentalControl.Core/Platform/` (D8): `INetworkListManager.GetNetworkConnections`, `INetworkConnection.GetAdapterId`, `INetworkConnection.GetNetwork`, `INetwork.GetCategory`, mapped to `NetworkInterface.Id`. Verify: the build passes, and the VM check 5.7.
- [ ] 2.3 Add a reader of the interface prefixes for `LanSourcePolicy` (`NetworkInterface.GetAllNetworkInterfaces()`, `UnicastIPAddressInformation.PrefixLength`, cache of 60 seconds, a new read before a refusal). Verify with a test in `tests/ParentalControl.Tests` on the VM (`scripts/vm-test.sh` prints `ALL ASSERTIONS PASSED`) that the VM's own subnet is in the list.

## 3. Web server (Service)

- [ ] 3.1 Split `WebServerHost` into two Kestrel hosts with their own `RunWithRetryAsync` (D2): 5050 as today, 5051 with `CreateExclusiveListenSocket` on `[::]` and `UseHttps` with only a `ServerCertificateSelector` from a certificate holder, so the host binds also without a certificate (D6). The start worker waits for the first bind try of both, at most 5 seconds together. Verify: the build passes, the tests in `tests/ParentalControl.Tests` pass on the VM, and the VM checks 5.4 and 5.9.
- [ ] 3.2 Add the source middleware before the 503 gate (D1, D3): on 5050 only `local`, else 403 with the German text; on 5051 `LanSourcePolicy`, else 403; on 5051 `LanEndpointRoutes`, else 404. Verify with the VM checks 5.5 and 5.6.
- [ ] 3.3 Set `Secure = ctx.Request.IsHttps` on the cookie and pass the scheme of the connection to `AdminRoutePolicy.Decide` (D7). Send no `Strict-Transport-Security`. Verify with the VM check 5.4 (the cookie flags in the browser tools of the laptop, no HSTS header).
- [ ] 3.4 Make or load the Web Admin Certificate in its own retry loop (a wait that grows to at most 60 seconds) at the start and once a day; switch the holder at a renewal; queue the Telegram notice when `WebAdminCertThumbprint` had an older value (D6, D9). Add the payload type to `NotificationPayloads` and its handler in `TelegramBotService`. Verify with the VM checks 5.3 and 5.10.
- [ ] 3.5 Give `OnBindFailedAsync` and `OnBoundAsync` the port; write and clear `WebLanPortBlockedBy` for 5051; name the port in the log and the Telegram alert (D9). Verify with the VM check 5.9.

## 4. Admin app and installer

- [ ] 4.1 Add the section "Open on your phone" to the "Web admin sign-in" card in `SettingsView.xaml` and `SettingsViewModel` (D8): the HTTPS addresses, the QR code of the first one, `http://localhost:5050/admin`, the SHA-256 fingerprint and the end date, the text about the warning, the text about a fixed IP address, and the Public network warning. Show the Public network warning also in the dashboard banner. Verify with the VM checks 5.2 and 5.7.
- [ ] 4.2 Treat `WebLanPortBlockedBy` like `WebPortBlockedBy` in `DataFolderReport`, `ServiceState` and `DashboardViewModel`, and name the port in the texts. Update the text in `SettingsView.xaml` that names `http://localhost:5050/admin`. Verify with xUnit tests of `DataFolderReport` and the VM check 5.9.
- [ ] 4.3 In `installer/ParentalControl.iss`, add the firewall rule in `CurStepChanged(ssPostInstall)` before `StartService()` (`netsh advfirewall firewall delete rule` then `add rule`, D4), and delete it in `CurUninstallStepChanged(usUninstall)`. Verify with the VM check 5.8.

## 5. Checks on test VM 102

Roll back to the snapshot `test-base` first. Delete the manual test rule for port 5050 from the checks of PR #21 if it exists. Use the Mac or a phone in the subnet `192.168.1.0/24` as the LAN device.

- [ ] 5.1 Install with the installer. Expected: `netsh advfirewall firewall show rule name="Parental Control Web Admin (HTTPS)" verbose` shows exactly one rule: inbound, allow, TCP 5051, profile Private, remote IP LocalSubnet, the program `C:\Program Files\…\service\ParentalControl.Service.exe`. No rule opens 5050.
- [ ] 5.2 Open the Admin app. Expected: the section shows `https://192.168.1.63:5051/admin`, a QR code, `http://localhost:5050/admin` and a SHA-256 fingerprint. `certlm.msc` shows `CN=Parental Control Web Admin (<name>)` in Personal with a validity of 825 days.
- [ ] 5.3 Key access and renewal. As `Kid`, run `certutil -store My <thumbprint>` and try to use the key (for example `certutil -repairstore` or a PowerShell `Get-Item Cert:\LocalMachine\My\<thumbprint>` and its private key). Expected: access denied for the key. Restart the service. Expected: the same fingerprint. Set the clock 800 days ahead (or replace the certificate with one that expires in 10 days) and restart the service. Expected: a new certificate, the old one and its key are gone, one Telegram notice with the new fingerprint (if Telegram is configured).
- [ ] 5.4 From the Mac, open `https://192.168.1.63:5051/admin`, accept the warning, compare the fingerprint with the Admin app, and sign in with a valid code. Expected: the admin page works; the cookie `pc_admin_session` has `Secure`, `HttpOnly`, `SameSite=Strict`; no `Strict-Transport-Security` header. `curl -k https://192.168.1.63:5051/request` gives 404.
- [ ] 5.5 From the Mac, `curl -v http://192.168.1.63:5050/admin`. Expected: the connection times out (firewall). Then turn the firewall off for the Private profile and repeat. Expected: 403 with the German text and the HTTPS address; `POST /api/admin/login` gives 403 and adds no row to `admin_login_failures`. Turn the firewall on again.
- [ ] 5.6 As `Kid` in the Proxmox console (not RDP), open `https://192.168.1.63:5051/admin` in Edge and enter a valid code. Expected: 403 with the German text, no Admin Session. The widget link `http://localhost:5050/request` still works, and the Session Agent samples still arrive (the Category Usage grows).
- [ ] 5.7 Set the network of the VM to Public (`Set-NetConnectionProfile -NetworkCategory Public`). Expected: the Mac cannot connect to port 5051, and the Admin app shows the Public network warning in the card and the banner. Set it back to Private.
- [ ] 5.8 Run the installer again two times, one time silent (`/VERYSILENT`). Expected: exactly one rule with the name. Uninstall. Expected: `netsh advfirewall firewall show rule name="Parental Control Web Admin (HTTPS)"` finds no rule.
- [ ] 5.9 Stop the service (`sc stop ParentalControl.Service`; it holds 5051 exclusively while it runs). As `Kid`, start a listener on port 5051 (for example `powershell -Command "$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any,5051);$l.Start();Start-Sleep 600"`). Then start the service. Expected: the limits work, port 5050 works (agent samples, `/request`, the admin page on localhost), the log and the Admin app name port 5051 and the program, the Mac cannot reach the listener of `Kid` on 5051. Stop the listener. Expected: within 60 seconds the LAN Admin Endpoint answers and the warning goes away.
- [ ] 5.10 Certificate failure: make the certificate step fail (for example a test switch that throws in `WebAdminCertificateStore`). Expected: `netstat -ano | findstr :5051` shows the service as the listener; `curl -kv https://192.168.1.63:5051/admin` from the Mac fails in the TLS handshake; as `Kid`, a bind on 5051 fails; port 5050 and the limits work; after the fault is gone, within 60 seconds the handshake works.
- [ ] 5.11 Real phones: open the address from the QR code on an iPhone (Safari) and on an Android phone (Chrome) in the same Wi-Fi. Expected: each browser shows a warning with a way to continue with this certificate (DNS names only, an IP address in the URL, ECDSA P-256, 825 days); after the parent continues, the sign-in with a valid code works and the dashboard loads. Record the steps that each browser needs and how long it remembers the choice, and put them in the README section of 6.2.

## 6. Docs

- [ ] 6.1 Add "LAN Admin Endpoint" and "Web Admin Certificate" to `GLOSSARY.md`, and change "Remote Admin Interface" in `CONTEXT.md` to "this PC and the local subnet". Verify that the terms match `specs/admin-network/spec.md`.
- [ ] 6.2 Update README line 30 (the address and Tailscale) and the diagram label, and add a short "Open the web admin on your phone" section with the warning and the fingerprint check. Verify that README names no `0.0.0.0:5050` and no Tailscale access.
- [ ] 6.3 Update `docs/vm.md` with the firewall rule, the certificate and how to open the LAN Admin Endpoint from the Mac. Verify that check 5.4 works with the steps from the file.

## Workflow follow-up

- Run `/opsx:verify` with the results of the VM checks of group 5.
- Archive the change with `/opsx:archive` after the PR is merged.
