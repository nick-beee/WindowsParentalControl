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

    public static (string ProcessName, string Title, string ExePath) SampleForegroundWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("Unknown", "", "");

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return ("Unknown", "", "");

            string processName = "Unknown";
            string exePath = "";
            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sbExe = new StringBuilder(1024);
                    int size = sbExe.Capacity;
                    if (QueryFullProcessImageNameW(hProcess, 0, sbExe, ref size))
                    {
                        exePath = sbExe.ToString(0, size);
                        processName = Path.GetFileNameWithoutExtension(exePath);
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

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
