New-ItemProperty -Path HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name KidMutex -Value 'powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\Users\Public\kid-mutex.ps1 600' -Force | Out-Null
Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run | Out-File C:\Users\Public\kid-run.txt
