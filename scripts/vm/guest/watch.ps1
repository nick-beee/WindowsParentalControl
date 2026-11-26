param([int]$N = 4)
for ($i = 0; $i -lt $N; $i++) {
  $s = (Invoke-RestMethod http://127.0.0.1:5050/api/admin/dashboard).sessions
  "{0:HH:mm:ss} {1} live={2}" -f (Get-Date), $s.currentApp, $s.isAppLive
  Start-Sleep 20
}
