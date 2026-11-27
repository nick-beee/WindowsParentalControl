# Spec Delta

## Purpose

Lets the Restricted User open the child request page and send a Grace Request from the Session Agent while all browsers are blocked, without a general browser.

## ADDED Requirements

### Requirement: The request button opens the Request Window
The request button of the widget and the request item of the tray menu SHALL open the child request page in the Request Window, a window of the Session Agent with an embedded web view. The Session Agent SHALL NOT start a browser or another program for it. A second click SHALL bring the open Request Window to the front and SHALL NOT open a second one. The child SHALL be able to close the window; the Session Agent keeps running.

#### Scenario: Request with all browsers blocked
- **WHEN** the 15 default browsers are blocked and the child clicks "🎮 Mehr Zeit wünschen"
- **THEN** the Request Window shows the request page of the child
- **AND** no browser process starts and the service records no blocked start

#### Scenario: Grace Request from the window
- **WHEN** the child asks for 15 more minutes with a reason in the Request Window
- **THEN** the service stores the Grace Request and alerts the parent as for a request from a browser

#### Scenario: Second click
- **WHEN** the Request Window is open behind a game and the child clicks the request button again
- **THEN** the same Request Window comes to the front and no second window opens

### Requirement: The Request Window shows only the local request page
The Request Window SHALL load as a document, in the window and in each frame, only `http://127.0.0.1:5050/request` with or without a query. It SHALL cancel each other navigation, also one that a script, a link, a form or a redirect starts. It SHALL load each other resource only from the origin `http://127.0.0.1:5050`.

#### Scenario: Link to the web admin
- **WHEN** the request page shows a link to `/admin` and the child clicks it
- **THEN** the window stays on the request page

#### Scenario: Script navigates away
- **WHEN** a script in the page sets `location` to `https://example.com` or to a `file:`, `data:`, `javascript:`, `about:`, `edge:` or `view-source:` URL
- **THEN** the navigation is cancelled and the window stays on the request page

#### Scenario: Other user in the query
- **WHEN** the page links to `/request?user=Kid2`
- **THEN** the window loads that page

#### Scenario: Resource from another origin
- **WHEN** the page asks for an image from `https://example.com`
- **THEN** the window does not load it

### Requirement: The Request Window opens no other window and no file
The Request Window SHALL NOT open a new window or a popup, SHALL NOT download a file, SHALL NOT start a program for an external protocol, and SHALL NOT navigate to a link, a text or a file that the child drops on it.

#### Scenario: Link in a new window
- **WHEN** the child opens a link of the page with Ctrl+click, with the middle mouse button or through `target="_blank"` or `window.open`
- **THEN** no window opens and the Request Window stays on the request page

#### Scenario: External protocol
- **WHEN** a link of the page points to `mailto:` or `ms-settings:`
- **THEN** no program starts

#### Scenario: Dropped link
- **WHEN** the child drags a link or an `.html` file from Explorer onto the Request Window
- **THEN** the window stays on the request page

### Requirement: The Request Window has no browser tools
The Request Window SHALL have no developer tools, no context menu of the web view, no browser shortcut keys (for example F12, Ctrl+Shift+I, Ctrl+P, Ctrl+F, Ctrl+U), no status bar, no saved passwords and no saved form data. Text input and the keys that a form needs (Tab, Enter, the arrow keys, copy and paste) SHALL still work.

#### Scenario: Developer tools
- **WHEN** the child presses F12 or Ctrl+Shift+I in the Request Window
- **THEN** no developer tools open

#### Scenario: Context menu
- **WHEN** the child right-clicks the request page
- **THEN** no menu with "Back", "Print" or "Inspect" opens

#### Scenario: Typing a reason
- **WHEN** the child types a reason, pastes a word and presses Tab and Enter
- **THEN** the form takes the text and sends the request

### Requirement: Settings of the user do not change the Request Window
The Session Agent SHALL ignore each `WEBVIEW2_*` environment variable of its process when it creates the web view. It SHALL use only the machine-wide Evergreen WebView2 Runtime below the Program Files folders, not a per-user runtime and not a folder that the user names.

#### Scenario: Debug port by a user variable
- **WHEN** the child sets the user variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, signs in again and opens the Request Window
- **THEN** no process listens on port 9222

