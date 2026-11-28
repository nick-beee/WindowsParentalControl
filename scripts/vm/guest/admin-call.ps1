# Calls one route of the web admin API as the parent: signs in with the next TOTP code, then sends the request.
# Run `VmTool totp setup` one time before. The body is JSON text, or @<file> with the JSON (cmd strips quotes). Example:
#   admin-call.ps1 POST /api/admin/capture/request @C:\Users\Public\body.json
param([string]$Method = 'GET', [string]$Path = '/api/admin/dashboard', [string]$Body = '')
$vmTool = Join-Path $PSScriptRoot '..\tools\VmTool'
$code = (dotnet run --project $vmTool -- totp code | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $code) { throw "No TOTP code: run VmTool totp setup first" }
Invoke-WebRequest -UseBasicParsing -Method Post -Uri http://127.0.0.1:5050/api/admin/login -Body @{ code = $code.Trim() } -SessionVariable admin | Out-Null
if (-not ($admin.Cookies.GetCookies('http://127.0.0.1:5050') | Where-Object Name -eq 'pc_admin_session')) { throw "Login failed" }
Get-Date -Format HH:mm:ss.fff
if ($Body.StartsWith('@')) { $Body = Get-Content -Raw $Body.Substring(1) }
if ($Body) {
    Invoke-RestMethod -Method $Method -Uri "http://127.0.0.1:5050$Path" -WebSession $admin -ContentType application/json -Body $Body | ConvertTo-Json -Compress -Depth 5
} else {
    Invoke-RestMethod -Method $Method -Uri "http://127.0.0.1:5050$Path" -WebSession $admin | ConvertTo-Json -Compress -Depth 5
}
