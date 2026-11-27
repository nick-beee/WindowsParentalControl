# Spec Delta

## MODIFIED Requirements

### Requirement: Quota for screen captures of the Session Agent
The service SHALL accept at most 200 screen captures and 50 MB per user on one day (since midnight of the Control Time Zone), and at most 5 MB per capture, on `POST /api/agent/capture`. It SHALL refuse more without a file and without a row, and it SHALL write the refusal to the log one time per user and day.

#### Scenario: Flood of captures
- **WHEN** a process sends a 201st capture for a user on one day
- **THEN** the service answers 429, writes no file and adds no row

#### Scenario: Capture too large
- **WHEN** a process sends a capture with more than 5 MB
- **THEN** the service answers 413 and keeps no file

#### Scenario: Time zone change does not reset the quota
- **WHEN** a process sent 200 captures for a user today, and the child changes the system time zone so that the local date is the next day
- **THEN** the service answers 429 to the next capture
