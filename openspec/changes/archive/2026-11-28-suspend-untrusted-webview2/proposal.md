# Proposal

## Why

GitHub issue #48: `ProtectedPrograms.IsProtected` and `SuspensionPlanner` protect each exe with the file name `msedgewebview2.exe`, in any folder. The child can copy a game into a folder that it can write, for example `C:\Users\Kid\Downloads\wv\`, and rename it to `msedgewebview2.exe`. A block then never closes it, and the system never suspends it. The only limit is the lock of a program that is never suspended (60 seconds in 10 minutes). That lock is a probe lock, so in shadow mode the copy gives unlimited play with Explorer actions only. Task 7.4 of the archived change `session-probe-and-agent-channel` (design D5 and D12, review item S22) makes this fix a prerequisite of the next release.

## What Changes

- A new term, the Trusted WebView2 Runtime: an exe with the file name `msedgewebview2.exe` below `%ProgramFiles(x86)%\Microsoft\EdgeWebView\Application\` or `%ProgramFiles%\Microsoft\EdgeWebView\Application\`. These are the folders of the Evergreen WebView2 Runtime, and only administrators can change them.
- A block, the suspension, and the counting of the Session Probe use one path test for this term (`TrustedWebView2`, pure code in `ParentalControl.Domain`). The service makes one instance and gives it to all three.
- Each other `msedgewebview2.exe` is an ordinary exe:
  - A block can close it at its start, or suspend it, like any other exe.
  - The system suspends it when its category is exhausted.
  - It counts as itself, by its App Rule, and not as the program that started it. This is the current behaviour; it does not change.
- The rule of a program that is never suspended no longer applies to such a copy, because the system suspends it. So the copy gets no lock, but a suspension, also with the probe locks off.
- The refusal of a blocked file rule for `msedgewebview2.exe` stays, because such a rule matches the Trusted WebView2 Runtime too. A parent blocks a copy with a folder rule.
- A Fixed Version runtime or a per-user runtime inside an app is an ordinary exe too. The parent adds an App Rule for its folder.

Decided by the orchestrator under the user's delegation (2026-11-28).

## Out of scope

- A junction or a symbolic link. See `design.md`, D3.
- Rules by hash or by signature (stage 4). A copy of a game with the name of another exe still counts by the App Rule of its path.
- A game in a folder below `C:\Windows` that a standard user can write (GitHub issue #45).
- The VM proof. Tasks 3.1 to 3.3 list the steps; they stay open until a run on VM 102.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `blocked-programs`: the requirement "Programs that are never blocked" protects only the Trusted WebView2 Runtime, not each `msedgewebview2.exe`.
- `category-limits`: the requirement "Only the child's processes" never suspends only the Trusted WebView2 Runtime. The requirement "WebView2 counts with the program that started it" changes the scenario "Game renamed as WebView2" from a lock to a suspension, and adds two scenarios for a Fixed Version runtime.

## Impact

- Code: `src/ParentalControl.Domain/Categories/TrustedWebView2.cs` (moved from `Sessions/CountedProcesses.cs`), `ProtectedPrograms.cs`, `SuspensionPlanner.cs`, `src/ParentalControl.Core/Data/CategoryRepository.cs`, `src/ParentalControl.Service/CategoryEnforcer.cs`, `src/ParentalControl.Service/SessionProbeHost.cs`.
- Tests: `tests/ParentalControl.Domain.Tests` (xUnit, on the Mac).
- Docs: `GLOSSARY.md`, ADR 0011 (a consequence), the archived design `session-probe-and-agent-channel` (a note in D5).
- No database, no web admin text, no installer change.
