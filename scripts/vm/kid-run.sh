#!/bin/sh
# Runs a script from scripts/vm/guest as Kid in the console session of the VM:
#   kid-run.sh <script.ps1> [arguments]
# Kid must be logged on in the console. The script is copied to C:\Users\Public, because
# Kid cannot read the folders of nick. For kidstart.ps1, the program list is guest/kidstart.txt
# if it exists, else guest/kidstart.example.txt.
set -eu
cd "$(dirname "$0")/guest"
name=$1; shift
scp -q "$name" winvm:C:/Users/Public/
if [ "$name" = kidstart.ps1 ]; then
  list=kidstart.example.txt
  if [ -f kidstart.txt ]; then list=kidstart.txt; fi
  scp -q "$list" winvm:C:/Users/Public/kidstart.txt
fi
task="Kid-${name%.ps1}"
ssh winvm "schtasks /create /tn $task /tr \"powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\\Users\\Public\\$name $*\" /ru Kid /it /sc once /st 23:59 /f >nul && schtasks /run /tn $task"
