# As Kid: starts the agent exe as a double-click does and records whether it exits within 2 s
# (session-probe-and-agent-channel, task 6.1). Output: C:\Users\Public\kid-agent-start.txt
$out = 'C:\Users\Public\kid-agent-start.txt'
$p = Start-Process 'C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe' -PassThru
$t = [Diagnostics.Stopwatch]::StartNew()
$exited = $p.WaitForExit(2000)
"$(Get-Date -Format HH:mm:ss.fff) pid $($p.Id) exited=$exited after $($t.ElapsedMilliseconds) ms exitcode=$(if ($exited) { $p.ExitCode } else { '-' })" | Add-Content $out
