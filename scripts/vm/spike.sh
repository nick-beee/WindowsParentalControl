#!/bin/sh
# Helpers for the spike of session-probe-and-agent-channel (task group 1) on the test VM. Not part of the product.
#   spike.sh deploy            copies scripts/vm/tools/{ProbeSpike,SpikeKid,SpikeHook,TestGame} to the VM, publishes them
#                              (ProbeSpike self-contained with the VM test switches, -p:VmTest=true, into
#                              C:\Program Files\ProbeSpike) and restarts the service ProbeSpike. Never use it for the
#                              child's PC (task 9.1): that build has no -p:VmTest=true and no SpikeKid, SpikeHook or TestGame
#   spike.sh conf k=v ...      writes C:\ProgramData\probespike\host.conf and restarts the service (the service creates
#                              the folder with an admin-only ACL; it refuses a folder that an older build left behind:
#                              delete the old C:\ProgramData\probespike on VM 102 once as an administrator)
#   spike.sh kid <args>        runs C:\Users\Public\spike\bin\SpikeKid.exe <args> as Kid in the console session
#   spike.sh kidcmd <cmdline>  runs a command line as Kid in the console session (scheduled task /ru Kid /it)
#   spike.sh log [n]           prints the last n lines of the service log
set -eu
cd "$(dirname "$0")"
cmd=$1; shift
case "$cmd" in
deploy)
  ssh winvm "sc stop ProbeSpike >nul 2>&1 & rmdir /s /q C:\\dev\\spike 2>nul & mkdir C:\\dev\\spike"
  COPYFILE_DISABLE=1 tar --exclude bin --exclude obj -cf - -C tools ProbeSpike SpikeKid SpikeHook TestGame | ssh winvm "tar -xf - -C C:/dev/spike"
  ssh winvm "cd /d C:\\dev\\spike && dotnet publish ProbeSpike -c Release -r win-x64 --self-contained -p:VmTest=true -o \"C:\\Program Files\\ProbeSpike\" 2>&1 | findstr /i \" error -> \" & dotnet publish SpikeKid -c Release -o C:\\Users\\Public\\spike\\bin 2>&1 | findstr /i \" error -> \" & dotnet publish SpikeHook -c Release -o C:\\Users\\Public\\spike\\hook 2>&1 | findstr /i \" error -> \" & dotnet publish TestGame -c Release -o C:\\Games\\TestGame 2>&1 | findstr /i \" error -> \""
  ssh winvm "sc query ProbeSpike >nul 2>&1 || sc create ProbeSpike binPath= \"\\\"C:\\Program Files\\ProbeSpike\\ProbeSpike.exe\\\" host\" start= auto" >/dev/null
  ssh winvm "sc start ProbeSpike" >/dev/null && echo deployed ;;
conf)
  lines=""
  for kv in "$@"; do lines="$lines& echo $kv"; done
  ssh winvm "sc stop ProbeSpike >nul 2>&1 & (if not exist C:\\ProgramData\\probespike\\ (sc start ProbeSpike >nul & powershell -NoProfile -Command \"Start-Sleep 3\" & sc stop ProbeSpike >nul & powershell -NoProfile -Command \"Start-Sleep 2\")) & (echo.$lines)> C:\\ProgramData\\probespike\\host.conf & powershell -NoProfile -Command \"Start-Sleep 2\" & sc start ProbeSpike >nul && type C:\\ProgramData\\probespike\\host.conf" ;;
kid)
  ssh winvm "schtasks /create /tn KidSpike$$ /tr \"C:\\Users\\Public\\spike\\bin\\SpikeKid.exe $*\" /ru Kid /it /sc once /st 23:59 /f >nul && schtasks /run /tn KidSpike$$ >nul && echo started" ;;
kidcmd)
  ssh winvm "schtasks /create /tn KidCmd$$ /tr \"$*\" /ru Kid /it /sc once /st 23:59 /f >nul && schtasks /run /tn KidCmd$$ >nul && echo started" ;;
log)
  ssh winvm "powershell -NoProfile -Command \"Get-Content C:\\ProgramData\\probespike\\host.log -Tail ${1:-20}\"" ;;
esac
