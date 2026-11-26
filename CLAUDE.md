# CLAUDE.md

Fork of `DarkTama/WindowsParentalControl`. The goal is a parental control for one child (10 years, a standard Windows account) with time limits per app category and breaks. The current behaviour is in `openspec/specs/`. The plan, the next steps and the open questions are in `docs/ROADMAP.md`; the known problems are GitHub issues of the fork. Read the roadmap before you start a new stage.

## The hard rule

A limit, a break or a schedule end never closes or kills a program of the child. It locks the session (`SessionManager.LockSession`) or suspends the processes of a category (`NtSuspendProcess`), and it resumes them later. Only a blocked program (a browser, a program on the block list) is closed, and only at its start.

## Vocabulary

`GLOSSARY.md` and `CONTEXT.md` define the domain terms (Restricted User, Session Agent, Allowance Time, Grace Request, Enforced Workstation Lock). Use these terms in code, specs and commits. Add a new term to `GLOSSARY.md` when you add a new concept, for example a category.

## Workflow: spec first

Each change of behaviour goes through OpenSpec (`openspec/`, commands `/opsx:*`). A chore (CI, docs, a build script) goes straight to a PR.

1. `/opsx:explore` for an open question. `/opsx:propose` writes `proposal.md`, the spec delta, `design.md` and `tasks.md` in `openspec/changes/<name>/`. Stop after the proposal. The user approves it.
2. `/opsx:apply` implements the tasks.
3. `/opsx:verify` checks the code against the spec. For Windows behaviour, the proof is a run on the test VM (see below), not the build on the Mac.
4. `/opsx:archive` moves the delta into `openspec/specs/`.

An architecture decision that is hard to reverse gets an ADR in `docs/adr/` (next number 0009), in the style of the existing ADRs.

The `openspec` command on this Mac is a wrapper in `~/.local/bin` pinned to 1.14.1, the version that generated the skills in `.claude/skills/openspec-*`. Keep the two versions the same.

## Build and test

- Build on the Mac: `dotnet build -p:EnableWindowsTargeting=true`. The Mac has the .NET 11 SDK; it builds the `net8.0-windows` projects.
- The tests (`tests/ParentalControl.Tests`) are a console program with `Debug.Assert`, not xUnit. They call WinAPI (`kernel32.dll`, WTS), so they run on Windows only. Run them from the Mac with `scripts/vm-test.sh`: it copies the working tree to the test VM and runs them in Debug. The run is green when it prints `ALL ASSERTIONS PASSED`; a failed `Debug.Assert` stops the process with an error.
- Put new pure logic (categories, limits, rules, break timers) in classes without WinAPI calls. Then a test for it can run on the Mac too.
- The pure logic is in `src/ParentalControl.Domain` (`net8.0`). Its xUnit tests are in `tests/ParentalControl.Domain.Tests`. Run them on the Mac with `dotnet test tests/ParentalControl.Domain.Tests`. `scripts/vm-test.sh` and CI also run them.
- CI: `.github/workflows/ci.yml` builds each project and runs the tests on `windows-latest` for each push to `main` and each PR.
- The installer: `build.ps1` (Inno Setup, Windows only). `.github/workflows/release.yml` builds it on a `v*` tag. The update button reads the releases of the fork (`AppVersion.GitHubRepo`); a release of the fork needs a version above 1.5.0. Change it in all places at once: `AppVersion.Current` and `DisplayName`, `MyAppVersion` in `installer/ParentalControl.iss`, and the version asserts in the tests. The tag must equal that version: if the tag is higher, each client sees an update on every check.

## Test VM

Windows VM 102 on the Proxmox host pve2 (192.168.1.98) is the test target: Windows 10 22H2, .NET 8 SDK, IP 192.168.1.63.

- SSH from the Mac: `ssh winvm` (user `nick`, an administrator). Commands run in `cmd`; use `powershell -NoProfile -Command "..."` for PowerShell.
- `Kid` is the Restricted User for tests (a standard account, no admin rights).
- Before you install the service on the VM, check that the snapshot `test-base` exists. Roll back to it if a test leaves the VM in a bad state. `test-base` has OpenSSH, the .NET 8 SDK and `Kid`, but no Parental Control. The older snapshot `clean-before-parental` has none of these: after a rollback to it, SSH does not work.
- Test the session lock and the re-login in the Proxmox console (noVNC or SPICE). Do not test it over RDP: an RDP session sends other session events.
- The VM is not the child's PC. Do the final test on the child's PC.
- `docs/vm.md` has the VM details: snapshots, sound card, and how to drive the console from the Mac (screenshot, keys, programs as `Kid`).

## Git

- `origin` is the fork `nick-beee/WindowsParentalControl`. `upstream` is `DarkTama/WindowsParentalControl`.
- Work on a branch and open the PR against `main` of the fork. Never open a PR against `upstream`.
- Commit style: `type(scope): message` (feat, fix, chore, docs, test).

## Security

- Data from an exe (ProductName, CompanyName, the window title, the path) is untrusted. It goes to an LLM only as data inside a strict JSON schema, never as instructions.
- The child can send requests to `127.0.0.1:5050`. A limit must never depend on data that the Session Agent sends without authentication.
