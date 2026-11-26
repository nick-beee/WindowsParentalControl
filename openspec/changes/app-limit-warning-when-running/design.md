# Design

## Context

`CategoryEnforcer.Tick` runs every 60 seconds. It calculates the `DayLimits` of each Restricted User, then calls `SendWarnings`. `SendWarnings` sends each `LimitStatus` whose `DueWarning` is not `None`, with the deduplication key `warn:<user>:<limit key>:<warning>` in `_sentToday`. After that, the tick lists the processes of the child's sessions with `ProcessControl.ListProcesses` for the `SuspensionPlanner`. So the warnings use no process data today, and an App Limit warning goes out for an app that does not run (see proposal.md, Why).

## Goals / Non-Goals

**Goals:**
- An App Limit warning goes out only while the app runs, and a late start still gets the due warning.
- The decision is pure Domain code with xUnit tests on the Mac.
- A small change in `CategoryEnforcer.cs`, because the open PR #22 also changes this file.

**Non-Goals:**
- No new data from the Session Agent and no new WinAPI call.
- No change to the deduplication of the warnings or to the warning text.

## Decisions

### D1. Running process from the service, not the Foreground Window

Decided by the orchestrator under the user's delegation (2026-11-26).

An App Limit warning is due only if a process of an exe that this App Limit covers runs now in a session of that Restricted User. The service reads the processes from its own process list (`ProcessControl.ListProcesses`, which uses `Process.GetProcesses`, `OpenProcess` with `PROCESS_QUERY_LIMITED_INFORMATION`, `QueryFullProcessImageNameW` and `GetProcessTimes`). These calls exist already. A Category Limit warning stays as today.

An App Limit covers an exe if `CategoryResolver.Resolve` gives the rule of that App Limit for the exe path. This is the same match that `DayLimits.LimitFor` uses for the suspension. So a file-name rule that wins over a folder rule also takes the warnings from that folder rule.

*Alternative (rejected):* send the warning if the app was the Foreground Window in the last N minutes, from the samples of the Session Agent. The child can send requests to `127.0.0.1:5050`, and the agent data has no authentication (CLAUDE.md, Security). The process list of the service has no such gap. The foreground check also needs a value for N and a new time window in `ForegroundHistory`, which today keeps only "was a Foreground Window today". A running app in the background (for example a game behind a browser) also gets the warning with the chosen rule, and that is correct: the game stops when the limit ends.

### D2. Pure method `DayLimits.DueWarnings`

New method in `CategoryLimitCalculator.cs`:

```csharp
public IReadOnlyList<LimitStatus> DueWarnings(IEnumerable<CategoryResolution> runningExes)
```

It returns each limit of `All` whose `DueWarning` is not `None`, and for an App Limit only if one of the `runningExes` has the rule of that App Limit. The caller resolves the exe paths of the running processes of one user with the `CategoryResolver` of the current config. The method has no WinAPI call and no clock, so xUnit tests on the Mac cover it.

A process counts also if it is suspended. A suspended process of an exe with no time left has no due warning (`RemainingMinutes` is 0). A suspended process of an exe with time left gets the resume in the same tick, so the warning is correct.

### D3. Order in the tick

The tick lists the processes before `SendWarnings`, and uses the same list for the `SuspensionPlanner`. So the tick lists the processes one time, as before. If no session of a Restricted User exists and no suspension record exists, the tick returns early as before; in that case no user gets a warning anyway.

### D4. Deduplication stays

A warning that is not due because the app does not run does not go into `_sentToday`. If the app starts while 1 to 5 minutes remain, the next tick sends the warning that is due then: the 5-minute warning (with the real number of minutes left, for example 3) while 2 to 5 minutes remain, else the 1-minute warning. Each key still goes out at most one time per limit and day. If the 5-minute warning went out and the app then stops, the 1-minute warning does not go out.

## Risks / Trade-offs

- [The app starts and ends between two ticks] → The child gets no warning. The tick is 60 seconds, and an app that runs for less time than that uses almost no time.
- [A helper process of the app runs in the background (for example a launcher)] → The child gets the warning without a window of the app. This is rare and harmless; the rule covers the exe, so the parent chose it.
- [The child renames or copies the exe] → A file-name rule no longer covers it, so no App Limit applies and no App Limit warning goes out. The Category Limit and its warning still apply. This is the same gap as for the suspension today.

## Migration Plan

No data change. Deploy with the next release. To roll back, install the previous release.
