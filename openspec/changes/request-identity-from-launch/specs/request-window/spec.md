# Spec Delta

## ADDED Requirements

### Requirement: The request routes take the user only from the Request Token
The service SHALL take the user and the session of `GET /request` and `POST /api/request` only from a valid Request Token in the cookie `pc_request_token`: the user SID and the session of the launch record of the Session Agent that got the token. It SHALL ignore a user name in the query, in the body or in any other part of the request. It SHALL accept a token only for a Restricted User.

#### Scenario: Request in the name of another user
- **WHEN** a process of the child sends `POST /api/request` with the Request Token of the child and the `username` of another Restricted User
- **THEN** the service stores the Grace Request for the child, not for the other user

#### Scenario: Token of an Administrator
- **WHEN** the Session Agent of an Administrator asks for a Request Token
- **THEN** the service gives no token

### Requirement: The request routes refuse a request without a valid Request Token
Without a valid Request Token, `GET /request` SHALL answer 401 with the text `page_no_access` of the Language Preset and SHALL show no data and no name of a user. `POST /api/request` SHALL answer 401 with the text `api_no_access` of the Language Preset and SHALL store nothing.

#### Scenario: Request without a token
- **WHEN** a process of the child sends `POST /api/request` without a Request Token, with the `username` of another Restricted User
- **THEN** the service answers 401 and stores no Grace Request
- **AND** the parent gets no alert

#### Scenario: Page without a token
- **WHEN** a browser opens `/request?user=Kid2` without a Request Token
- **THEN** the service answers 401 with the text `page_no_access`
- **AND** the answer has no name of a Restricted User, no remaining time and no schedule

#### Scenario: German text without a token
- **WHEN** the preset is "de" and a browser opens `/request` without a Request Token
- **THEN** the page shows "Diese Seite geht nur über den Knopf „Mehr Zeit wünschen“ von Parental Control."

### Requirement: A Request Token ends
A Request Token SHALL be valid only for 60 minutes after its issue, only while the Session Agent of its launch record runs, and only until the same agent gets a new token. A restart of the service SHALL end each token.

#### Scenario: Token after a restart of the agent
- **WHEN** the child ends the Session Agent, the service starts a new agent, and a process sends `POST /api/request` with the token of the old agent
- **THEN** the service answers 401 and stores no Grace Request

#### Scenario: Expired token
- **WHEN** the Request Window got its token 61 minutes ago and the child sends a request
- **THEN** the service answers 401 and the page shows the text `api_no_access` of the preset

## MODIFIED Requirements

### Requirement: The request button opens the Request Window
The request button of the widget and the request item of the tray menu SHALL open the child request page in the Request Window, a window of the Session Agent with an embedded web view. The Session Agent SHALL NOT start a browser or another program for it. A second click SHALL bring the open Request Window to the front and SHALL NOT open a second one. The child SHALL be able to close the window; the Session Agent keeps running. Each open of the Request Window SHALL get a new Request Token from the service through the Agent Channel, and the page SHALL send it with each request to the service.

#### Scenario: Request with all browsers blocked
- **WHEN** the 15 default browsers are blocked and the child clicks "🎮 Mehr Zeit wünschen"
- **THEN** the Request Window shows the request page of the child
- **AND** no browser process starts and the service records no blocked start

#### Scenario: Grace Request from the window
- **WHEN** the child asks for 15 more minutes with a reason in the Request Window
- **THEN** the service stores the Grace Request for the user of the launch record of the Session Agent and alerts the parent as for a request from a browser

#### Scenario: Second click
- **WHEN** the Request Window is open behind a game and the child clicks the request button again
- **THEN** the same Request Window comes to the front and no second window opens

### Requirement: The Request Window shows only the local request page
The Request Window SHALL load as a document, in the window and in each frame, only `http://127.0.0.1:5050/request` with or without a query. It SHALL cancel each other navigation, also one that a script, a link, a form or a redirect starts. It SHALL load each other resource only from the origin `http://127.0.0.1:5050`. Its first page SHALL be `http://127.0.0.1:5050/request` without a query.

#### Scenario: Link to the web admin
- **WHEN** the request page shows a link to `/admin` and the child clicks it
- **THEN** the window stays on the request page

#### Scenario: Script navigates away
- **WHEN** a script in the page sets `location` to `https://example.com` or to a `file:`, `data:`, `javascript:`, `about:`, `edge:` or `view-source:` URL
- **THEN** the navigation is cancelled and the window stays on the request page

#### Scenario: Other user in the query
- **WHEN** a script in the page navigates to `/request?user=Kid2`
- **THEN** the window loads that page
- **AND** the page shows the data of the child, not of `Kid2`

#### Scenario: Resource from another origin
- **WHEN** the page asks for an image from `https://example.com`, also from inside a frame
- **THEN** the window does not load it

### Requirement: The lockdown applies before the first navigation
The Session Agent SHALL start the first navigation of the Request Window only after it has applied each setting, each event handler and the resource filter of the web view, and after it has set the Request Token as an HttpOnly cookie for `127.0.0.1`. It SHALL NOT give the web view a URL before that, also not in XAML. If any step of this setup throws, the Request Window SHALL show the text `request_window_unavailable`, SHALL NOT navigate, and SHALL close the web view.

#### Scenario: Event that the runtime does not have
- **WHEN** the installed WebView2 Runtime lacks an event that the Session Agent subscribes to, and the subscription throws
- **THEN** the window shows the text `request_window_unavailable` of the preset
- **AND** the web view loads no page

### Requirement: Text when the Request Window cannot show the page
If the WebView2 Runtime is missing, the setup of the web view fails, the service gives no Request Token, the first navigation of the window fails for any reason, a later navigation that the window did not cancel fails, or the browser process fails, the Request Window SHALL show the text `request_window_unavailable` of the Language Preset and SHALL NOT start a browser. The window title SHALL be the text `request_window_title`. Both texts SHALL follow the Language Preset.

#### Scenario: No runtime, German
- **WHEN** the preset is "de", the PC has no WebView2 Runtime and the child clicks the request button
- **THEN** the window shows "Das Anfragefenster geht gerade nicht. Frag deine Eltern direkt."
- **AND** no browser process starts

#### Scenario: Service not reachable
- **WHEN** the web server of the service does not answer and the child opens the Request Window
- **THEN** the window shows the text `request_window_unavailable` of the preset

#### Scenario: No Request Token
- **WHEN** the service does not answer the request of the Session Agent for a Request Token within 3 seconds, or refuses it
- **THEN** the window shows the text `request_window_unavailable` of the preset
- **AND** the web view loads no page

#### Scenario: First page redirects
- **WHEN** the first navigation of the Request Window gets a redirect to `/admin`, and the window cancels it
- **THEN** the window shows the text `request_window_unavailable` of the preset, not an empty page

#### Scenario: Refused link
- **WHEN** the request page is shown and the window cancels a link to `/admin`
- **THEN** the window keeps the request page and does not show the text `request_window_unavailable`

#### Scenario: Window title in German
- **WHEN** the preset is "de" and the Request Window opens
- **THEN** the window title is "Mehr Zeit wünschen"
