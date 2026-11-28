# Sends a Grace Request for a user and declines it through the web admin API, so that the Session Agent shows the
# decline toast. It signs in with the next TOTP code first. Run `VmTool totp setup` one time before.
param([string]$User = 'Kid', [int]$Minutes = 10, [string]$Reason = 'VM test')
$vmTool = Join-Path $PSScriptRoot '..\tools\VmTool'
$sent = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/request -ContentType application/json `
    -Body (@{ minutes = $Minutes; reason = $Reason; username = $User } | ConvertTo-Json)
$sent | ConvertTo-Json -Compress
$code = (dotnet run --project $vmTool -- totp code | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $code) { throw "No TOTP code: run VmTool totp setup first" }
Invoke-WebRequest -UseBasicParsing -Method Post -Uri http://127.0.0.1:5050/api/admin/login -Body @{ code = $code.Trim() } -SessionVariable admin | Out-Null
$id = (dotnet run --project $vmTool -- sql "SELECT MAX(id) FROM grace_requests" | Select-Object -Last 1).Trim()
Get-Date -Format HH:mm:ss.fff
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/admin/resolve-request -WebSession $admin -ContentType application/json `
    -Body (@{ requestId = [int]$id; action = 'deny' } | ConvertTo-Json) | ConvertTo-Json -Compress
"declined request $id"
