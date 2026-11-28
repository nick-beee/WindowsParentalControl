## ADDED Requirements

### Requirement: Count a Child Resume
A Child Resume SHALL be a check that finds evidence of a run of a recorded process that the system suspended or saw suspended, while its exe still has no time left or is still a Blocked Program. The check sees the process running again, or suspended again after a run. A process that the system resumed itself, or that ran during a Session End Hold or under a Session End Guard, SHALL NOT count until the system suspends it again or sees it suspended outside a hold and a guard.

#### Scenario: Cancelled sign-outs do not count
- **WHEN** a game is suspended and the child starts a sign-out and cancels it three times in 10 minutes, so that the system or the Session Agent resumes the game each time for the end of the session
- **THEN** the system counts no Child Resume and does not start a Resume Lock

#### Scenario: Resume after Bonus Minutes
- **WHEN** the parent approves Bonus Minutes and the system resumes the game because Games has time left again
- **THEN** the system counts no Child Resume

### Requirement: Require evidence of a run for a Child Resume
The system SHALL count a Child Resume only with evidence that the process ran: its user-mode CPU time grew by at least 50 ms since the system last saw it suspended. A process that the thread check sees running without this evidence, or whose CPU time the system cannot read, SHALL NOT count, and the system SHALL still suspend it again.

#### Scenario: A thread that keeps a kernel wait after the suspension does not count as a resume
- **WHEN** Games is exhausted, the system suspended the game, no tool resumed it, and a later check sees a thread of the game outside the state Suspended, while the CPU time of the game grew less than 50 ms since the suspension
- **THEN** the system counts no Child Resume
- **AND** the system suspends the game again

#### Scenario: A failed read does not count
- **WHEN** the system suspended the game, a later check sees it running, and the system cannot read its CPU time (access denied or another error)
- **THEN** the system counts no Child Resume
- **AND** the system suspends the game again

#### Scenario: A real resume counts
- **WHEN** the system suspended the game, the child resumes it with a tool and plays, and the next check sees it running with at least 50 ms more CPU time than at the suspension
- **THEN** the system counts a Child Resume

#### Scenario: A new process with the same pid is not a resume
- **WHEN** a suspended game ended, and a new process gets its pid with another start time
- **THEN** the system does not count a Child Resume for the old record

### Requirement: Count a run between two checks
When a check sees a watched recorded process suspended, outside a Session End Hold and a Session End Guard, while its exe still has no time left or is still a Blocked Program, the system SHALL compare its user-mode CPU time with the CPU time of the last time that it saw the process suspended. A growth of at least 50 ms SHALL count a Child Resume. After the check, the CPU time of this check SHALL be the new baseline, except when the 30-second gap stopped the count.

#### Scenario: A script suspends the game before each check
- **WHEN** Games is exhausted, the system suspended the game, and a script of the child resumes the game a few seconds after each check and suspends it a few seconds before the next check
- **THEN** each check sees the game suspended with more than 50 ms of new CPU time and counts a Child Resume
- **AND** after the third Child Resume within 10 minutes the system starts a Resume Lock

#### Scenario: A suspended game stays suspended
- **WHEN** the system suspended the game and no tool resumed it, and each later check sees it suspended with less than 50 ms of new CPU time
- **THEN** the system counts no Child Resume

### Requirement: Count many resumed processes as one Child Resume
One check SHALL count at most one Child Resume per Restricted User, also if it finds many resumed processes. A check less than 30 seconds after the last counted Child Resume of the user SHALL NOT count.

#### Scenario: One resume of many processes
- **WHEN** a blocked Chrome with 10 processes is suspended and the child resumes all 10 processes with one script run
- **THEN** the system counts one Child Resume
- **AND** the session is not locked

#### Scenario: Two ticks a few seconds apart
- **WHEN** the system counted a Child Resume of the child, suspended the game again 10 seconds later, the child resumes it again at once, and another check runs 15 seconds after the counted Child Resume
- **THEN** that check does not count a Child Resume
- **AND** the system still suspends the game again

