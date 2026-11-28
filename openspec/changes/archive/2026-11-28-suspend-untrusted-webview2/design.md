# Design

## Context

Three parts of the service decide about `msedgewebview2.exe`:

- `ProtectedPrograms.IsProtected` (a block and the rule of a program that is never suspended) tested only the file name.
- `SuspensionPlanner.IsExcluded` (the suspension at a limit) tested only the file name.
- The counting of the Session Probe (`CountedProcesses`, `SessionProbeHost.ReadEntry`) already tested the path with `TrustedWebView2`. Since the review finding B4 of `session-probe-and-agent-channel`, an untrusted copy counts as itself, by its App Rule.

So a copy of a game as `C:\Users\Kid\Downloads\wv\msedgewebview2.exe` counts in Unknown, but the system never suspends it and a block never closes it. The rule "A program that is never suspended still has a limit" locks the session after 60 seconds, but only while the probe locks are on (shadow mode, archived design D12). Issue #48 and task 7.4 of the archived change ask for a suspension by the path.

## Goals / Non-Goals

Goals:
- A copy of `msedgewebview2.exe` outside the folders of the Evergreen WebView2 Runtime is an ordinary exe for a block, for a limit and for the counting.
- One path test for the three parts, so that they cannot differ.
- The Request Window and each WebView2 control of an app keep working, and the system never suspends the runtime of the Evergreen WebView2 Runtime.

Non-Goals:
- No new WinAPI call, no new table, no new column. The service reads the Program Files known folders with `Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)` and `Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)` (they call `SHGetKnownFolderPath`), as `SessionProbeHost` did before.
- No change of the parent chain (`ProcessControl.ResolveWebView2Creator`).

## Decisions

### D1. One path test: the Trusted WebView2 Runtime

*Decided by the orchestrator under the user's delegation (2026-11-28).*

