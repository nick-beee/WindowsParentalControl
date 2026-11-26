# Runs as Kid in the console session: brings a process window to the front.
param([string]$Name)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
}
'@
$p = Get-Process $Name -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $p) { "no window: $Name" | Out-File C:\Users\Public\front.txt; exit 1 }
[W]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [W]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)  # Alt tap unlocks SetForegroundWindow
[W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
"front $Name pid $($p.Id): " + [W]::SetForegroundWindow($p.MainWindowHandle) | Out-File C:\Users\Public\front.txt