### Requirement: Lock the session after repeated resumes
After 3 Child Resumes of one Restricted User in 10 minutes, of the same exe or of different exes, the system SHALL start a Resume Lock for that user. During a Resume Lock it SHALL apply the Enforced Workstation Lock to each unlocked session of that user: within 70 seconds after the resume that the third Child Resume counted, within 10 seconds after an unlock, and within 70 seconds after a sign-in. A Lock Deferral delays this lock (capability session-lock).

#### Scenario: Script resumes a game again and again
- **WHEN** Games is exhausted, the game is suspended, and a script of the child resumes the game each time after the system suspended it again
- **THEN** after the third Child Resume within 10 minutes the system locks the session within 70 seconds
- **AND** the game process still exists and is suspended again
- **AND** the system writes an event and the parent gets an alert that names the game

#### Scenario: Different exes
- **WHEN** the child resumes a suspended game, 3 minutes later a suspended video player, and 3 minutes later the game again, while their limits are exhausted
- **THEN** the system starts a Resume Lock and locks the session

#### Scenario: Two resumes
- **WHEN** the child resumes a suspended game two times in 10 minutes and then not again for 10 minutes
- **THEN** the system does not lock the session
- **AND** a later resume counts as the first Child Resume of a new window

#### Scenario: Unlock during a Resume Lock
- **WHEN** a Resume Lock of the child runs and the child unlocks the session
- **THEN** the child gets the message of the Resume Lock
- **AND** the system locks the session again within 10 seconds

### Requirement: Messages, event and alert of a Resume Lock
The system SHALL send the child a session message before each lock of a Resume Lock, at most one time in 10 seconds per session. At the start of each Resume Lock it SHALL write an event. It SHALL send the parent an alert through the Alert Channel that names the resumed exes, at most one alert per Restricted User per day. The Resume Lock SHALL NOT close, kill or suspend a program; the system SHALL still suspend the resumed processes again as "Keep a suspended process suspended" states.

#### Scenario: Second Resume Lock on the same day
- **WHEN** a Resume Lock ended because of Bonus Minutes and the child starts a new Resume Lock on the same day
- **THEN** the system writes an event
- **AND** the parent gets no second alert today

#### Scenario: Unsaved work stays
- **WHEN** Notepad has unsaved text and a Resume Lock locks the session
- **THEN** Notepad still runs with its unsaved text after the next unlock

### Requirement: End of a Resume Lock
A Resume Lock SHALL end at the first of these: the next day of the Control Time Zone; new Bonus Minutes of the user; no exe of the counted Child Resumes still has no time left or is still a Blocked Program. When it ends, the count of Child Resumes of the user SHALL start again at 0. A restart of the service or of Windows SHALL NOT end it: the system SHALL store it and SHALL apply it again after the restart. The count itself is not stored.

#### Scenario: Bonus Minutes end the Resume Lock
- **WHEN** a Resume Lock of the child runs and the parent approves 15 Bonus Minutes
- **THEN** the Resume Lock ends, and the next unlock of the child is not locked again because of it

#### Scenario: Parent removes the block
- **WHEN** a Resume Lock started because the child resumed a blocked Chrome again and again, and the parent removes the block of `chrome.exe` while Unknown has time left
- **THEN** the Resume Lock ends

#### Scenario: Next day
- **WHEN** a Resume Lock of the child runs and the day of the Control Time Zone changes
- **THEN** the Resume Lock ends

#### Scenario: Restart during a Resume Lock
- **WHEN** a Resume Lock of the child runs, Windows restarts on the same day, and the child signs in again
- **THEN** the Resume Lock still runs, and the system locks the session within 70 seconds after the sign-in
- **AND** the system writes no new event and sends the parent no new alert

## MODIFIED Requirements

### Requirement: Session limits stay unchanged
The Allowance Time of the session, the Schedule Window and the Enforced Workstation Lock SHALL work as before. Category Limits SHALL NOT lock the session, except in the fallback when a process cannot be suspended and in a Resume Lock (requirement "Lock the session after repeated resumes").

#### Scenario: Allowance Time ends first
- **WHEN** the session Allowance Time ends while Games still has time left
- **THEN** the session is locked as before
