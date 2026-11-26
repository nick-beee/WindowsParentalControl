#!/bin/sh
# Copies the working tree (tracked and untracked, not ignored files) to the
# Windows test VM and runs the self-check tests there.
# Needs the SSH alias "winvm" in ~/.ssh/config (see docs/HANDOFF.md).
set -eu
cd "$(dirname "$0")/.."

REMOTE_DIR='C:\dev\parental-control'

# The parentheses end the "if": without them, cmd runs mkdir only if the folder exists.
ssh winvm "cmd /c \"(if exist $REMOTE_DIR rmdir /s /q $REMOTE_DIR) & mkdir $REMOTE_DIR\""

# COPYFILE_DISABLE and --no-mac-metadata stop macOS tar from adding ._* files.
# A ._*.csproj file makes "dotnet run" fail with "more than one project file".
git ls-files -z --cached --others --exclude-standard |
  COPYFILE_DISABLE=1 tar --no-mac-metadata --no-xattrs --null -T - -czf - |
  ssh winvm "tar -xzf - -C C:/dev/parental-control"

ssh winvm "set DOTNET_CLI_TELEMETRY_OPTOUT=1&& cd /d $REMOTE_DIR && dotnet run --project tests\\ParentalControl.Tests"
ssh winvm "set DOTNET_CLI_TELEMETRY_OPTOUT=1&& cd /d $REMOTE_DIR && dotnet test tests\\ParentalControl.Domain.Tests"
