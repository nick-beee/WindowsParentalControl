## MODIFIED Requirements

### Requirement: Only the child's processes
The system SHALL suspend only processes that run in a session of the Restricted User whose limit ended. It SHALL NOT suspend processes of another user or session, processes in System, the Session Agent, the Trusted WebView2 Runtime (an exe with the file name `msedgewebview2.exe` below `Microsoft\EdgeWebView\Application\` in a Program Files folder), an exe below the Windows folder (also if no App Rule puts it in System), or a process whose exe path it cannot read. It SHALL suspend each other exe with the file name `msedgewebview2.exe` as any other exe.

#### Scenario: WebView2 host
- **WHEN** Unknown is exhausted and a Study app uses `msedgewebview2.exe` of the Trusted WebView2 Runtime
- **THEN** `msedgewebview2.exe` keeps running

#### Scenario: Game renamed as WebView2 is suspended
- **WHEN** the probe locks are on or off, Unknown is exhausted, no App Rule covers the Downloads folder, and the child plays a game in front that it copied as `C:\Users\Kid\Downloads\wv\msedgewebview2.exe`
- **THEN** the game is suspended within 70 seconds
- **AND** the session is not locked

#### Scenario: Windows rule deleted
- **WHEN** the parent deletes the default rule for the Windows folder and Unknown is exhausted
- **THEN** `explorer.exe` and Task Manager keep running

#### Scenario: Admin session
- **WHEN** an Administrator runs the same game in another session at the same time
- **THEN** the game of the Administrator keeps running

### Requirement: WebView2 counts with the program that started it
A process of the Trusted WebView2 Runtime (a `msedgewebview2.exe` below `Microsoft\EdgeWebView\Application` in a Program Files folder) SHALL count, in each role, as the first process in its chain of parent processes that is not such a file, and that process SHALL be recorded as a Foreground Window exe. If a parent ended or the chain cannot be checked, the entry SHALL count nothing and SHALL be no blind sample and no budget item. Each other `msedgewebview2.exe` (a copy, a Fixed Version runtime, a per-user runtime) SHALL count as itself by its App Rule, like any other exe, and SHALL be recorded as a Foreground Window exe.

#### Scenario: WebView2 in a Study app
- **WHEN** a Study app is the Foreground Window and its `msedgewebview2.exe` control from the WebView2 Runtime has the focus
- **THEN** the time counts in Study only

#### Scenario: WebView2 in the shell
- **WHEN** the probe locks are on and Unknown is exhausted and the child reads "News and interests" of `explorer.exe` for 5 minutes
- **THEN** no Category Usage increases
- **AND** the session is not locked

#### Scenario: WebView2 popup of a Study app
- **WHEN** the probe locks are on and Unknown is exhausted and a Study app opens a WebView2 popup window that stays in front for 3 minutes
- **THEN** the time counts in Study only
- **AND** the session is not locked

#### Scenario: Second instance of a WebView2 app
- **WHEN** the probe locks are on, Unknown is exhausted, a Study app with a WebView2 control runs twice, the child closes the first instance, and the WebView2 control of the second instance has the focus for 3 minutes
- **THEN** the time counts in Study
- **AND** the session is not locked

#### Scenario: WebView2 control moved into Notepad
- **WHEN** a script of the child moves the WebView2 control of a Games app into a window of `notepad.exe`, and the child plays in it
- **THEN** the time counts in Games
- **AND** the Games app is suspended when Games is exhausted

#### Scenario: Game renamed as WebView2
- **WHEN** the probe locks are on or off, Unknown is exhausted, no App Rule covers the Downloads folder, and the child copies a game as `msedgewebview2.exe` into its Downloads folder, puts it into a Study app and plays it
- **THEN** the time counts in Unknown
- **AND** the game is suspended within 70 seconds
- **AND** the session is not locked

#### Scenario: Fixed Version runtime with an App Rule
- **WHEN** a folder rule maps `C:\Program Files\StudyApp\` to Study, and the WebView2 control of that Study app runs its own `msedgewebview2.exe` below that folder and has the focus
- **THEN** the time counts in Study only

#### Scenario: Fixed Version runtime without an App Rule
- **WHEN** Unknown is exhausted, a Study app runs its own `msedgewebview2.exe` below `C:\Program Files\StudyApp\`, no App Rule covers that folder, and its WebView2 control has the focus
- **THEN** the time counts in Unknown
- **AND** that `msedgewebview2.exe` is suspended within 70 seconds
- **AND** the session is not locked
