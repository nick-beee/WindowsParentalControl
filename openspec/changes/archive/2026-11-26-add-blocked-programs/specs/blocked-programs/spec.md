# Spec Delta

## Purpose

Stops the programs that the parent blocks, for example web browsers, in the sessions of a Restricted User. The system closes a blocked program only at its start and suspends a blocked program that already runs.

## ADDED Requirements

### Requirement: Block on an App Rule
An App Rule SHALL have a block switch. When the block is on, each exe that the rule resolves SHALL be a Blocked Program. The rule SHALL keep its category and its App Limit. They SHALL have no effect while the block is on, and they SHALL apply again when the parent removes the block.

#### Scenario: Blocked file rule
- **WHEN** a file rule for `chrome.exe` has the block on and the child runs `C:\Program Files\Google\Chrome\Application\chrome.exe`
- **THEN** the exe is a Blocked Program

#### Scenario: No App Limit warning for a blocked program
- **WHEN** the rule for `chrome.exe` has the block on and an App Limit, a suspended Chrome process is in the session of the child, and the App Limit has 5 minutes left
- **THEN** the child gets no warning that Chrome ends

#### Scenario: Block removed
- **WHEN** the parent removes the block from the rule for `chrome.exe`, which has the category Video
- **THEN** `chrome.exe` is not a Blocked Program
- **AND** its time counts in Video

### Requirement: The block follows the rule precedence
The system SHALL decide the block of an exe from the one App Rule that the rule precedence of `app-categories` selects for that exe. A file rule without the block SHALL thus allow an exe below a blocked folder.

