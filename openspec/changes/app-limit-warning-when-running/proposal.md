# Proposal

## Why

The service sends the 5-minute and the 1-minute warning of each App Limit, also when the child does not use that app (issue #12). Example: Games is at 115 minutes and the Roblox rule has an App Limit of 120. The child plays another game and still gets "Roblox ends in 5 minutes". The message is wrong, and the child learns to ignore the warnings.

## What Changes

- The service sends an App Limit warning only if a process of an exe that this App Limit covers runs now in a session of that Restricted User. The service reads this from its own process list, not from data of the Session Agent.
- If the app starts later while 1 to 5 minutes of its App Limit remain, the service sends the due warning then. Each warning stays at most one time per limit and day.
- A Category Limit warning does not change: the service sends it also if no program of the category runs.

## Out of scope

- A check of the Foreground Window for the warnings. A running process is enough.
- A change to the Category Limit warnings, to the warning text, or to the warning times (5 and 1 minutes).
- A change to the suspension, the resume, or the count of Category Usage.
- A warning for an app that starts after its App Limit ended. The suspension message covers that case.

## Capabilities

### New Capabilities

### Modified Capabilities
- `category-limits`: the requirement "Warnings before a Category Limit ends" gets the condition that an App Limit warning needs a running process of the app.

## Impact

- `src/ParentalControl.Domain/Categories/CategoryLimitCalculator.cs`: a new pure method selects the warnings that are due, from the running exes of the user. New xUnit tests.
- `src/ParentalControl.Service/CategoryEnforcer.cs`: the tick lists the processes before it sends the warnings, and gives the running exes of each user to the new method. No new WinAPI call, no new table and no new column.
