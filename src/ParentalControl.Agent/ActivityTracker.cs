using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace ParentalControl.Agent;

public static class ActivityTracker
{
    private sealed record ActivityResponse(bool Success, int[]? SuspendedPids);

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // The window of a Store (UWP) app belongs to this host. The app itself runs in another process.
    private const string StoreAppHostFileName = "ApplicationFrameHost.exe";

    private static string QueryExePath(uint pid)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return "";
        try
        {
            var sbExe = new StringBuilder(1024);
            int size = sbExe.Capacity;
            return QueryFullProcessImageNameW(hProcess, 0, sbExe, ref size) ? sbExe.ToString(0, size) : "";
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>The pid of the Store app in a host window: the first child window of another process. 0 if none.</summary>
    private static uint FindHostedAppPid(IntPtr hostWindow, uint hostPid)
    {
        uint appPid = 0;
        EnumChildWindows(hostWindow, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid == 0 || childPid == hostPid) return true;
            appPid = childPid;
            return false;
        }, IntPtr.Zero);
        return appPid;
    }

    public static (string ProcessName, string Title, string ExePath) SampleForegroundWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("Unknown", "", "");

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return ("Unknown", "", "");

            string processName = "Unknown";
            string exePath = QueryExePath(pid);

            // A Store app: report the app process, not the host in the Windows folder (that is System).
            if (Path.GetFileName(exePath).Equals(StoreAppHostFileName, StringComparison.OrdinalIgnoreCase))
            {
                var appPid = FindHostedAppPid(hwnd, pid);
                var appPath = appPid == 0 ? "" : QueryExePath(appPid);
                if (appPath.Length > 0)
                {
                    pid = appPid;
                    exePath = appPath;
                }
            }

            if (exePath.Length > 0)
                processName = Path.GetFileNameWithoutExtension(exePath);

            if (processName == "Unknown")
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { }
            }

            var sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString();

            return (processName, title, exePath);
        }
        catch
        {
            return ("Unknown", "", "");
        }
    }

    public static async Task ReportActivityAsync(string username)
    {
        try
        {
            var (process, title, exePath) = SampleForegroundWindow();
            if (process == "Unknown" || string.IsNullOrWhiteSpace(process)) return;

            // Skip bare explorer / shell desktop clicks or lock screens
            if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(title))
                return;
            if (process.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
                process.Equals("LogonUI", StringComparison.OrdinalIgnoreCase))
                return;

            var payload = new
            {
                username,
                processName = process,
                exePath, // full path; the service resolves the App Category from it
                windowTitle = title,
                durationSeconds = 20
            };

            using var response = await _httpClient.PostAsJsonAsync("http://127.0.0.1:5050/api/agent/activity", payload);
            if (response.IsSuccessStatusCode)
            {
                // The service answers with the pids of this user's Suspended Apps. Mute them.
                var body = await response.Content.ReadFromJsonAsync<ActivityResponse>();
                var pids = body?.SuspendedPids ?? [];
                await Task.Run(() => SuspendedAppMuter.Apply(pids));
            }
        }
        catch
        {
            // Service not running or network busy; drop tick silently
        }
    }
}