#### Scenario: Exception in a blocked folder
- **WHEN** a folder rule blocks `C:\Tools\`, a file rule maps `calc2.exe` to Study without the block, and the child runs `C:\Tools\calc2.exe`
- **THEN** the exe is not a Blocked Program and counts in Study

#### Scenario: Longest folder decides
- **WHEN** a rule blocks `C:\Apps\` and a rule maps `C:\Apps\School\` to Study without the block
- **THEN** `C:\Apps\School\x.exe` is not a Blocked Program
- **AND** `C:\Apps\Other\y.exe` is a Blocked Program

### Requirement: Programs that are never blocked
The system SHALL never close or suspend because of a block: an exe below the Windows folder, an exe below the Parental Control install folder, an exe with the file name `msedgewebview2.exe`, a process outside the sessions of Restricted Users, or a process whose exe path it cannot read. This SHALL be true also if a rule matches the exe.

#### Scenario: WebView2 host
- **WHEN** a folder rule blocks `C:\Program Files (x86)\Microsoft\` and a Study app starts `msedgewebview2.exe` below that folder
- **THEN** `msedgewebview2.exe` keeps running

#### Scenario: Windows program
- **WHEN** a file rule for `notepad.exe` has the block on and the child runs `C:\Windows\System32\notepad.exe`
- **THEN** Notepad keeps running

#### Scenario: Parent session
- **WHEN** an Administrator starts `chrome.exe` in another session while `chrome.exe` is blocked
- **THEN** the Chrome of the Administrator keeps running

### Requirement: Refuse a block that cannot apply
The system SHALL refuse a block on a rule for the System category, on a folder rule for the Windows folder or the install folder or a folder below them, and on a file rule for `msedgewebview2.exe` or for an exe file name of Parental Control. It SHALL keep the rule as it was.

#### Scenario: Block on System
- **WHEN** the parent turns on the block of a rule with the category System
- **THEN** the system refuses the change and the rule has no block

#### Scenario: Block on WebView2
- **WHEN** the parent adds a blocked file rule for `msedgewebview2.exe`
- **THEN** the system refuses the rule

### Requirement: Default blocked browsers
The system SHALL create blocked file rules with the category Unknown for `chrome.exe`, `firefox.exe`, `msedge.exe`, `opera.exe`, `brave.exe`, `vivaldi.exe`, `browser.exe`, `iexplore.exe`, `chromium.exe`, `waterfox.exe`, `librewolf.exe`, `floorp.exe`, `palemoon.exe`, `thorium.exe` and `DuckDuckGo.exe`. It SHALL do this one time per install, on a new and on an existing database.

#### Scenario: New install
- **WHEN** the service starts for the first time with a new database and the child starts Firefox
- **THEN** the rule for `firefox.exe` exists with the block on, and Firefox is closed

#### Scenario: Deleted default stays deleted
- **WHEN** the parent deletes the default rule for `opera.exe` and the service restarts
- **THEN** the rule for `opera.exe` does not exist

#### Scenario: Tor Browser
- **WHEN** the child starts `Tor Browser\Browser\firefox.exe` from the Desktop
- **THEN** the exe is a Blocked Program

### Requirement: Existing rule of a default browser
If the one-time creation of the default blocked rules finds a rule with the same match type and pattern, the system SHALL turn on the block of that rule and SHALL keep its category and App Limit. It SHALL NOT add a second rule.

#### Scenario: Parent mapped Chrome to Video
- **WHEN** an install has a rule that maps `chrome.exe` to Video and gets this version
- **THEN** the rule for `chrome.exe` has the category Video and the block on

### Requirement: Detect process starts in the service
The system SHALL detect each process start in a session of a Restricted User from Windows, without data from the Session Agent. It SHALL read the exe path and the creation time from the process itself. It SHALL also check the processes of these sessions at least every 60 seconds, so that a missed start event does not leave a Blocked Program open.

#### Scenario: Agent not running
- **WHEN** the Session Agent does not run and the child starts `brave.exe`
- **THEN** the system detects the start and acts on it

#### Scenario: Missed start event
- **WHEN** the start event of a `chrome.exe` process does not arrive
- **THEN** the system finds the process within 60 seconds

### Requirement: Close a Blocked Start
The system SHALL close a process of a Blocked Program only if the process started after the block took effect and after the service started, and the system detects it within 10 seconds after its creation. If the parent process is a live Blocked Program process that started before the block took effect, the system SHALL suspend the new process, not close it. This is the only close of a program of a Restricted User.

#### Scenario: Child starts a browser
- **WHEN** `chrome.exe` is blocked and the child starts Chrome
- **THEN** the system closes the Chrome process within 2 seconds after its start

#### Scenario: Browser started before the block
- **WHEN** Chrome runs and then the parent turns on the block for `chrome.exe`
- **THEN** the system does not close Chrome

#### Scenario: New tab in a browser that ran before the block
- **WHEN** Firefox ran before the parent turned on the block for `firefox.exe`, and the child opens a new tab before the system suspends Firefox
- **THEN** the system suspends the new Firefox process and does not close it

### Requirement: Close a Blocked Start without a Pending Suspension
The system SHALL close a Blocked Start without a Pending Suspension. It SHALL close a Blocked Start also during a Session End Hold of its session.

#### Scenario: Browser start during a cancelled sign-out
- **WHEN** a Session End Hold of the session of the child is active and the child starts Chrome, which is blocked
- **THEN** the system closes the Chrome process within 2 seconds after its start

### Requirement: Suspend a Blocked Program that already runs
The system SHALL suspend, within 70 seconds, a process of a Blocked Program that it does not close at its start. The 70 seconds include the 10 seconds of the Pending Suspension of `category-limits`. The requirement "Session End Hold" of `category-limits` adds an exception. This includes a process that ran before the block took effect or before the service started, and a process that it detects more than 10 seconds after its creation. It SHALL NOT close or kill such a process.

#### Scenario: Block turned on while the browser runs
- **WHEN** Chrome runs and the parent turns on the block for `chrome.exe`
- **THEN** each Chrome process of the child is suspended within 70 seconds and still exists

#### Scenario: Service restart while the browser runs
- **WHEN** the service restarts while Firefox runs in the session of the child and `firefox.exe` is blocked
- **THEN** Firefox is suspended and still exists

#### Scenario: Late detection
- **WHEN** the start event of a `brave.exe` process does not arrive and the 60-second check finds it 40 seconds after its start
- **THEN** the process is suspended, not closed

#### Scenario: Sign-out while a blocked browser is suspended
- **WHEN** a blocked Chrome is suspended and the child signs out
- **THEN** the system resumes Chrome for the end of the session and does not suspend it during the Session End Hold
- **AND** the sign-out completes

### Requirement: A blocked suspension lasts while the block applies
A process that the system suspended because of a block SHALL stay suspended while its exe is a Blocked Program, also if its category has time left. The system SHALL resume it when the exe is no longer a Blocked Program and has time left under its limit, when the session ends, and when the service stops.

#### Scenario: Category with time left
- **WHEN** a suspended blocked Chrome is in Unknown and Unknown has time left
- **THEN** Chrome stays suspended

#### Scenario: Parent removes the block
- **WHEN** the parent removes the block for `chrome.exe` and Unknown has time left
- **THEN** the system resumes the suspended Chrome processes within 60 seconds

#### Scenario: Child resumes the browser with a tool
- **WHEN** the child resumes a suspended blocked Chrome with a tool
- **THEN** the system suspends Chrome again within 70 seconds

#### Scenario: Service stop
- **WHEN** the service stops while a blocked Chrome is suspended
- **THEN** the system resumes Chrome before it exits

### Requirement: Fallback when a Blocked Program cannot be stopped
If the system cannot close a Blocked Start, it SHALL suspend the process after its Pending Suspension. If it cannot suspend a process that it must suspend because of a block, it SHALL lock the session with the Enforced Workstation Lock and SHALL notify the parent through the Alert Channel, at most one time per exe and day.

#### Scenario: Close refused
- **WHEN** Windows refuses to close a Blocked Start of `opera.exe` and permits the suspension
- **THEN** the Opera process is suspended and still exists

#### Scenario: Close and suspension refused
- **WHEN** Windows refuses both the close and the suspension of a Blocked Start
- **THEN** the session is locked
- **AND** the parent gets one alert that names the exe

### Requirement: Silent close of a background start
When the command line of a Blocked Program process contains `--no-startup-window` or `--type=`, the system SHALL close or suspend the process without a message to the child and without a Telegram notice. It SHALL still record the blocked start or the suspension for the parent.

#### Scenario: Edge Startup Boost at sign-in
- **WHEN** `msedge.exe` is blocked and Windows starts `msedge.exe --no-startup-window --win-session-start` at the sign-in of the child
- **THEN** the system closes the process
- **AND** the child gets no message

#### Scenario: Edge from a search result
- **WHEN** `msedge.exe` is blocked and the child opens a web result from the Windows search
- **THEN** the system closes the Edge process
- **AND** the child gets the blocked program message

### Requirement: Tell the child about a blocked program
When the system closes or suspends a process of a Blocked Program, and the action is not silent, it SHALL send one session message per program, Restricted User and day. The message SHALL name the program by its exe file name without `.exe` and SHALL say that the parent blocked it. The text SHALL follow the Language Preset of `child-messages`, and the parent SHALL be able to edit it.

#### Scenario: German message
- **WHEN** the preset is "de" and the system closes a Blocked Start of `chrome.exe` for the first time today
- **THEN** the child gets the message "chrome ist auf diesem Computer gesperrt. Frag deine Eltern, wenn du das Programm brauchst."

#### Scenario: Second start on the same day
- **WHEN** the child starts Chrome again on the same day and the system closes it again
- **THEN** the child gets no second message

### Requirement: Report blocked programs to the parent
The system SHALL record per Restricted User, program and day the number of blocked starts and the times of the first and the last one, also after a service restart. It SHALL write one event log entry per program, user and day. Records SHALL be deleted after the usage retention period.

#### Scenario: Three starts of Chrome
- **WHEN** the child starts Chrome three times today and the system closes each start
- **THEN** the record of today for `chrome.exe` has the count 3
- **AND** the event log has one entry for `chrome.exe` today

### Requirement: Optional Telegram notice for a blocked program
When Telegram is set up and the notice setting is on, the system SHALL send the parent one Telegram notice per program, Restricted User and day at the first blocked start that is not silent. The setting SHALL be on by default. The notice SHALL be in German and SHALL escape the exe name and path.

#### Scenario: First blocked start
- **WHEN** the setting is on and the system closes the first Blocked Start of `firefox.exe` today
- **THEN** the parent gets one Telegram notice that names `firefox.exe`

#### Scenario: Setting off
- **WHEN** the parent turns the setting off and the system closes a Blocked Start
- **THEN** the parent gets no Telegram notice
- **AND** the event log still has the entry

### Requirement: Parent manages blocks through the admin API
The admin API SHALL let a signed-in admin read and set the block of each App Rule, read the blocked starts of a day, and read and set the Telegram notice setting. It SHALL mark each file rule of a default blocked browser. After each change of a block, the system SHALL close each new Blocked Start, and SHALL suspend each running process of a new Blocked Program as the requirement "Suspend a Blocked Program that already runs" states. A request without a valid Admin Session SHALL be refused.

#### Scenario: Block a new program
- **WHEN** the parent adds a file rule for `game2.exe` with the block on and the child then starts `game2.exe`
- **THEN** the system closes the process

#### Scenario: Default browser marked
- **WHEN** the parent opens the App Rules and the rules for `chrome.exe` and `Minecraft.exe` exist
- **THEN** the rule for `chrome.exe` is marked as a default browser
- **AND** the rule for `Minecraft.exe` is not marked

#### Scenario: Not signed in
- **WHEN** a request without a valid Admin Session turns off the block of the rule for `chrome.exe`
- **THEN** the system refuses the request and the block stays on

### Requirement: Process data for a block is untrusted
The system SHALL treat the exe path, the file name and the command line of a process as untrusted text. It SHALL use them only to match rules, to choose a silent close, and to show them escaped to the parent. The command line SHALL NOT change whether a Blocked Program is closed or suspended.

#### Scenario: Command line with the silent flag
- **WHEN** the child starts `chrome.exe --no-startup-window` by hand
- **THEN** the system closes the process as a Blocked Start, and only the message is left out
