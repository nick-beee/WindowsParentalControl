# Installs the working tree build on VM 102 like the installer does. Test machine only.
$ErrorActionPreference = 'Stop'
$src = 'C:\dev\parental-control'
$app = 'C:\Program Files\ParentalControl'
$svc = 'ParentalControl.Service'
if (Get-Service $svc -ErrorAction SilentlyContinue) { sc.exe stop $svc | Out-Null; Start-Sleep 4 }
Get-Process ParentalControl.Agent, ParentalControl.Probe -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($p in 'Service', 'Agent', 'Admin', 'Probe') {
    dotnet publish "$src\src\ParentalControl.$p\ParentalControl.$p.csproj" -c Release -o "$app\$($p.ToLower())" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    sc.exe create $svc binPath= "`"$app\service\ParentalControl.Service.exe`"" start= auto obj= LocalSystem DisplayName= "Parental Control Service" | Out-Null
    sc.exe failure $svc reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}
# As the installer does (require-admin-sign-in SF2): run the restart actions also after a stop with an error code.
sc.exe failureflag $svc 1 | Out-Null
reg add HKLM\Software\Microsoft\Windows\CurrentVersion\Run /v ParentalControlAgent /t REG_SZ /d "`"$app\agent\ParentalControl.Agent.exe`"" /f | Out-Null
sc.exe start $svc | Out-Null
Start-Sleep 5
Get-Service $svc | Format-Table -AutoSize Name, Status
