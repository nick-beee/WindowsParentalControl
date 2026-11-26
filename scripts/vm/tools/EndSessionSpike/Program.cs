// Test program for VM 102 (task group 1 of resume-suspended-apps-at-session-end).
// It checks the Windows behaviour that the design assumes. It is not part of the product.
//
//   agent [spsp] [resume=<a,b>] [delay=<ms>]   hidden WS_POPUP window; logs WM_QUERYENDSESSION and
//                                              WM_ENDSESSION; resumes the processes <a>, <b> in the handler
//   game [borderless]                          visible window with a clock; logs its own end-session messages
//   suspend <name> | resume <name>             NtSuspendProcess / NtResumeProcess on each process <name>
//   minimize <name>                            ShowWindowAsync(SW_FORCEMINIMIZE) on the top-level windows of <name>
//   windows <name>                             logs the top-level windows of <name>
//   poke-minimize <name>                       posts WM_NULL, waits 6 s, then does what minimize does
//   minimize-resume <name> [settle=<ms>]       resumes <name> for a short time, minimizes it with SW_MINIMIZE,
//                                              waits <ms> after the minimize, and suspends it again
//   service                                    a Windows service that logs PRESHUTDOWN, SHUTDOWN and session changes
//
// The log of a session process is C:\Users\Public\spike\<mode>-<pid>.log. The service log is
// C:\ProgramData\spike\service.log. Each line starts with the UTC time in milliseconds.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static StreamWriter? _log;

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "game";
        var dir = mode == "service" ? @"C:\ProgramData\spike" : @"C:\Users\Public\spike";
        Directory.CreateDirectory(dir);
        var file = mode == "service" ? Path.Combine(dir, "service.log") : Path.Combine(dir, $"{mode}-{Environment.ProcessId}.log");
        _log = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Log($"start mode={mode} args=[{string.Join(' ', args)}] pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId}");
        try
        {
            switch (mode)
            {
                case "agent": return RunAgent(args);
                case "game": return RunGame(args);
                case "suspend": return ForEach(args[1], h => Log($"NtSuspendProcess status=0x{Native.NtSuspendProcess(h):X}"));
                case "resume": return ForEach(args[1], h => Log($"NtResumeProcess status=0x{Native.NtResumeProcess(h):X}"));
                case "minimize": return Minimize(args[1]);
                case "windows": LogWindows(args[1]); return 0;
                case "poke-minimize": return PokeThenMinimize(args[1]);
                case "minimize-resume": return MinimizeWithShortResume(args[1], int.TryParse(Arg(args, "settle"), out var settle) ? settle : 0);
                case "service": return SpikeService.Run();
                default: Log("unknown mode"); return 2;
            }
        }
        catch (Exception ex)
        {
            Log("error " + ex);
            return 1;
        }
    }

    public static void Log(string text)
    {
        lock (typeof(Program)) _log?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {text}");
    }

    private static string Arg(string[] args, string key) =>
        args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..] ?? "";

    // ---------- agent ----------

    private static int RunAgent(string[] args)
    {
        if (args.Contains("spsp"))
        {
            var ok = Native.SetProcessShutdownParameters(0x3FF, Native.SHUTDOWN_NORETRY);
            Log($"SetProcessShutdownParameters(0x3FF, SHUTDOWN_NORETRY) ok={ok} err={(ok ? 0 : Marshal.GetLastWin32Error())}");
        }
        Native.GetProcessShutdownParameters(out var level, out var flags);
        Log($"shutdown level=0x{level:X} flags=0x{flags:X}");
        var resume = Arg(args, "resume");
        int.TryParse(Arg(args, "delay"), out var delay);
        var window = new HiddenWindow(resume, delay);
        Log($"hidden window hwnd=0x{window.Handle:X} visible={Native.IsWindowVisible(window.Handle)}");
        Application.Run();
        return 0;
    }

    private sealed class HiddenWindow : NativeWindow
    {
        private readonly string _resume;
        private readonly int _delay;

        public HiddenWindow(string resume, int delay)
        {
            _resume = resume;
            _delay = delay;
            CreateHandle(new CreateParams { Caption = "EndSessionSpikeAgent", Style = unchecked((int)0x80000000) }); // WS_POPUP
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
            {
                Log($"agent WM_QUERYENDSESSION wParam=0x{m.WParam:X} lParam=0x{(long)m.LParam:X}");
                if (((long)m.LParam & Native.ENDSESSION_CLOSEAPP) != 0)
                    Log("agent ENDSESSION_CLOSEAPP: no resume");
                else if (_resume.Length > 0)
                {
                    if (_delay > 0)
                    {
                        var name = _resume;
                        new Thread(() => { Thread.Sleep(_delay); Log($"agent delayed resume after {_delay} ms"); ResumeAll(name); }) { IsBackground = true }.Start();
                    }
                    else ResumeAll(_resume);
                }
                m.Result = 1;
                Log("agent WM_QUERYENDSESSION return TRUE");
                return;
            }
            if (m.Msg == Native.WM_ENDSESSION)
            {
                Log($"agent WM_ENDSESSION wParam=0x{m.WParam:X} lParam=0x{(long)m.LParam:X}");
                m.Result = 0;
                return;
            }
            base.WndProc(ref m);
        }
    }

    private static void ResumeAll(string names)
    {
        foreach (var name in names.Split(','))
            ForEach(name, h => Log($"NtResumeProcess status=0x{Native.NtResumeProcess(h):X}"));
    }

    private static int ForEach(string name, Action<IntPtr> action)
    {
        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 0) Log($"no process {name}");
        foreach (var p in processes)
        {
            var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, p.Id);
            if (h == IntPtr.Zero) { Log($"{name} pid={p.Id} OpenProcess err={Marshal.GetLastWin32Error()}"); continue; }
            Log($"{name} pid={p.Id}:");
            action(h);
            Native.CloseHandle(h);
        }
        return 0;
    }

    // ---------- game ----------

    private static int RunGame(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new GameForm(args.Contains("borderless"));
        Application.Run(form);
        Log("game exit");
        return 0;
    }

    private sealed class GameForm : Form
    {
        public GameForm(bool borderless)
        {
            Text = $"SpikeGame (pid {Environment.ProcessId})";
            var label = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 28), TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(label);
            if (borderless)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = Screen.PrimaryScreen!.Bounds;
                BackColor = Color.DarkGreen;
                TopMost = false;
            }
            else
            {
                Width = 480;
                Height = 220;
            }
            var start = DateTime.Now;
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) => label.Text = $"{Text}\n{(DateTime.Now - start):hh\\:mm\\:ss}";
            timer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
                Log($"game WM_QUERYENDSESSION wParam=0x{m.WParam:X} lParam=0x{(long)m.LParam:X}");
            base.WndProc(ref m);
            if (m.Msg == Native.WM_QUERYENDSESSION)
                Log($"game WM_QUERYENDSESSION return 0x{m.Result:X}");
            if (m.Msg == Native.WM_ENDSESSION)
                Log($"game WM_ENDSESSION wParam=0x{m.WParam:X} lParam=0x{(long)m.LParam:X}");
        }
    }

    // ---------- windows ----------

    private static List<IntPtr> TopLevelWindows(HashSet<int> pids)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains((int)pid)) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string Describe(IntPtr hwnd)
    {
        var title = new StringBuilder(256);
        Native.GetWindowText(hwnd, title, title.Capacity);
        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        Native.GetWindowRect(hwnd, out var r);
        return $"hwnd=0x{hwnd:X} class={cls} title=\"{title}\" visible={Native.IsWindowVisible(hwnd)} iconic={Native.IsIconic(hwnd)} hung={Native.IsHungAppWindow(hwnd)} rect=({r.Left},{r.Top},{r.Right},{r.Bottom})";
    }

    private static void LogWindows(string name)
    {
        var pids = Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();
        Log($"pids of {name}: {string.Join(',', pids)}");
        foreach (var hwnd in TopLevelWindows(pids)) Log("  " + Describe(hwnd));
        var fg = Native.GetForegroundWindow();
        Log("foreground: " + Describe(fg));
    }

    private static int Minimize(string name)
    {
        var pids = Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();
        foreach (var hwnd in TopLevelWindows(pids))
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) continue;
            Log("before " + Describe(hwnd));
            var ok = Native.ShowWindowAsync(hwnd, Native.SW_FORCEMINIMIZE);
            Log($"ShowWindowAsync(SW_FORCEMINIMIZE) ok={ok} err={(ok ? 0 : Marshal.GetLastWin32Error())}");
        }
        Thread.Sleep(1500);
        LogWindows(name);
        return 0;
    }

    // Posts WM_NULL to each visible window of <name>, waits 6 s until Windows can mark it as hung,
    // then calls the SW_FORCEMINIMIZE path.
    private static int PokeThenMinimize(string name)
    {
        var pids = Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();
        foreach (var hwnd in TopLevelWindows(pids).Where(Native.IsWindowVisible))
            Log($"PostMessage(WM_NULL) ok={Native.PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero)} {Describe(hwnd)}");
        Thread.Sleep(6000);
        return Minimize(name);
    }

    // Alternative to SW_FORCEMINIMIZE: resume the process for a short time, minimize its windows
    // with SW_MINIMIZE, wait until they are minimized (at most 2 s), and suspend it again.
    private static int MinimizeWithShortResume(string name, int settle)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, p.Id);
            if (h == IntPtr.Zero) { Log($"{name} pid={p.Id} OpenProcess err={Marshal.GetLastWin32Error()}"); continue; }
            var windows = TopLevelWindows([p.Id]).Where(w => Native.IsWindowVisible(w) && !Native.IsIconic(w)).ToList();
            var sw = Stopwatch.StartNew();
            Log($"{name} pid={p.Id} resume status=0x{Native.NtResumeProcess(h):X}");
            foreach (var w in windows) Log($"ShowWindowAsync(SW_MINIMIZE) {Describe(w)} ok={Native.ShowWindowAsync(w, Native.SW_MINIMIZE)}");
            while (sw.ElapsedMilliseconds < 2000 && windows.Any(w => !Native.IsIconic(w))) Thread.Sleep(10);
            Log($"iconic after {sw.ElapsedMilliseconds} ms; settle {settle} ms");
            if (settle > 0) Thread.Sleep(settle);
            Log($"{name} pid={p.Id} suspend status=0x{Native.NtSuspendProcess(h):X} after {sw.ElapsedMilliseconds} ms, all iconic={windows.All(Native.IsIconic)}");
            Native.CloseHandle(h);
        }
        Thread.Sleep(500);
        LogWindows(name);
        return 0;
    }
}

