# As Kid: holds the old agent mutex Global\ParentalControlAgent_Kid for some minutes (task 6.4, "Mutex of the child").
param([int]$Seconds = 600)
$created = $false
$m = New-Object System.Threading.Mutex($true, 'Global\ParentalControlAgent_Kid', [ref]$created)
"$(Get-Date -Format HH:mm:ss.fff) mutex created=$created" | Add-Content C:\Users\Public\kid-mutex.txt
Start-Sleep $Seconds
$m.Dispose()
