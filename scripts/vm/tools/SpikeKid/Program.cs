using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
// Helpers that run as Kid for the spike of session-probe-and-agent-channel (task group 1). Not part of the product.
// Each mode logs to C:\Users\Public\spike\kid-<mode>-<pid>.log.
//
//   window [title=<t>]                 a plain window (a decoy)
//   hung [after=<s>] [noghost]         a window whose UI thread stops after s seconds (ghost window unless noghost)
//   longtitle                          a window with a title of 10,000 characters and a lone surrogate
//   framewin                           a top-level window of the class ApplicationFrameWindow
//   setparent <child> <host>           puts the top-level window of process <child> into the window of <host>
//   setparent-child <proc> <class> <host>  puts the first child window of <class> below the window of <proc> into <host>
//   noactivate <proc>                  sets WS_EX_NOACTIVATE on the top-level window of <proc>
//   decoys <n> <host>                  starts n decoy windows and puts each into <host>
//   desktop hostile|plain <s> [game=<exe>]   creates a desktop (hostile: only Kid has access), switches to it for s seconds
//   alternate <hide> <show> <total> [hostile|plain]  switches to the desktop for hide s and back for show s, again and again
//   dacl <proc>                        removes SYSTEM from the DACL of the process and its threads
//   cpuload <s>                        High class and one time-critical thread per core plus 4, for s seconds
//   click <x> <y> [n] [ms]  | move <x> <y> | drag <x1> <y1> <x2> <y2>   mouse input with SendInput
//   probeattack <pid>                  tries OpenProcess, OpenThread, OpenProcessToken, suspend and terminate
//   enumwin <pid>                      logs the windows (also message-only) of a process and its children
//   fgwatch <s>                        logs the Foreground Window every 200 ms
//   widget [move=dragmove|poll]        a non-activating WPF widget as design D5 states
//   wv2 [url=<u>]                      a WebView2 app with a button that opens a popup
//   rethread [period=<ms>] [embed=<proc>] [total=<s>]   a game window on a new thread again and again (Rethread.cs)
//   ghostclass [title=<t>]             a foreground window of the class Ghost (Rethread.cs)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static StreamWriter? _log;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void DisableProcessWindowsGhosting();
    public static void Log(string s) { lock (typeof(Program)) _log?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {s}"); }
    private static string Kv(string[] a, string k, string d = "") => a.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase))?[(k.Length + 1)..] ?? d;

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "window";
        Directory.CreateDirectory(@"C:\Users\Public\spike");
        _log = new StreamWriter(new FileStream($@"C:\Users\Public\spike\kid-{mode}-{Environment.ProcessId}.log", FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Log($"start {string.Join(' ', args)} user={Environment.UserName} session={Process.GetCurrentProcess().SessionId}");
        try
        {
            switch (mode)
            {
                case "window": return Forms.Run(Kv(args, "title", "Decoy " + Environment.ProcessId), null);
                case "hung":
                    if (args.Contains("noghost")) { DisableProcessWindowsGhosting(); Log("ghosting disabled"); }
                    Forms.X = int.Parse(Kv(args, "x", "200")); Forms.Y = int.Parse(Kv(args, "y", "150"));
                    return Forms.Run("Hung test", int.Parse(Kv(args, "after", "3")));
                case "longtitle":
                    var t = new StringBuilder();
                    for (var i = 0; t.Length < 10000; i++) t.Append("Title" + i + " ");
                    t.Insert(50, '\uD800'); // a lone high surrogate
                    t.Insert(60, '\u0007');
                    return Forms.Run(t.ToString(0, 10000), null);
                case "framewin": return Win.FrameWindow();
                case "setparent": return Win.SetParentOf(args[1], args[2]);
                case "setparent-child": return Win.SetParentChild(args[1], args[2], args[3]);
                case "noactivate": return Win.NoActivate(args[1]);
                case "decoys": return Win.Decoys(int.Parse(args[1]), args[2]);
                case "desktop": return Desk.Once(args[1], int.Parse(args[2]), Kv(args, "game"));
                case "alternate": return Desk.Alternate(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), args.Length > 4 ? args[4] : "hostile");
                case "dacl": return Attack.Dacl(args[1]);
                case "cpuload": return Attack.CpuLoad(int.Parse(args[1]));
                case "click": return Mouse.Click(int.Parse(args[1]), int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 1, args.Length > 4 ? int.Parse(args[4]) : 500);
                case "move": Mouse.Move(int.Parse(args[1]), int.Parse(args[2])); return 0;
                case "drag": return Mouse.Drag(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
                case "probeattack": return Attack.Probe(int.Parse(args[1]));
                case "enumwin": return Win.EnumOf(int.Parse(args[1]));
                case "fgwatch": return Win.FgWatch(int.Parse(args[1]));
                case "widget": return WidgetApp.Run(Kv(args, "move", "dragmove"));
                case "wv2": return Wv2.Run(Kv(args, "url", "about:blank"));
                case "rethread": return Rethread.Run(int.Parse(Kv(args, "period", "150")), Kv(args, "embed"), int.Parse(Kv(args, "total", "600")));
                case "ghostclass": return Rethread.Ghost(Kv(args, "title", "Ghost game"));
                default: Log("unknown mode"); return 2;
            }
        }
        catch (Exception ex) { Log("error " + ex); return 1; }
    }
}

