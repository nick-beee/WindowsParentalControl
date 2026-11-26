# Shows the service state, the suspended threads of the test programs and the end of the service log.
param([string[]]$Names = @('TestGame', 'Other'))
(Get-Service ParentalControl.Service).Status
Get-Process $Names -ErrorAction SilentlyContinue | ForEach-Object {
  '{0} pid={1} threads={2} suspended={3}' -f $_.Name, $_.Id, $_.Threads.Count, ($_.Threads | Where-Object WaitReason -eq 'Suspended').Count }
# The service stores the location of the data folder in the registry (require-admin-sign-in D14); default C:\ProgramData\ParentalControl.
$dataDir = (Get-ItemProperty -Path HKLM:\SOFTWARE\ParentalControl -Name DataFolderPath -ErrorAction SilentlyContinue).DataFolderPath
if (-not $dataDir) { $dataDir = 'C:\ProgramData\ParentalControl' }
$log = Get-ChildItem (Join-Path $dataDir 'logs\*.log') | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName -Tail 8 | Select-String -NotMatch launched
