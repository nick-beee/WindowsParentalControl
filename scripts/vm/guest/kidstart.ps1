# Runs as Kid: starts each program in C:\Users\Public\kidstart.txt (one per line, "exe|arguments"), then brings each to the front for 25 s.
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
}
'@
$log = 'C:\Users\Public\kidstart.log'; "" | Out-File $log
foreach ($line in Get-Content C:\Users\Public\kidstart.txt) {
  $exe, $arg = $line -split '\|', 2
  $p = if ($arg) { Start-Process $exe -ArgumentList $arg -PassThru } else { Start-Process $exe -PassThru }
  Start-Sleep 3
  $w = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.StartTime -gt (Get-Date).AddSeconds(-6) } | Select-Object -First 1
  if (-not $w) { $w = $p }
  [W]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [W]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
  [W]::ShowWindow($w.MainWindowHandle, 9) | Out-Null; [W]::SetForegroundWindow($w.MainWindowHandle) | Out-Null
  Start-Sleep 1
  $fp = 0; [W]::GetWindowThreadProcessId([W]::GetForegroundWindow(), [ref]$fp) | Out-Null
  "$(Get-Date -f HH:mm:ss) $line -> front pid $fp ($((Get-Process -Id $fp).ProcessName))" | Out-File $log -Append
  Start-Sleep 25
}
