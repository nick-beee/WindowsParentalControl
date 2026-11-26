# Grants Bonus Minutes through the web admin API and shows the service log after it.
# The call works without a login only while TOTP is off.
param([int]$UserId = 2, [int]$Minutes = 15)
Get-Date -Format HH:mm:ss.fff
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/admin/grant -ContentType application/json -Body (@{ userId = $UserId; minutes = $Minutes } | ConvertTo-Json) | ConvertTo-Json -Compress
Start-Sleep 4
$log = Get-ChildItem C:\ProgramData\ParentalControl\logs\*.log | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName -Tail 5 | Select-String -NotMatch launched