internal static class N
{
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr dev, IntPtr mode, uint flags, uint access, ref SECURITY_ATTRIBUTES sa);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr dev, IntPtr mode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SwitchDesktop(IntPtr d);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr d);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int rev, out IntPtr sd, out int size);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetSecurityDescriptorDacl(IntPtr sd, out bool present, out IntPtr dacl, out bool def);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern uint SetSecurityInfo(IntPtr h, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr h, int code);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] public static extern bool SetThreadPriority(IntPtr t, int p);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr sd; public bool inherit; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDPAD pad; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDPAD { public long a, b, c, d; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    public static string Cls(IntPtr h) { var b = new char[256]; var n = GetClassNameW(h, b, 256); return new string(b, 0, Math.Max(0, n)); }
    public static string Title(IntPtr h) { var b = new char[256]; var n = GetWindowTextW(h, b, 256); return new string(b, 0, Math.Max(0, n)); }
    public static string ProcName(int pid) { try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; } }

    public static IntPtr MainWindowOf(string proc)
    {
        var pids = Process.GetProcessesByName(proc).Where(p => p.SessionId == Process.GetCurrentProcess().SessionId).Select(p => p.Id).ToHashSet();
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(h) && GetAncestor(h, 3 /*GA_ROOTOWNER*/) == h) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

internal sealed class Forms : System.Windows.Forms.Form
{
    public static int X = 200, Y = 150;
    public static int Run(string title, int? hangAfter)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        var f = new Forms { Text = title, Width = 420, Height = 260, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = X, Top = Y };
        var label = new System.Windows.Forms.Label { Dock = System.Windows.Forms.DockStyle.Fill, Font = new System.Drawing.Font("Segoe UI", 20), Text = title.Length > 40 ? title[..40] : title };
        f.Controls.Add(label);
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        var start = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            label.Text = $"{(title.Length > 30 ? title[..30] : title)}\n{DateTime.Now:HH:mm:ss.f}";
            if (hangAfter is int s && DateTime.UtcNow - start > TimeSpan.FromSeconds(s))
            {
                Program.Log("UI thread stops now");
                Thread.Sleep(Timeout.Infinite);
            }
        };
        timer.Start();
        f.Shown += (_, _) => Program.Log($"window hwnd=0x{f.Handle:X} title.length={title.Length}");
        System.Windows.Forms.Application.Run(f);
        return 0;
    }
}

internal static class Win
{
    public static int SetParentOf(string child, string host)
    {
        var c = N.MainWindowOf(child);
        var h = N.MainWindowOf(host);
        Program.Log($"child {child} hwnd=0x{c:X}, host {host} hwnd=0x{h:X}");
        if (c == IntPtr.Zero || h == IntPtr.Zero) return 1;
        var style = (long)N.GetWindowLongPtrW(c, -16);
        style = (style | 0x40000000L /*WS_CHILD*/) & ~0x80000000L /*WS_POPUP*/ & ~0x00C00000L /*WS_CAPTION*/;
        N.SetWindowLongPtrW(c, -16, (IntPtr)style);
        var prev = N.SetParent(c, h);
        N.MoveWindow(c, 20, 60, 360, 220, true);
        N.ShowWindow(c, 5);
        Program.Log($"SetParent prev=0x{prev:X} err={Marshal.GetLastWin32Error()}");
        return 0;
    }