The class `TrustedWebView2` moves from `ParentalControl.Domain.Sessions` (`CountedProcesses.cs`) to `ParentalControl.Domain.Categories` (`TrustedWebView2.cs`). Its test does not change: an exe is trusted if its canonical path (`WindowsPath.Canonicalize`) has the file name `msedgewebview2.exe` (any case) and is below `<Program Files (x86)>\Microsoft\EdgeWebView\Application\` or `<Program Files>\Microsoft\EdgeWebView\Application\`.

- `ProtectedPrograms` gets the instance in its constructor (a required parameter) and exposes it as `ProtectedPrograms.TrustedWebView2`. `IsProtected` tests the path with it, not the file name.
- `PlannerInput` gets the instance as a required parameter after `WindowsFolder`. `SuspensionPlanner.IsExcluded` tests the path with it. The constant `SuspensionPlanner.WebView2FileName` goes away.
- `CategoryRepository.LocalProtectedPrograms()` makes the one instance of the service. `CategoryEnforcer` gives `BlockEnforcer.ProtectedPrograms.TrustedWebView2` to the planner. `SessionProbeHost` uses `_protected.TrustedWebView2` for the creator walk and for `CountedProcesses.Select`.

The parameter is required, not optional. An optional parameter with a fallback to the file name would bring the bypass back. A `TrustedWebView2` without valid Program Files folders trusts nothing (fail closed): then the system can suspend the runtime of a WebView2 app, which the hard rule permits.

**The invariant.** `NeverSuspendedAllowance` reads `ProtectedPrograms.IsProtected` as "the system never suspends this exe". If `IsProtected` said "not protected" while the planner still excluded the copy by its file name, the copy would get neither the lock nor a suspension. So the planner and `IsProtected` change in the same commit, and the xUnit tests `Untrusted_WebView2_copy_is_suspended_and_not_protected` and `Trusted_WebView2_is_protected_and_not_suspended` check both sides.

Rejected: a second test that reads the runtime folder from the registry (`WebView2RuntimeLocation`, the `pv` value). The registry gives one version; a WebView2 app can still run an older version folder during an update. The folder test covers each version.

### D2. An untrusted copy counts as itself

*Decided by the orchestrator under the user's delegation (2026-11-28).*

An untrusted `msedgewebview2.exe` does not count as the program that started it. It counts as itself, by its App Rule, and it is a Foreground Window exe (a suspend candidate) in each role where it is a Read entry. This is the current behaviour (B4); this change keeps it.

Reason: a creator mapping for an untrusted copy lets a game hide in a Study app. The child starts the copy from a Study app (for example with a "open file" dialog), and the game counts in Study. The copy as itself counts in Unknown, unless the parent adds an App Rule for its folder.

### D3. Path forms

The test uses the canonical form of `WindowsPath.Canonicalize`. The xUnit tests in `TrustedWebView2Tests` cover each form:

- The `\\?\` prefix, `/` separators, quotes and the letter case: trusted below the runtime folders.
- `..` parts: canonicalized first. A `..` that stays below a runtime folder is trusted (only administrators can write there). A `..` that leaves the folder is not.
- A folder name that only starts with `Application` (`Application2`): not trusted, because the test compares with a trailing `\`.
- An 8.3 short name (`C:\PROGRA~2\...`) and a UNC path to the same disk (`\\localhost\C$\...`): not trusted. The service reads the exe path with `QueryFullProcessImageNameW` (flag 0, the Win32 form). It is not confirmed on VM 102 that this form never has an 8.3 part; task 3.2 records the real paths. If a process shows such a form, the result is "not trusted", and the system can suspend that process (fail closed).
- A path that is not absolute: not trusted. `ProtectedPrograms.IsProtected` still treats it as protected (the old rule for each path that is not absolute), so the rule of a program that is never suspended limits it.

**A junction or a symbolic link is out of scope.** A standard user can make a directory junction in its own folders, for example `C:\Users\Kid\j` to the runtime folder. If Windows gives the path through the junction, the real runtime is "not trusted" and the system can suspend it (fail closed). If Windows gives the target path, the process is the real runtime that an administrator installed; a child who starts it from a script is class "a WebView2 with a false parent" of archived design D13. The opposite way, a path below a runtime folder that points to a file of the child, needs write access to a Program Files folder, which a standard user does not have. Which path Windows gives for a junction is not confirmed on VM 102.

### D4. Fixed Version and per-user WebView2 runtimes

*Decided by the orchestrator under the user's delegation (2026-11-28).*

An app can ship its own WebView2 runtime (Fixed Version), and a user without admin rights can install a per-user runtime below `%LOCALAPPDATA%`. Each such `msedgewebview2.exe` is an ordinary exe: it counts by its App Rule, Unknown if no rule matches, and the system suspends it when its category is exhausted.

The cost: a Study app with a Fixed Version runtime and no App Rule for its folder uses Unknown time while its WebView2 control has the focus, and when Unknown is exhausted, the system suspends the WebView2 processes of that app. The main window of the app keeps running, but its web content stops. Before this change, the result was a lock of the session after 60 seconds (with the probe locks on), so the suspension is the smaller fault. The parent fixes it with a folder rule for the app folder, for example `C:\Program Files\StudyApp\` → Study.

The parent must add such a rule only for a folder that a standard user cannot write (below a Program Files folder). A Study rule for a folder below `%LOCALAPPDATA%` lets the child copy a game into it. The hint text of the web admin does not change in this change.

Rejected: trust each `msedgewebview2.exe` that has a valid Microsoft signature. The child can copy a signed runtime and use it to show a game page; the signature names the file, not the use. And a check of the signature needs WinAPI calls (`WinVerifyTrust`) on each sample.

### D5. The refusal of a blocked file rule stays

A file rule for `msedgewebview2.exe` matches each exe with that name, also the Trusted WebView2 Runtime. A block on it could never act on the runtime, so `AppRule.Validate` still refuses it (`ProtectedPrograms.IsProtectedFileName`). To block a copy, the parent uses a folder rule, for example `C:\Users\Kid\Downloads\`. A blocked folder rule above a runtime folder, for example `C:\Program Files (x86)\Microsoft\`, stays permitted, and it never acts on the runtime (scenario "WebView2 host").

### D6. The hard rule

A limit still never closes a program. The copy of a game is suspended, and resumed later, like any other exe. Only a Blocked Start of a copy is closed, at its start, as for any other Blocked Program.

### D7. The archived design

Task 7.4 of `session-probe-and-agent-channel` asks this change to modify the list of programs that are never suspended in design D5 of that change. This change adds a note to that paragraph of the archived design. The archived spec scenario "Game renamed as WebView2" changes through the spec delta of `category-limits`.

## Risks / Trade-offs

- A Study app with a Fixed Version runtime stops its web content when Unknown is exhausted (D4). The parent sees the suspension in the dashboard and adds a folder rule.
- If a Windows build starts the runtime of the Evergreen WebView2 Runtime from another folder, the system treats it as an ordinary exe. When Unknown is exhausted, it suspends the WebView2 processes of each app and of the Request Window, and the parent does not get an alert for it. VM task 3.2 checks the folder on Windows 10 22H2; the release gates of the child's PC (archived task 9.1) list each `msedgewebview2.exe` path in normal use.

## Migration Plan

No data changes. The release that has this change suspends a copy at the first planner tick after the update, if its category is exhausted and it was a Foreground Window exe on that day.
