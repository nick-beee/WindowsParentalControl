# app-categories Specification

## Purpose
Sorts each program of a Restricted User into an App Category, so that limits can apply to a kind of program (games, video) and not only to the whole session.

## Requirements

### Requirement: Fixed set of App Categories
The system SHALL provide the App Categories Games, Video, Social, Study, Creative, System and Unknown. The parent SHALL NOT add, delete or rename a category in this version.

#### Scenario: Categories after the first start
- **WHEN** the service starts on a new or an existing database
- **THEN** the seven categories exist, each one time

### Requirement: Default category limits
On the first start after the upgrade, the system SHALL set Games, Video, Social and Unknown to 120 minutes on a weekday and 180 minutes on Saturday and Sunday. Study, Creative and System SHALL have no limit. A later start SHALL NOT overwrite limits that the parent changed.

#### Scenario: First start after the upgrade
- **WHEN** the service starts for the first time with this version
- **THEN** Games has a weekday limit of 120 minutes and a weekend limit of 180 minutes
- **AND** Study has no limit

#### Scenario: Parent change survives a restart
- **WHEN** the parent sets the Games weekday limit to 90 minutes and the service restarts
- **THEN** the Games weekday limit is still 90 minutes

### Requirement: App Rules map an exe to a category
An App Rule SHALL map an exe to one App Category by one of two match types: the exact exe file name (for example `RobloxPlayerBeta.exe`), or a folder prefix of the full exe path. The match SHALL ignore letter case.

#### Scenario: Match by exe file name
- **WHEN** a rule maps the file name `RobloxPlayerBeta.exe` to Games and the child runs `C:\Users\Kid\AppData\Local\Roblox\Versions\v1\robloxplayerbeta.exe`
- **THEN** the exe is in Games

#### Scenario: Match by folder prefix
- **WHEN** a rule maps the folder `C:\Program Files (x86)\Steam\steamapps\common\` to Games and the child runs an exe below that folder
- **THEN** the exe is in Games

### Requirement: App Limit on a rule
An App Rule SHALL be able to carry an App Limit: a weekday and a weekend minute value (0 to 1440), or none. The App Limit SHALL apply to every exe that the rule resolves. It SHALL NOT be allowed on a rule for System.

#### Scenario: Rule with an App Limit
- **WHEN** the parent sets an App Limit of 180 weekday minutes on the rule for `Minecraft.Windows.exe`
- **THEN** the rule keeps the category Games and has the App Limit 180 on weekdays

### Requirement: Rule precedence
If more than one rule matches an exe, the system SHALL use a file-name rule before a folder rule, and the longest matching folder before a shorter one. An exe that no rule matches SHALL be in Unknown.

#### Scenario: File name wins over folder
- **WHEN** a folder rule maps `C:\Games\` to Games, a file-name rule maps `editor.exe` to Creative, and the child runs `C:\Games\Tool\editor.exe`
- **THEN** the exe is in Creative

#### Scenario: Longest folder wins
- **WHEN** a rule maps `C:\Apps\` to Study, another maps `C:\Apps\Fun\` to Games, and the child runs `C:\Apps\Fun\x.exe`
- **THEN** the exe is in Games

#### Scenario: No rule
- **WHEN** no rule matches the exe `C:\Users\Kid\Downloads\new.exe`
- **THEN** the exe is in Unknown

### Requirement: Folder match on whole folder names
A folder rule SHALL match only on a whole folder boundary, after the system makes the path canonical (full path, `\` separators, no `..` parts).

#### Scenario: Similar folder name
- **WHEN** a rule maps `C:\Games\` to Games and the child runs `C:\GamesBackup\x.exe`
- **THEN** the rule does not match

#### Scenario: Path with parent references
- **WHEN** a rule maps `C:\Windows\` to System and an exe reports the path `C:\Windows\..\Users\Kid\x.exe`
- **THEN** the rule does not match, and the exe is in Unknown

### Requirement: Default App Rules
On the first start after the upgrade, the system SHALL create default rules: the Windows folder and the Parental Control install folder to System; the Steam `steamapps\common`, Epic Games and Riot Games folders and the Roblox and Minecraft exe names to Games; the Microsoft Office exe names (Word, Excel, PowerPoint, OneNote) to Study. A later start SHALL NOT add a default rule again that the parent deleted.

#### Scenario: Default rule for Steam games
- **WHEN** the service starts for the first time with this version and the child runs a game below `C:\Program Files (x86)\Steam\steamapps\common\`
- **THEN** the game is in Games

#### Scenario: Deleted default rule stays deleted
- **WHEN** the parent deletes the default Office rule for `WINWORD.EXE` and the service restarts
- **THEN** the rule does not exist

### Requirement: System category is never limited
The system SHALL never count usage for System and SHALL never suspend a process in System. The parent SHALL NOT set a limit on System.

#### Scenario: Limit on System is refused
- **WHEN** the parent tries to set a limit on System in the web admin
- **THEN** the system refuses the change and System keeps no limit

### Requirement: Parent edits categories and rules in the web admin
The web admin SHALL let the parent set the weekday and weekend limit of each category except System (minutes from 0 to 1440, or no limit), and list, add, change and delete App Rules with their App Limits. Only a signed-in admin SHALL reach these functions.

#### Scenario: Add a rule from the App Usage table
- **WHEN** the parent selects an app in the App Usage table and assigns it to Video
- **THEN** the system creates a file-name rule for that exe to Video
- **AND** the next usage of that exe counts for Video

#### Scenario: Invalid limit
- **WHEN** the parent enters a limit of 2000 minutes
- **THEN** the system refuses the value and keeps the old limit

#### Scenario: Not signed in
- **WHEN** a request to change a category limit has no valid admin session
- **THEN** the system refuses the request

### Requirement: Exe data is untrusted
The system SHALL treat the exe path, the exe name and the window title that it gets from the Session Probe or the Session Agent as untrusted text: it SHALL use them only to match rules and to show them escaped in the admin pages.

#### Scenario: Script in a window title
- **WHEN** the Session Probe reports a window title or path that contains `<script>`
- **THEN** the admin page shows the text as text and runs no script