    public static int SetParentChild(string proc, string cls, string host)
    {
        var top = N.MainWindowOf(proc);
        var h = N.MainWindowOf(host);
        var found = IntPtr.Zero;
        N.EnumChildWindows(top, (w, _) => { if (N.Cls(w) == cls) { found = w; return false; } return true; }, IntPtr.Zero);
        N.GetWindowThreadProcessId(found, out var cpid);
        Program.Log($"{proc} 0x{top:X}: child {cls} 0x{found:X} pid={cpid} {N.ProcName(cpid)}; host {host} 0x{h:X}");
        if (found == IntPtr.Zero || h == IntPtr.Zero) return 1;
        var prev = N.SetParent(found, h);
        N.MoveWindow(found, 20, 60, 400, 250, true);
        N.ShowWindow(found, 5);
        Program.Log($"SetParent prev=0x{prev:X} err={Marshal.GetLastWin32Error()}");
        return 0;
    }

    public static int NoActivate(string proc)
    {
        var h = N.MainWindowOf(proc);
        if (h == IntPtr.Zero) { Program.Log("no window"); return 1; }
        var ex = (long)N.GetWindowLongPtrW(h, -20);
        N.SetWindowLongPtrW(h, -20, (IntPtr)(ex | 0x08000000L));
        Program.Log($"{proc} hwnd=0x{h:X} exstyle 0x{ex:X} -> 0x{(long)N.GetWindowLongPtrW(h, -20):X}");
        return 0;
    }

    public static int Decoys(int n, string host)
    {
        var exe = Environment.ProcessPath!;
        var procs = new List<Process>();
        for (var i = 0; i < n; i++) procs.Add(Process.Start(exe, $"window title=Decoy{i}"));
        Thread.Sleep(4000);
        var h = N.MainWindowOf(host);
        var k = 0;
        foreach (var p in procs)
        {
            p.Refresh();
            var w = p.MainWindowHandle;
            var style = ((long)N.GetWindowLongPtrW(w, -16) | 0x40000000L) & ~0x80000000L & ~0x00C00000L;
            N.SetWindowLongPtrW(w, -16, (IntPtr)style);
            N.SetParent(w, h);
            N.MoveWindow(w, 10 + 30 * k, 60 + 10 * k, 120, 80, true);
            N.ShowWindow(w, 5);
            k++;
        }
        Program.Log($"put {procs.Count} decoys into {host} 0x{h:X}");
        return 0;
    }

    public static int FrameWindow()
    {
        // A WinForms window cannot pick a class name, so register the class here.
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), lpszClassName = "ApplicationFrameWindow", hInstance = Marshal.GetHINSTANCE(typeof(Win).Module) };
        var atom = RegisterClassW(ref wc);
        var h = CreateWindowExW(0, "ApplicationFrameWindow", "Fake frame", 0x10CF0000 /*OVERLAPPEDWINDOW|VISIBLE*/, 300, 200, 500, 350, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Program.Log($"RegisterClass atom={atom} err={Marshal.GetLastWin32Error()} hwnd=0x{h:X}");
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
        return 0;
    }
    private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    private static readonly WndProc Proc = (h, m, w, l) => { if (m == 2) PostQuitMessage(0); return DefWindowProcW(h, m, w, l); };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int c);

    public static int EnumOf(int pid)
    {
        var pids = new HashSet<int> { pid };
        foreach (var p in Process.GetProcesses())
            try { if (ParentOf(p.Id) == pid) pids.Add(p.Id); } catch { }
        Program.Log($"pids: {string.Join(",", pids.Select(p => p + ":" + N.ProcName(p)))}");
        var count = 0;
        N.EnumWindows((h, _) =>
        {
            N.GetWindowThreadProcessId(h, out var wp);
            if (pids.Contains(wp)) { count++; Program.Log($"top-level 0x{h:X} pid={wp} cls={N.Cls(h)} visible={N.IsWindowVisible(h)}"); }
            return true;
        }, IntPtr.Zero);
        var after = IntPtr.Zero;
        var HWND_MESSAGE = (IntPtr)(-3);
        var all = 0;
        while ((after = N.FindWindowExW(HWND_MESSAGE, after, null, null)) != IntPtr.Zero)
        {
            all++;
            N.GetWindowThreadProcessId(after, out var wp);
            if (pids.Contains(wp)) { count++; Program.Log($"message-only 0x{after:X} pid={wp} cls={N.Cls(after)}"); }
        }
        Program.Log($"windows of the pids: {count} (message-only windows seen in total: {all})");
        return 0;
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int c, ref PBI i, int l, out int r);
    [StructLayout(LayoutKind.Sequential)] private struct PBI { public IntPtr a, b, c, d, pid, parent; }
    public static int ParentOf(int pid)
    {
        var h = N.OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return -1;
        var pbi = new PBI();
        NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PBI>(), out _);
        N.CloseHandle(h);
        return (int)pbi.parent;
    }

    public static int FgWatch(int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var last = "";
        while (DateTime.UtcNow < end)
        {
            var fg = N.GetForegroundWindow();
            N.GetWindowThreadProcessId(fg, out var pid);
            var now = $"fg=0x{fg:X} pid={pid} {N.ProcName(pid)} cls={N.Cls(fg)} [{N.Title(fg)}]";
            if (now != last) { Program.Log(now); last = now; }
            Thread.Sleep(100);
        }
        return 0;
    }
}

