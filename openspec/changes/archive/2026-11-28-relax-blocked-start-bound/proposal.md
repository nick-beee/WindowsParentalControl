# Proposal

## Why

Issue #46: the spec `blocked-programs` says that the service closes a Blocked Start within 2 seconds. On VM 102 (PR #33), 24 of 25 starts closed in 0.23 to 1.7 seconds. One Edge start took about 3.2 seconds, because WMI delivered the `Win32_ProcessStartTrace` event late. WMI gives no delivery time that the service can control, so the spec bound of 2 seconds is a promise that the service cannot keep.

## What Changes

- The bound in the two scenarios of the spec changes from 2 seconds to 5 seconds. The scenarios are "Child starts a browser" and "Browser start during a cancelled sign-out".
- The service keeps WMI as the source of process start events.
- No code changes. The behaviour of the service stays the same. Only the written bound changes.

Decided by the orchestrator under the user's delegation (2026-11-28).

Out of scope:
- ETW as a faster source of start events. See `design.md`, decision D1.
- A change of the 10 second start window of the requirement "Close a Blocked Start". It is a safety bound for the decision between close and suspension, not a speed bound.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `blocked-programs`: the requirements "Close a Blocked Start" and "Close a Blocked Start without a Pending Suspension" state a bound of 5 seconds, not 2 seconds.

## Impact

- `openspec/specs/blocked-programs/spec.md` (through the archive).
- No code, no database, no web admin text and no installer change.
