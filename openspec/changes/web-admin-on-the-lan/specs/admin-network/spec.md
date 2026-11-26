# Spec Delta

## Purpose

Controls which networks reach the Remote Admin Interface and over which transport: the loopback HTTP port, the LAN Admin Endpoint over HTTPS, the Web Admin Certificate, the firewall rule and the address that the Admin app shows to the parent.

## ADDED Requirements

### Requirement: Port 5050 answers only this PC
The system SHALL answer a request on port 5050 only if its source is this PC: a loopback address or an address of this PC. It SHALL answer each other request with status 403 and change no data. The 403 page SHALL name the HTTPS address of the LAN Admin Endpoint. The system SHALL take the source from the TCP connection only, never from a header such as `X-Forwarded-For`.

#### Scenario: Phone on the HTTP port
- **WHEN** a phone on the LAN sends `POST /api/admin/login` to `http://<LAN IP>:5050`, for example while the Windows Firewall is off
- **THEN** the system answers 403, does not check the code, and counts no failed login

#### Scenario: Session Agent on loopback
- **WHEN** the Session Agent sends `POST /api/agent/activity` to `http://127.0.0.1:5050`
- **THEN** the system accepts the sample as before

#### Scenario: Parent in the own Windows session
- **WHEN** the parent opens `http://localhost:5050/admin` in the parent's own Windows session on this PC
- **THEN** the system serves the admin page and accepts a valid code

#### Scenario: Browser on this PC with the LAN address
- **WHEN** a browser on this PC opens `http://<LAN IP of this PC>:5050/request`
- **THEN** the system treats the source as this PC and serves the page

#### Scenario: Forwarded header
- **WHEN** a device on the LAN sends a request to port 5050 with the header `X-Forwarded-For: 127.0.0.1`
- **THEN** the system answers 403

### Requirement: LAN Admin Endpoint over HTTPS
The system SHALL serve the Remote Admin Interface over HTTPS on TCP port 5051 on all interfaces, with the Web Admin Certificate. On this port it SHALL serve only `/admin`, the files below `/admin/assets/` and the routes below `/api/admin/`. It SHALL answer each other path with status 404. It SHALL not accept plain HTTP on this port. It SHALL not send a `Strict-Transport-Security` header.

#### Scenario: Sign-in from the phone
- **WHEN** the parent opens `https://<LAN IP>:5051/admin` on a phone in the local subnet, accepts the certificate, and enters a valid code
- **THEN** the system issues an Admin Session and the admin page works as on port 5050

#### Scenario: Child route on the HTTPS port
- **WHEN** a device sends `POST /api/agent/activity` or `GET /request` to port 5051
- **THEN** the system answers 404 and changes no data

#### Scenario: Plain HTTP on the HTTPS port
- **WHEN** a client sends a plain HTTP request to port 5051
- **THEN** the system does not serve a page and does not read a code or a cookie from the request

#### Scenario: No HSTS
- **WHEN** the system answers a request on port 5051
- **THEN** the answer has no `Strict-Transport-Security` header

### Requirement: Only the local subnet reaches the LAN Admin Endpoint
The system SHALL answer a request on port 5051 only if its source is this PC or an address in the on-link subnet of an active network interface of this PC. An IPv6 link-local source is in the subnet. It SHALL answer each other request with status 403 and change no data. It SHALL take the source from the TCP connection only, never from a header.

#### Scenario: Phone in the same subnet
- **WHEN** a phone with the address `192.168.1.40` sends a request to port 5051 and the PC has the address `192.168.1.63/24`
- **THEN** the system serves the request

#### Scenario: Source in another network
- **WHEN** a request to port 5051 comes from `100.101.102.103` (for example a Tailscale peer after the parent added an own firewall rule) and no interface of the PC has that subnet
- **THEN** the system answers 403, does not check a code, and counts no failed login

#### Scenario: Subnet changes
- **WHEN** the PC moves to another network and gets the address `10.0.0.5/24`
- **THEN** within 60 seconds the system serves a source in `10.0.0.0/24`, and it refuses a source in `192.168.1.0/24` if no interface has that subnet any more

### Requirement: Secure admin cookie over HTTPS
The system SHALL set the `Secure` flag on the Admin Session cookie when it issues the cookie over HTTPS. Over loopback HTTP it SHALL issue the cookie without the flag. The cookie SHALL stay `HttpOnly` and `SameSite=Strict` and SHALL have no `Expires`.

#### Scenario: Sign-in over HTTPS
- **WHEN** the parent signs in on `https://<LAN IP>:5051/admin`
- **THEN** the cookie has the flags `Secure`, `HttpOnly` and `SameSite=Strict`
- **AND** the browser does not send the cookie to `http://<LAN IP>:5050`