internal static class Desk
{
    private static string KidSid => System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    private static IntPtr Create(string kind, string name)
    {
        const uint ALL = 0x01FF | 0x000F0000;
        if (kind == "plain") return N.CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, ALL, IntPtr.Zero);
        N.ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{KidSid})", 1, out var sd, out _);
        var sa = new N.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<N.SECURITY_ATTRIBUTES>(), sd = sd };
        return N.CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, ALL, ref sa);
    }

    private static void StartOn(string desk, string exe)
    {
        var si = new N.STARTUPINFO { cb = Marshal.SizeOf<N.STARTUPINFO>(), lpDesktop = @"winsta0\" + desk };
        var ok = N.CreateProcessW(null, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out var pi);
        Program.Log($"start {exe} on {desk} ok={ok} pid={pi.pid} err={Marshal.GetLastWin32Error()}");
    }

    public static int Once(string kind, int seconds, string game)
    {
        var name = kind + "Desk";
        var d = Create(kind, name);
        Program.Log($"CreateDesktop {name} ({kind}) h=0x{d:X} err={Marshal.GetLastWin32Error()}");
        var def = N.OpenDesktopW("Default", 0, false, 0x0100 /*DESKTOP_SWITCHDESKTOP*/);
        if (game.Length > 0) StartOn(name, game);
        Program.Log($"SwitchDesktop to {name} ok={N.SwitchDesktop(d)} err={Marshal.GetLastWin32Error()}");
        Thread.Sleep(seconds * 1000);
        Program.Log($"SwitchDesktop to Default ok={N.SwitchDesktop(def)} err={Marshal.GetLastWin32Error()}");
        return 0;
    }

    public static int Alternate(int hide, int show, int total, string kind)
    {
        var name = kind + "Alt";
        var d = Create(kind, name);
        var def = N.OpenDesktopW("Default", 0, false, 0x0100);
        Program.Log($"CreateDesktop {name} h=0x{d:X} err={Marshal.GetLastWin32Error()}");
        var end = DateTime.UtcNow.AddSeconds(total);
        var n = 0;
        while (DateTime.UtcNow < end)
        {
            var a = N.SwitchDesktop(d); Program.Log($"to {name} ok={a}");
            Thread.Sleep(hide * 1000);
            var b = N.SwitchDesktop(def); Program.Log($"to Default ok={b}");
            Thread.Sleep(show * 1000);
            n++;
        }
        Program.Log($"cycles={n}");
        return 0;
    }
}

