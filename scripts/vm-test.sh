#!/bin/sh
# Copies the working tree (tracked and untracked, not ignored files) to the
# Windows test VM and runs the self-check tests there.
# Needs the SSH alias "winvm" in ~/.ssh/config (see docs/vm.md).
set -eu
cd "$(dirname "$0")/.."

# VM_DIR sets another folder on the VM (for example C:\dev\pc-g4-fixes), so two agents do not share one folder.
REMOTE_DIR="${VM_DIR:-C:\\dev\\parental-control}"
REMOTE_TAR_DIR=$(printf '%s' "$REMOTE_DIR" | tr '\\' '/')

# The parentheses end the "if": without them, cmd runs mkdir only if the folder exists.
ssh winvm "cmd /c \"(if exist $REMOTE_DIR rmdir /s /q $REMOTE_DIR) & mkdir $REMOTE_DIR\""

# COPYFILE_DISABLE and --no-mac-metadata stop macOS tar from adding ._* files.
# A ._*.csproj file makes "dotnet run" fail with "more than one project file".
# The tests build with -p:VmTest=true (compile symbol VM_TEST): the test hooks of the probe and the service exist only
# in such a build (session-probe-and-agent-channel, design D15). The release (build.ps1) never sets it.
git ls-files -z --cached --others --exclude-standard |
  COPYFILE_DISABLE=1 tar --no-mac-metadata --no-xattrs --null -T - -czf - |
  ssh winvm "tar -xzf - -C $REMOTE_TAR_DIR"

ssh winvm "set DOTNET_CLI_TELEMETRY_OPTOUT=1&& cd /d $REMOTE_DIR && dotnet run --project tests\\ParentalControl.Tests -p:VmTest=true"
ssh winvm "set DOTNET_CLI_TELEMETRY_OPTOUT=1&& cd /d $REMOTE_DIR && dotnet test tests\\ParentalControl.Domain.Tests -p:VmTest=true"
