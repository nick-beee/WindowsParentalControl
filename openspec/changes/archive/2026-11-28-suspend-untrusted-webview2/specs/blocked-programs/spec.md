## MODIFIED Requirements

### Requirement: Programs that are never blocked
The system SHALL never close or suspend because of a block, also if a rule matches: an exe below the Windows folder or the Parental Control install folder, the Trusted WebView2 Runtime (`msedgewebview2.exe` below `Microsoft\EdgeWebView\Application\` in a Program Files folder), a process outside the sessions of Restricted Users, or a process whose exe path it cannot read. Each other `msedgewebview2.exe` SHALL be an ordinary exe for a block.

#### Scenario: WebView2 host
- **WHEN** a folder rule blocks `C:\Program Files (x86)\Microsoft\` and a Study app starts `msedgewebview2.exe` below that folder
- **THEN** `msedgewebview2.exe` keeps running

#### Scenario: Game renamed as WebView2 in a blocked folder
- **WHEN** a folder rule blocks `C:\Users\Kid\Downloads\` and the child starts a game that it copied as `C:\Users\Kid\Downloads\wv\msedgewebview2.exe`
- **THEN** the system closes the process within 5 seconds after its start

#### Scenario: Windows program
- **WHEN** a file rule for `notepad.exe` has the block on and the child runs `C:\Windows\System32\notepad.exe`
- **THEN** Notepad keeps running

#### Scenario: Parent session
- **WHEN** an Administrator starts `chrome.exe` in another session while `chrome.exe` is blocked
- **THEN** the Chrome of the Administrator keeps running
