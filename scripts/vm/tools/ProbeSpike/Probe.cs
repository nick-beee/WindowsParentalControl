using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Native;

// The sampler of the spike, as design D4 of session-probe-and-agent-channel states. It runs as SYSTEM in the session
// of the child, writes one JSON line per sample to the inherited pipe handle, and exits when the pipe breaks.
// Spike-only fields (not in the design): u (unbiased ms), cls (class of fg), vis (windows visited), dur (ms of the
// sample), cms (ms of WindowFromPoint), steps/chainProcs of the cursor chain, deskErr (error codes).
// Test switches: hang=<s> blocks the sampler after s seconds; failfast=<s>; throw=<s>; fixed=<ms> fixed interval.
internal static class Probe
{
    private static IntPtr _pipe;
    private static readonly object WriteGate = new();
    private static long _lastLog;

    public static int Run(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
        var dllOk = SetDefaultDllDirectories(0x00000800 | 0x00000200); // SYSTEM32 | APPLICATION_DIR
        _pipe = (IntPtr)long.Parse(Program.Arg(args, "--pipe"));
        var hang = int.TryParse(Program.Kv(args, "hang"), out var h) ? h : -1;
        var failfast = int.TryParse(Program.Kv(args, "failfast"), out var f) ? f : -1;
        var throwAt = int.TryParse(Program.Kv(args, "throw"), out var th) ? th : -1;
        var fixedMs = int.TryParse(Program.Kv(args, "fixed"), out var fx) ? fx : -1;
        var started = Environment.TickCount64;

        Write(new Dictionary<string, object?>
        {
            ["log"] = $"probe start pid={Environment.ProcessId} dllDirs={dllOk} hooksEnv={Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") ?? "-"} " +
                      $"diag={Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") ?? "-"} prio={System.Diagnostics.Process.GetCurrentProcess().PriorityClass}"
        });

        if (failfast >= 0) new Thread(() => { Thread.Sleep(failfast * 1000); Environment.FailFast("spike failfast"); }) { IsBackground = true }.Start();
        if (throwAt >= 0) new Thread(() => { Thread.Sleep(throwAt * 1000); throw new InvalidOperationException("spike throw"); }) { IsBackground = true }.Start();

        var sampler = new Thread(() => SampleLoop(started, hang, fixedMs)) { IsBackground = false };
        sampler.SetApartmentState(ApartmentState.MTA);
        sampler.Start();
        sampler.Join();
        return 0;
    }

    private static void SampleLoop(long started, int hang, int fixedMs)
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        var original = GetThreadDesktop(GetCurrentThreadId());
        while (true)
        {
            double wait = fixedMs > 0 ? fixedMs / 1000.0 : 0.2 + Exponential(4.8);
            if (wait > 10) wait = 10;
            Thread.Sleep(TimeSpan.FromSeconds(wait));
            if (hang >= 0 && Environment.TickCount64 - started > hang * 1000L)
            {
                Write(new Dictionary<string, object?> { ["log"] = "hang switch: sampler blocks now" });
                Thread.Sleep(Timeout.Infinite);
            }
            var line = Sample(original);
            if (!Write(line)) Environment.Exit(4); // the pipe broke: the service ended
        }
    }

    private static double Exponential(double mean)
    {
        var u = (RandomNumberGenerator.GetInt32(1, int.MaxValue)) / (double)int.MaxValue;
        return -mean * Math.Log(u);
    }

    private static Dictionary<string, object?> Sample(IntPtr original)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = new Dictionary<string, object?> { ["t"] = (long)GetTickCount64() };
        QueryUnbiasedInterruptTime(out var unbiased);
        d["u"] = (long)(unbiased / 10000);
        var desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desk == IntPtr.Zero)
        {
            d["blind"] = "desktop";
            d["deskErr"] = "OpenInputDesktop:" + Marshal.GetLastWin32Error();
            return d;
        }
        try
        {
            if (!SetThreadDesktop(desk))
            {
                d["blind"] = "desktop";
                d["deskErr"] = "SetThreadDesktop:" + Marshal.GetLastWin32Error();
                d["desktop"] = Clean(DeskName(desk), 64);
                return d;
            }
            d["desktop"] = Clean(DeskName(desk), 64);
            Fill(d);
        }
        finally
        {
            if (!SetThreadDesktop(original))
            {
                Write(new Dictionary<string, object?> { ["log"] = "restore of the thread desktop failed " + Marshal.GetLastWin32Error() });
                Environment.Exit(3);
            }
            CloseDesktop(desk);
            d["dur"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        }
        return d;
    }

    private static void Fill(Dictionary<string, object?> d)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) { d["window"] = false; return; }
        var tid = GetWindowThreadProcessId(fg, out var pid);
        if (tid == 0)
        {
            fg = GetForegroundWindow();
            tid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            if (fg == IntPtr.Zero) { d["window"] = false; return; }
            if (tid == 0) { d["blind"] = "window"; return; }
        }
        d["fg"] = Entry(fg, tid, pid);
        d["cls"] = Clean(ClassName(fg), 64);
        d["hung"] = IsHungAppWindow(fg);
        var seen = new HashSet<int> { pid };

        // Step 4: focus.
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref gti) && gti.hwndFocus != IntPtr.Zero)
        {
            var ft = GetWindowThreadProcessId(gti.hwndFocus, out var fpid);
            if (ft != 0 && fpid != pid) { d["focus"] = Entry(gti.hwndFocus, ft, fpid); seen.Add(fpid); }
        }

        // Step 5: children of other processes.
        var children = new List<object>();
        var childPids = new HashSet<int>();
        var visited = 0;
        var tooMany = false;
        EnumChildWindows(fg, (w, _) =>
        {
            if (++visited > 4096) { tooMany = true; return false; }
            if (!IsWindowVisible(w) || !GetWindowRect(w, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
            var ct = GetWindowThreadProcessId(w, out var cpid);
            if (ct == 0 || cpid == pid || childPids.Contains(cpid)) return true;
            childPids.Add(cpid);
            if (childPids.Count > 8) { tooMany = true; return false; }
            children.Add(Entry(w, ct, cpid));
            return true;
        }, IntPtr.Zero);
        d["vis"] = visited;
        if (tooMany) { d.Remove("fg"); d.Remove("focus"); d["blind"] = "children"; return; }
        if (children.Count > 0) d["children"] = children;
        foreach (var c in childPids) seen.Add(c);

        // Step 6: the chain under the cursor.
        if (GetCursorPos(out var pt))
        {
            var cw = System.Diagnostics.Stopwatch.StartNew();
            var hit = WindowFromPoint(pt);
            d["cms"] = Math.Round(cw.Elapsed.TotalMilliseconds, 1);
            if (hit != IntPtr.Zero)
            {
                var root = GetAncestor(hit, GA_ROOT);
                if (root == IntPtr.Zero) root = hit;
                var chain = new List<IntPtr> { hit };
                if (root != hit) chain.Add(root);
                var up = new List<IntPtr>();
                var cur = hit;
                var steps = 0;
                while (cur != root && steps < 32)
                {
                    cur = GetAncestor(cur, GA_PARENT);
                    steps++;
                    if (cur == IntPtr.Zero || cur == root) break;
                    up.Add(cur);
                }
                chain.AddRange(up);
                var entries = new List<object>();
                var chainProcs = new HashSet<int>();
                foreach (var w in chain)
                {
                    var wt = GetWindowThreadProcessId(w, out var wpid);
                    if (wt == 0) continue;
                    chainProcs.Add(wpid);
                    if (seen.Contains(wpid) || entries.Count >= 8) continue;
                    seen.Add(wpid);
                    entries.Add(Entry(w, wt, wpid));
                }
                var noAct = ((long)GetWindowLongPtrW(root, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0;
                if (entries.Count > 0)
                    d["cursor"] = new Dictionary<string, object?> { ["noActivate"] = noAct, ["entries"] = entries };
                d["steps"] = steps;
                d["chainProcs"] = chainProcs.Count;
                d["curNoAct"] = noAct;
                d["curCls"] = Clean(ClassName(hit), 64);
            }
        }

        // Step 7: title.
        var buf = new char[257];
        var n = GetWindowTextW(fg, buf, buf.Length);
        d["title"] = Clean(new string(buf, 0, Math.Max(0, n)), 256);
    }

    private static Dictionary<string, object?> Entry(IntPtr hwnd, int tid, int pid) =>
        new() { ["hwnd"] = (long)hwnd, ["tid"] = tid, ["pid"] = pid };

    private static string DeskName(IntPtr desk)
    {
        var buf = new char[256];
        return GetUserObjectInformationW(desk, UOI_NAME, buf, buf.Length * 2, out _) ? new string(buf).TrimEnd('\0') : "?";
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        var n = GetClassNameW(hwnd, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    // Cut to max UTF-16 chars; replace each lone surrogate and each control character with U+FFFD.
    public static string Clean(string s, int max)
    {
        if (s.Length > max) s = s[..max];
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
            else if (char.IsSurrogate(c) || char.IsControl(c)) sb.Append('�');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool Write(Dictionary<string, object?> obj)
    {
        if (obj.ContainsKey("log"))
        {
            var now = Environment.TickCount64;
            if (now - _lastLog < 1000 && !((string)obj["log"]!).StartsWith("probe start")) return true;
            _lastLog = now;
            var l = (string)obj["log"]!;
            if (l.Length > 256) obj["log"] = l[..256];
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        lock (WriteGate) return WriteFile(_pipe, line, line.Length, out _, IntPtr.Zero);
    }
}