#### Scenario: Sign-in over loopback HTTP
- **WHEN** the parent signs in on `http://localhost:5050/admin` in the parent's own Windows session
- **THEN** the cookie has the flags `HttpOnly` and `SameSite=Strict`, and the next admin request works

### Requirement: Web Admin Certificate
The service SHALL make a self-signed certificate for the LAN Admin Endpoint if none exists, if it expires in 30 days or less, if it is not valid, or if the computer name changed. Otherwise it SHALL keep the same certificate, so its fingerprint stays the same. The certificate SHALL be valid for at most 825 days. Its private key SHALL not be exportable, and only SYSTEM and the Administrators group SHALL have access to it.

#### Scenario: First start
- **WHEN** the service starts and no Web Admin Certificate exists
- **THEN** the service makes one and the LAN Admin Endpoint uses it

#### Scenario: Restart and new IP address
- **WHEN** the service restarts, or the PC gets a new IP address from DHCP
- **THEN** the LAN Admin Endpoint uses the same certificate with the same fingerprint

#### Scenario: Certificate near its end
- **WHEN** the service starts or checks once a day, and the certificate expires in 30 days or less
- **THEN** the service makes a new certificate, uses it, and removes the old certificate and its key

#### Scenario: Child reads the key
- **WHEN** a process of the Restricted User tries to open the private key of the Web Admin Certificate
- **THEN** Windows refuses the access

#### Scenario: Certificate fails
- **WHEN** the service cannot make or load the certificate
- **THEN** the service enforces the limits and port 5050 works
- **AND** port 5051 stays bound, so no other program can bind it, but it completes no TLS handshake
- **AND** the service logs the error and tries again after a wait that grows to at most 60 seconds

### Requirement: Notice of a new Web Admin Certificate
When the service makes a new Web Admin Certificate after the first one, it SHALL send one Telegram notice with the SHA-256 fingerprint of the new certificate, if Telegram is configured. The notice SHALL use the Offline Notification Queue.

#### Scenario: Renewal
- **WHEN** the service replaces the Web Admin Certificate because it expires soon
- **THEN** the parent gets one Telegram notice with the new fingerprint

#### Scenario: First certificate
- **WHEN** the service makes the first Web Admin Certificate of an install
- **THEN** the service sends no notice

### Requirement: Firewall rule for the LAN Admin Endpoint
The installer SHALL add one inbound allow rule to the Windows Firewall for TCP port 5051, only for the Private profile, only for the remote addresses LocalSubnet, and only for the program of the service. It SHALL replace the rule at each install and upgrade, also a silent one, so that exactly one such rule exists. The uninstaller SHALL remove it. The installer SHALL not open port 5050 and SHALL not add a rule for the Public or the Domain profile.

#### Scenario: Fresh install
- **WHEN** the installer completes on a PC with a Private network
- **THEN** a phone in the local subnet can open `https://<LAN IP>:5051/admin`
- **AND** a phone cannot connect to `http://<LAN IP>:5050`

#### Scenario: Silent upgrade
- **WHEN** the Built-in Updater installs a new version two times
- **THEN** exactly one rule with the name of the LAN Admin Endpoint exists

#### Scenario: Uninstall
- **WHEN** the parent uninstalls Parental Control
- **THEN** no firewall rule of Parental Control exists

#### Scenario: Public network
- **WHEN** the network of the PC has the category Public
- **THEN** the firewall blocks a connection from the LAN to port 5051

#### Scenario: Program of the child on the port
- **WHEN** a program of the Restricted User listens on port 5051 because the service could not bind it
- **THEN** the firewall rule does not let a connection from the LAN reach that program

### Requirement: Admin app shows the LAN address
The Admin app SHALL show the address `https://<IPv4>:5051/admin` for each active network adapter of the PC in a Private network, a QR code of the first address, the loopback address `http://localhost:5050/admin`, and the SHA-256 fingerprint of the Web Admin Certificate. It SHALL show a warning when no adapter is in a Private network. It SHALL not change the network category.

#### Scenario: Private network
- **WHEN** the PC has the address `192.168.1.63` in a Private network and the service runs
- **THEN** the Admin app shows `https://192.168.1.63:5051/admin`, its QR code and the fingerprint

#### Scenario: Public network
- **WHEN** the only network of the PC has the category Public
- **THEN** the Admin app shows no LAN address and a warning that the phone cannot reach the web admin until the parent sets the network to Private in the Windows settings

#### Scenario: No certificate yet
- **WHEN** the service did not make the Web Admin Certificate yet or does not run
- **THEN** the Admin app shows no fingerprint and tells the parent that the service must run
