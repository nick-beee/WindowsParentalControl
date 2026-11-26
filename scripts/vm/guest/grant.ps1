# Grants Bonus Minutes through the web admin API and shows the service log after it.
# It signs in with the next TOTP code first. Run `VmTool totp setup` one time before.
param([int]$UserId = 2, [int]$Minutes = 15)
$vmTool = Join-Path $PSScriptRoot '..\tools\VmTool'
$code = (dotnet run --project $vmTool -- totp code | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $code) { throw "No TOTP code: run VmTool totp setup first" }
$code = $code.Trim()
Get-Date -Format HH:mm:ss.fff
Invoke-WebRequest -UseBasicParsing -Method Post -Uri http://127.0.0.1:5050/api/admin/login -Body @{ code = $code } -SessionVariable admin | Out-Null
if (-not ($admin.Cookies.GetCookies('http://127.0.0.1:5050') | Where-Object Name -eq 'pc_admin_session')) { throw "Login failed with code $code" }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/admin/grant -WebSession $admin -ContentType application/json -Body (@{ userId = $UserId; minutes = $Minutes } | ConvertTo-Json) | ConvertTo-Json -Compress
Start-Sleep 4
# The service stores the location of the data folder in the registry (require-admin-sign-in D14); default C:\ProgramData\ParentalControl.
$dataDir = (Get-ItemProperty -Path HKLM:\SOFTWARE\ParentalControl -Name DataFolderPath -ErrorAction SilentlyContinue).DataFolderPath
if (-not $dataDir) { $dataDir = 'C:\ProgramData\ParentalControl' }
$log = Get-ChildItem (Join-Path $dataDir 'logs\*.log') | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName -Tail 5 | Select-String -NotMatch launched