internal static class Attack
{
    public static int Dacl(string proc)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        N.ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out var sd, out _);
        N.GetSecurityDescriptorDacl(sd, out _, out var dacl, out _);
        foreach (var p in Process.GetProcessesByName(proc))
        {
            var h = N.OpenProcess(0x00040000 /*WRITE_DAC*/ | 0x1000, false, p.Id);
            var r = N.SetSecurityInfo(h, 6 /*SE_KERNEL_OBJECT*/, 0x4 | 0x80000000 /*DACL | PROTECTED_DACL*/, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            Program.Log($"process {proc} pid={p.Id} open=0x{h:X} SetSecurityInfo={r}");
            N.CloseHandle(h);
            foreach (ProcessThread t in p.Threads)
            {
                var th = N.OpenThread(0x00040000, false, t.Id);
                var tr = N.SetSecurityInfo(th, 6, 0x4 | 0x80000000, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
                if (tr != 0) Program.Log($"thread {t.Id} SetSecurityInfo={tr}");
                N.CloseHandle(th);
            }
            Program.Log($"threads done: {p.Threads.Count}");
        }
        return 0;
    }

    public static int CpuLoad(int seconds)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var n = Environment.ProcessorCount + 4;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var threads = Enumerable.Range(0, n).Select(_ => new Thread(() =>
        {
            N.SetThreadPriority(N.GetCurrentThread(), 15);
            var x = 0L;
            while (DateTime.UtcNow < end) x++;
        })).ToList();
        threads.ForEach(t => t.Start());
        Program.Log($"load threads={n} class={Process.GetCurrentProcess().PriorityClass}");
        threads.ForEach(t => t.Join());
        Program.Log("load end");
        return 0;
    }

    public static int Probe(int pid)
    {
        foreach (var (name, access) in new[] { ("QUERY_LIMITED", 0x1000u), ("TERMINATE", 0x1u), ("SUSPEND_RESUME", 0x800u), ("VM_READ", 0x10u), ("VM_WRITE", 0x20u), ("ALL", 0x1FFFFFu) })
        {
            var h = N.OpenProcess(access, false, pid);
            Program.Log($"OpenProcess {name}: {(h == IntPtr.Zero ? "fail " + Marshal.GetLastWin32Error() : "OK")}");
            if (h != IntPtr.Zero)
            {
                if (name == "TERMINATE") Program.Log($"TerminateProcess={N.TerminateProcess(h, 9)} err={Marshal.GetLastWin32Error()}");
                if (name == "SUSPEND_RESUME") Program.Log($"NtSuspendProcess=0x{N.NtSuspendProcess(h):X}");
                if (name == "QUERY_LIMITED") Program.Log($"OpenProcessToken={N.OpenProcessToken(h, 0x8, out _)} err={Marshal.GetLastWin32Error()}");
                N.CloseHandle(h);
            }
        }
        Program.Log($"NtSuspendProcess(null handle)=0x{N.NtSuspendProcess(IntPtr.Zero):X}");
        try
        {
            foreach (ProcessThread t in Process.GetProcessById(pid).Threads)
            {
                var th = N.OpenThread(0x0800, false, t.Id);
                Program.Log($"OpenThread {t.Id} QUERY_LIMITED: {(th == IntPtr.Zero ? "fail " + Marshal.GetLastWin32Error() : "OK")}");
                if (th != IntPtr.Zero) N.CloseHandle(th);
                break;
            }
        }
        catch (Exception ex) { Program.Log("threads: " + ex.Message); }
        return 0;
    }
}

internal static class Mouse
{
    private static void Send(uint flags, int x = 0, int y = 0)
    {
        var w = N.GetSystemMetrics(0); var h = N.GetSystemMetrics(1);
        var input = new N.INPUT { type = 0 };
        input.u.mi = new N.MOUSEINPUT { dx = x * 65535 / (w - 1), dy = y * 65535 / (h - 1), dwFlags = flags };
        var sent = N.SendInput(1, new[] { input }, Marshal.SizeOf<N.INPUT>());
        if (sent != 1) Program.Log($"SendInput err={Marshal.GetLastWin32Error()}");
    }
    public static void Move(int x, int y) { Send(0x0001 | 0x8000, x, y); Program.Log($"move {x},{y}"); }
    public static int Click(int x, int y, int n, int ms)
    {
        Move(x, y);
        for (var i = 0; i < n; i++) { Send(0x0002); Thread.Sleep(50); Send(0x0004); Program.Log($"click {i}"); Thread.Sleep(ms); }
        return 0;
    }
    public static int Drag(int x1, int y1, int x2, int y2)
    {
        Move(x1, y1);
        Send(0x0002);
        for (var i = 1; i <= 20; i++) { Send(0x0001 | 0x8000, x1 + (x2 - x1) * i / 20, y1 + (y2 - y1) * i / 20); Thread.Sleep(30); }
        Send(0x0004);
        Program.Log($"drag {x1},{y1} -> {x2},{y2}");
        return 0;
    }
}
