using System.Runtime.InteropServices;
using ParentalControl.Core.Platform;

namespace ParentalControl.Agent;

/// <summary>
/// Minimizes the visible top-level windows of each Pending Suspension while the process still runs. The service
/// suspends the process some seconds later, also without the agent. A suspended window cannot be minimized:
/// Windows queues the request until the process runs again.
/// </summary>
internal static class WindowMinimizer
{
    private const int SwMinimize = 6;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    public static void Minimize(IReadOnlyList<AgentAppDto> pendingApps)
    {
        if (pendingApps.Count == 0)
            return;

        // The pid alone does not identify a process: Windows reuses pids.
        var pids = new HashSet<uint>();
        foreach (var app in pendingApps)
        {
            var (_, start) = ProcessControl.QueryPathAndStart(app.pid);
            if (start is { } s && s == app.startTimeUtc)
                pids.Add((uint)app.pid);
        }
        if (pids.Count == 0)
            return;

        // EnumWindows lists only the windows of the desktop of the agent, so never a window of another session.
        var windows = new List<(IntPtr Hwnd, uint Pid)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                windows.Add((hwnd, pid));
            return true;
        }, IntPtr.Zero);

        foreach (var (hwnd, pid) in windows)
        {
            var ok = ShowWindowAsync(hwnd, SwMinimize);
            AgentLog.Logger.Information("Minimized window 0x{Hwnd:X} of pid {Pid} before its suspension: {Ok}", hwnd.ToInt64(), pid, ok);
        }
    }
}
