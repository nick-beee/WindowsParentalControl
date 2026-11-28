# session-lock Specification

## Purpose
Keeps the Enforced Workstation Lock away from the first seconds of a sign-out, a restart or a shutdown, so that a lock never makes Windows end a program of the child with unsaved data.

## Requirements

### Requirement: Lock Deferral at the end of a session
When the Session Agent reports that the session of a Restricted User ends, the system SHALL start a Lock Deferral of 75 seconds for that session, unless its limit refuses it, also if it refuses the Session End Hold. Before a Forced Logoff it SHALL start one too. During the deferral, the system SHALL NOT apply the Enforced Workstation Lock to that session and SHALL NOT send a lock message to it. The deferral SHALL end when the session ends or when its time is over.

#### Scenario: Break starts in the first second of a sign-out
- **WHEN** Notepad has unsaved text, the child signs out, and Continuous Use reaches N minutes 0.3 seconds later
- **THEN** the system does not lock the session
- **AND** Notepad asks to save, as in a sign-out without a Break

#### Scenario: Sign-out completes
- **WHEN** the system deferred a lock of the Allowance Time and the child saves the text and the sign-out completes
- **THEN** the system does not lock the session
- **AND** the deferral ends with the session

#### Scenario: Deferral after a refused hold
- **WHEN** a sign-out of the child did not complete 5 minutes ago, no lock was deferred in it, and the child signs out again while the Schedule Window ends
- **THEN** the system refuses the Session End Hold
- **AND** the system does not lock the session during the deferral

### Requirement: Check at the moment of the lock
The system SHALL check for a Lock Deferral directly before it locks the session, also after a message that it sent before the lock. A lock that the deferral stops SHALL NOT mark the session as locked and SHALL NOT start a Rest. If the unlock check or a sign-in during a Break finds the deferral, the system SHALL track the session as unlocked, so that its time counts as used time and the checks of each tick apply to it.

#### Scenario: Message before the lock
- **WHEN** the system shows the message that the daily screen time is over, and the child signs out while the message shows
- **THEN** the system does not lock the session after the message

#### Scenario: Usage during the deferral
- **WHEN** the system deferred the lock of the Allowance Time and the child cancels the sign-out
- **THEN** the Allowance Time counts the time of the deferral as used time

#### Scenario: Unlock check during the deferral
- **WHEN** the Allowance Time is used up, the session is locked, and the child unlocks it during a deferral
- **THEN** the system does not lock the session during the deferral
- **AND** the Allowance Time counts the time from the unlock as used time
- **AND** the system locks the session within 10 seconds after the end of the deferral, also if Windows does not report the lock flag

### Requirement: Owed lock at the end of the deferral
A lock that becomes due during a Lock Deferral is owed. When the deferral ends by its time and the session still exists, the system SHALL check the session again and SHALL lock it if a lock is still due: within 10 seconds for the Allowance Time, the Schedule Window, a Break and a Resume Lock, and within 20 seconds for the fallback of a suspension. A changed state SHALL apply: a lock that Bonus Minutes made unnecessary SHALL NOT run.

#### Scenario: Sign-out cancelled at the schedule end
- **WHEN** the Schedule Window ends during the sign-out, Notepad asks to save, and the child clicks Cancel
- **THEN** the system locks the session within 10 seconds after the end of the deferral
- **AND** Notepad still runs with its unsaved text

#### Scenario: Bonus Minutes during the deferral
- **WHEN** the lock of the Allowance Time is owed and the parent approves 15 Bonus Minutes before the deferral ends
- **THEN** the system does not lock the session at the end of the deferral

#### Scenario: Fallback owed
- **WHEN** a suspension failed during the deferral and the child cancelled the sign-out, while Games is still exhausted
- **THEN** the system tries the suspension again after a new Pending Suspension and locks the session within 20 seconds after the end of the deferral
- **AND** the parent gets no alert that says the session is locked before the session locks

#### Scenario: Break owed
- **WHEN** a Break started during the deferral and the child cancelled the sign-out
- **THEN** the system sends the message of the running Break and locks the session within 10 seconds after the end of the deferral

#### Scenario: Resume Lock owed
- **WHEN** a Resume Lock of the child runs, the child starts a sign-out, and cancels it
- **THEN** the system does not lock the session during the deferral
- **AND** the system sends the message of the Resume Lock and locks the session within 10 seconds after the end of the deferral

### Requirement: Limit the Lock Deferral
A session-end request of the Session Agent is a claim of the child, so a request during a deferral of the session SHALL NOT extend it. After a deferral that owed a lock and ended by its time while the session still existed, the system SHALL start no deferral for that Restricted User for 15 minutes from its start, except before a Forced Logoff. A deferral that ended with the session, or that owed no lock, SHALL NOT count. The system SHALL log each deferral, each deferred lock and each refusal.

#### Scenario: Fake request at the limit
- **WHEN** the Allowance Time ends and the child makes its Session Agent send the session-end request but does not sign out
- **THEN** the system locks the session at most 75 seconds plus 10 seconds after the time when it was due

#### Scenario: Repeated fake request
- **WHEN** the child makes its Session Agent send the session-end request again 5 minutes after a deferral that owed a lock and did not end with a sign-out
- **THEN** the system starts no deferral and locks the session within 60 seconds

#### Scenario: Request repeated during a deferral
- **WHEN** the child makes its Session Agent send the session-end request every 10 seconds during a deferral that owes a lock
- **THEN** the deferral ends 75 seconds after the first request

#### Scenario: Requests while no lock is due
- **WHEN** the child makes its Session Agent send the session-end request many times during the day while the Allowance Time and the Schedule Window have time left and no Break is due
- **THEN** a later deferral at the limit is still granted

#### Scenario: Session-end request on the old route
- **WHEN** a process of the child sends the session-end request to `POST /api/agent/session-ending`
- **THEN** the web server answers 404 and no deferral starts

### Requirement: Lock timings with a Lock Deferral
The timings of the Enforced Workstation Lock in the requirements for Breaks, for the Allowance Time, for the Schedule Window, for the fallback of a suspension and for a Resume Lock SHALL apply as before when no Lock Deferral of the session is active. During a deferral, a lock that they require SHALL be owed and SHALL run as the requirement "Owed lock at the end of the deferral" states.

#### Scenario: No sign-out
- **WHEN** Continuous Use reaches N minutes and nobody started a sign-out
- **THEN** the system starts the Break and locks the session as before, within 60 seconds

### Requirement: Delayed lock of the parent
When the countdown of a lock from the admin page ends during a Lock Deferral of the session, the system SHALL wait until the deferral ends and SHALL lock the session then, if it still exists. It SHALL try again at most 3 times. The deferred lock SHALL count against the limit of the Lock Deferral like each other lock. The lock from Telegram SHALL lock at once, as before.

#### Scenario: Sign-out during the countdown
- **WHEN** the parent locks the session from the admin page with a countdown of 15 seconds, and the child starts a sign-out during the countdown and clicks Cancel in the save dialog of Notepad
- **THEN** the system locks the session about 0.5 seconds after the end of the deferral
- **AND** Notepad still runs with its unsaved text

#### Scenario: Sign-out completes during the countdown
- **WHEN** the child starts a sign-out during the countdown of a lock of the parent and the sign-out completes
- **THEN** the system does not lock a session

#### Scenario: Lock of the parent without a sign-out
- **WHEN** the parent locks the session from the admin page and no deferral is active
- **THEN** the system locks the session after the countdown, as before
