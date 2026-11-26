param([string]$ExePath, [int]$Count = 9, [string]$User = 'Kid')
$last = $null
for ($i = 0; $i -lt $Count; $i++) {
    $body = @{ username = $User; processName = [IO.Path]::GetFileNameWithoutExtension($ExePath); exePath = $ExePath; windowTitle = 'test'; durationSeconds = 20 } | ConvertTo-Json
    $last = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/agent/activity -ContentType 'application/json' -Body $body
}
$last | ConvertTo-Json -Compress
