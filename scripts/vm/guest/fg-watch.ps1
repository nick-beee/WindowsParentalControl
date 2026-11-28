# As Kid: logs each change of the Foreground Window process for some seconds (task 6.2, widget scenarios).
# Output: C:\Users\Public\fg-watch.txt
param([int]$Seconds = 60)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class F {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
'@
$out = 'C:\Users\Public\fg-watch.txt'
"$(Get-Date -Format HH:mm:ss.fff) start" | Add-Content $out
$last = ''
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
  $h = [F]::GetForegroundWindow(); $fpid = 0; [void][F]::GetWindowThreadProcessId($h, [ref]$fpid)
  $name = try { (Get-Process -Id $fpid).ProcessName } catch { "pid $fpid" }
  if ($name -ne $last) { "$(Get-Date -Format HH:mm:ss.fff) fg=$name" | Add-Content $out; $last = $name }
  Start-Sleep -Milliseconds 100
}
"$(Get-Date -Format HH:mm:ss.fff) end" | Add-Content $out
