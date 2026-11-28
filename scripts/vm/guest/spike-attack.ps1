# As Kid (spike of session-probe-and-agent-channel, tasks 1.1 and 1.3): tries to stop, open and reach the Session
# Probe of the spike. Writes C:\Users\Public\spike\attack.log. Not part of the product.
#   spike-attack.ps1 <probe pid> [pipe name without \\.\pipe\]
param([int]$ProbePid, [string]$PipeName = "")
$log = "C:\Users\Public\spike\attack.log"
function L($s) { Add-Content $log "$((Get-Date).ToUniversalTime().ToString('HH:mm:ss.fff')) $s" }
L "user=$env:USERNAME probe=$ProbePid"
L ("taskkill: " + ((taskkill /pid $ProbePid /f 2>&1) -join ' '))
try { Stop-Process -Id $ProbePid -Force -ErrorAction Stop; L "Stop-Process: no error" } catch { L "Stop-Process: $($_.Exception.Message)" }
if ($PipeName) {
  try { $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(2000); L "pipe open: CONNECTED" }
  catch { L "pipe open: $($_.Exception.GetType().Name) $($_.Exception.Message)" }
  try { $s = New-Object System.IO.Pipes.NamedPipeServerStream($PipeName, [System.IO.Pipes.PipeDirection]::InOut, 2); L "second instance: CREATED" }
  catch { L "second instance: $($_.Exception.GetType().Name) $($_.Exception.Message)" }
}
L ("probe still runs: " + [bool](Get-Process -Id $ProbePid -ErrorAction SilentlyContinue))
