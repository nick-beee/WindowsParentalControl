# Shows the service state, the suspended threads of the test programs and the end of the service log.
param([string[]]$Names = @('TestGame', 'Other'))
(Get-Service ParentalControl.Service).Status
Get-Process $Names -ErrorAction SilentlyContinue | ForEach-Object {
  '{0} pid={1} threads={2} suspended={3}' -f $_.Name, $_.Id, $_.Threads.Count, ($_.Threads | Where-Object WaitReason -eq 'Suspended').Count }
$log = Get-ChildItem C:\ProgramData\ParentalControl\logs\*.log | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName -Tail 8 | Select-String -NotMatch launched
