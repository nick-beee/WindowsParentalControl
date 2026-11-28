# As Kid (task 8.1): sends a Study sample to POST /api/agent/activity every 5 s, as an old agent or a script of the
# child does. The route no longer exists (404), so the samples must change no usage and no limit.
# Output: C:\Users\Public\kid-study-samples.log (one line per request with the HTTP status).
param([int]$Count = 72, [string]$ExePath = 'C:\Study\Study.exe')
$log = 'C:\Users\Public\kid-study-samples.log'
for ($i = 0; $i -lt $Count; $i++) {
    $body = @{ username = $env:USERNAME; processName = 'Study'; exePath = $ExePath; windowTitle = 'Homework'; durationSeconds = 5 } | ConvertTo-Json
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri http://127.0.0.1:5050/api/agent/activity -ContentType 'application/json' -Body $body
        $status = [int]$r.StatusCode
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { "error: $($_.Exception.Message)" }
    }
    "$(Get-Date -Format HH:mm:ss) $status" | Add-Content $log
    Start-Sleep 5
}
