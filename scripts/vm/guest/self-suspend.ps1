# Test only (task 5.7 of lock-after-repeated-resume): as Kid, resumes a suspended program fully and then suspends it
# from 6 s before to 6 s after each 60 s check of the service, so that the checks see it suspended.
# -TickSecond: the second of the minute of the service check (read it from the "Pending suspension" lines).
param([string]$Name = 'TestGame', [int]$TickSecond = 45, [int]$Rounds = 5)
Add-Type -Name N -Namespace W -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h); [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);'
$p = Get-Process $Name | Select-Object -First 1
$log = 'C:\Users\Public\self-suspend.txt'
function Resume-Fully { for ($i = 0; $i -lt 5; $i++) { [void][W.N]::NtResumeProcess($p.Handle) } ; "$(Get-Date -Format HH:mm:ss.fff) resume x5" | Add-Content $log }
function Wait-Second([int]$s) { while ((Get-Date).Second -ne (($s + 60) % 60)) { Start-Sleep -Milliseconds 200 } }
Resume-Fully
for ($i = 0; $i -lt $Rounds; $i++) {
    Wait-Second ($TickSecond - 6)
    "$(Get-Date -Format HH:mm:ss.fff) suspend $([W.N]::NtSuspendProcess($p.Handle))" | Add-Content $log
    Start-Sleep 12
    Resume-Fully
}