internal static class SpikeService
{
    private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const int SERVICE_RUNNING = 4, SERVICE_STOP_PENDING = 3, SERVICE_STOPPED = 1;
    private const int ACCEPT_STOP = 1, ACCEPT_SHUTDOWN = 4, ACCEPT_SESSIONCHANGE = 0x80, ACCEPT_PRESHUTDOWN = 0x100;
    private const int CONTROL_STOP = 1, CONTROL_SHUTDOWN = 5, CONTROL_SESSIONCHANGE = 0xE, CONTROL_PRESHUTDOWN = 0xF;

    private static IntPtr _status;
    private static readonly ManualResetEventSlim Stop = new();
    private static Native.HandlerEx? _handler;
    private static Native.ServiceMain? _main;

    public static int Run()
    {
        _main = ServiceMain;
        var table = new[] { new Native.SERVICE_TABLE_ENTRY { lpServiceName = "EndSessionSpike", lpServiceProc = _main }, new Native.SERVICE_TABLE_ENTRY() };
        if (!Native.StartServiceCtrlDispatcher(table))
            Program.Log($"StartServiceCtrlDispatcher err={Marshal.GetLastWin32Error()}");
        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = Handler;
        _status = Native.RegisterServiceCtrlHandlerEx("EndSessionSpike", _handler, IntPtr.Zero);
        Set(SERVICE_RUNNING, ACCEPT_STOP | ACCEPT_SHUTDOWN | ACCEPT_SESSIONCHANGE | ACCEPT_PRESHUTDOWN);
        Program.Log("service running");
        Stop.Wait();
        Set(SERVICE_STOPPED, 0);
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case CONTROL_PRESHUTDOWN:
                Program.Log("service PRESHUTDOWN");
                Set(SERVICE_STOP_PENDING, 0);
                Stop.Set();
                break;
            case CONTROL_SHUTDOWN:
                Program.Log("service SHUTDOWN");
                Set(SERVICE_STOP_PENDING, 0);
                Stop.Set();
                break;
            case CONTROL_STOP:
                Program.Log("service STOP");
                Set(SERVICE_STOP_PENDING, 0);
                Stop.Set();
                break;
            case CONTROL_SESSIONCHANGE:
                var session = Marshal.ReadInt32(eventData, 4); // WTSSESSION_NOTIFICATION: cbSize, dwSessionId
                Program.Log($"service SESSIONCHANGE event={eventType} session={session}");
                break;
        }
        return 0;
    }

    private static void Set(int state, int accepted)
    {
        var s = new Native.SERVICE_STATUS { dwServiceType = SERVICE_WIN32_OWN_PROCESS, dwCurrentState = state, dwControlsAccepted = accepted, dwWaitHint = 5000 };
        Native.SetServiceStatus(_status, ref s);
    }
}

internal static class Native
{
    public const int WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16;
    public const long ENDSESSION_CLOSEAPP = 0x1;
    public const uint SHUTDOWN_NORETRY = 0x1;
    public const int PROCESS_SUSPEND_RESUME = 0x0800;
    public const int SW_FORCEMINIMIZE = 11, SW_MINIMIZE = 6;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessShutdownParameters(uint level, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessShutdownParameters(out uint level, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder s, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

    public delegate void ServiceMain(int argc, IntPtr argv);
    public delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY { public string? lpServiceName; public ServiceMain? lpServiceProc; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS { public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerEx handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetServiceStatus(IntPtr h, ref SERVICE_STATUS status);
}