#### Scenario: Own runtime by a user variable
- **WHEN** the child sets `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` to a folder in its profile and opens the Request Window
- **THEN** the web view runs `msedgewebview2.exe` from the machine-wide runtime folder

### Requirement: The Request Window keeps no site data
The Session Agent SHALL keep the data of the web view in a folder below `%LOCALAPPDATA%\ParentalControl` of the user and SHALL write nothing into the install folder. It SHALL keep no cookies, no storage and no history of a page from one open of the Request Window to the next.

#### Scenario: Data folder
- **WHEN** the child opens the Request Window for the first time
- **THEN** the web view data is below `%LOCALAPPDATA%\ParentalControl` of the child
- **AND** the install folder has no new file

#### Scenario: Storage after a close
- **WHEN** the page stores a value in `localStorage`, the child closes the Request Window and opens it again
- **THEN** the value is gone

### Requirement: Text when the Request Window cannot show the page
If the WebView2 Runtime is missing or the web view fails to start, or the request page does not load, the Request Window SHALL show the text `request_window_unavailable` of the Language Preset and SHALL NOT start a browser. The window title SHALL be the text `request_window_title`. Both texts SHALL follow the Language Preset like each other text of the Session Agent.

#### Scenario: No runtime, German
- **WHEN** the preset is "de", the PC has no WebView2 Runtime and the child clicks the request button
- **THEN** the window shows "Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt."
- **AND** no browser process starts

#### Scenario: Service not reachable
- **WHEN** the web server of the service does not answer and the child opens the Request Window
- **THEN** the window shows the text `request_window_unavailable` of the preset

#### Scenario: Window title in German
- **WHEN** the preset is "de" and the Request Window opens
- **THEN** the window title is "Mehr Zeit wünschen"

### Requirement: The installer provides the WebView2 Runtime
The installer SHALL check for the machine-wide Evergreen WebView2 Runtime. If it is missing, the installer SHALL install it for all users. If that install fails, for example because the PC is offline, the installer SHALL complete and SHALL log the failure.

#### Scenario: Runtime missing and PC online
- **WHEN** the PC has no WebView2 Runtime and the parent runs the installer with a network connection
- **THEN** after the install the runtime is below `%ProgramFiles(x86)%\Microsoft\EdgeWebView\Application`

#### Scenario: Runtime missing and PC offline
- **WHEN** the PC has no WebView2 Runtime and no network connection and the parent runs the installer
- **THEN** the install completes and the request button shows the text `request_window_unavailable`

#### Scenario: Runtime present
- **WHEN** the runtime is present
- **THEN** the installer does not run the runtime setup

### Requirement: The request button is only for a Restricted User
The Session Agent SHALL offer the request button and the request item of the tray menu only while the status of the service says that the user is a Restricted User. Without such a status, it SHALL NOT open the Request Window.

#### Scenario: Administrator
- **WHEN** an Administrator is signed in and the Session Agent runs in that session
- **THEN** the widget is hidden and the tray menu has no request item

#### Scenario: Tray menu of the child
- **WHEN** the child picks "🎮 Mehr Bildschirmzeit wünschen..." in the tray menu
- **THEN** the Request Window opens

### Requirement: Agent Window Budget
In each 60 minutes of unlocked time of a Restricted User, the time in which the Session Agent is the counted process SHALL add no Category Usage and SHALL count toward no lock, for at most 5 minutes. After that, the rules of `category-limits` SHALL apply. The system SHALL NOT start, extend or end the budget because of data that the Session Agent sends.

#### Scenario: Request while Unknown is exhausted
- **WHEN** the Session Agent counts in Unknown, Unknown is exhausted, and the child types a request in the Request Window for 3 minutes
- **THEN** the session is not locked
- **AND** Category Usage for Unknown does not increase

#### Scenario: Budget used up
- **WHEN** the Session Agent counts in Unknown, Unknown is exhausted, and the Session Agent is the counted process for 7 minutes within 60 minutes
- **THEN** the last 2 minutes count under the rules of `category-limits`

#### Scenario: Default rules today
- **WHEN** the default App Rules put the install folder in System and the child keeps the Request Window open for 20 minutes
- **THEN** Category Usage does not increase
