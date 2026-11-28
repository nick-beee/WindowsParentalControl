using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static ParentalControl.Probe.Native;

namespace ParentalControl.Probe;

/// <summary>One window of a sample: the handle and the thread and process that own it.</summary>
internal readonly record struct Entry(IntPtr Hwnd, int Tid, int Pid);

/// <summary>
/// The sampler of the Session Probe (design D4). It runs on one MTA thread at <c>THREAD_PRIORITY_TIME_CRITICAL</c> and
/// creates no window and no hook. Before each sample it waits 0.2 seconds plus an exponential random time with a mean of
/// 4.8 seconds, at most 10 seconds minus the duration of the last sample (at least 0), so two samples are at most about
/// 10 seconds apart. Each sample reads the input desktop:
/// <list type="number">
/// <item><c>OpenInputDesktop</c>; a failure is <c>{ "blind": "desktop" }</c>.</item>
/// <item><c>SetThreadDesktop</c> to it; a failure is <c>{ "blind": "desktop" }</c>.</item>
/// <item><c>GetForegroundWindow</c>, then <c>t</c>; NULL is <c>{ "window": false }</c> with step 6 only; no thread after
/// one retry is <c>{ "blind": "window" }</c>; a ghost window (class <c>Ghost</c>) gives <c>hung</c>.</item>
/// <item>The focus window of the foreground thread, if another process owns it.</item>
/// <item>One visible child window of each other process below <c>fg</c>; more than 8 processes or 4096 windows is
/// <c>{ "blind": "children" }</c>.</item>
/// <item>The window under the cursor and its parent chain up to the top-level window (at most 32 steps, 8 processes),
/// with <c>WS_EX_NOACTIVATE</c> of the top-level window. The desktop window is never on the chain.</item>
/// <item>The title of <c>fg</c>.</item>
/// <item>The restore of the thread desktop, then <c>CloseDesktop</c>. A failed restore ends the probe.</item>
/// </list>
/// <c>dur</c> is the time from the start of step 1 to the end of step 7.
/// </summary>
internal sealed class Sampler
{
    private const double MinWaitSeconds = 0.2;
    private const double MeanRandomSeconds = 4.8;
    private const double MaxWaitSeconds = 10;
    private const int MaxOtherProcesses = 8;
    private const int MaxWindows = 4096;
    private const int MaxCursorSteps = 32;
    private const int MaxCursorEntries = 8;
    private const int MaxTitleChars = 256;
    private const int MaxDesktopChars = 64;

    private readonly LineWriter _output;
    private readonly char[] _text = new char[MaxTitleChars + 1];

#if VM_TEST
    private readonly TestSwitches _switches;

    public Sampler(LineWriter output, TestSwitches switches)
    {
        _output = output;
        _switches = switches;
    }

    private bool NoHung => _switches.NoHung;
#else
    public Sampler(LineWriter output) => _output = output;

    private static bool NoHung => false;
#endif

    public void Run()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        var original = GetThreadDesktop(GetCurrentThreadId());
#if VM_TEST
        var started = Stopwatch.StartNew();
        long count = 0;
#endif
        var lastSampleSeconds = 0.0;
        while (true)
        {
            // The cap counts the time of the last sample, so two samples are at most about 10 seconds apart (D4).
            var wait = MinWaitSeconds + Exponential(MeanRandomSeconds);
            var cap = Math.Max(0, MaxWaitSeconds - lastSampleSeconds);
            if (wait > cap) wait = cap;
#if VM_TEST
            if (_switches.GapSeconds is { } gap && count > 0 && count % 10 == 0) wait += gap;
#endif
            Thread.Sleep(TimeSpan.FromSeconds(wait));

#if VM_TEST
            if (_switches.HangAfterSeconds is { } hang && started.Elapsed.TotalSeconds > hang)
            {
                _output.Log("test switch hang: the sampler blocks now", force: true);
                Thread.Sleep(Timeout.Infinite);
            }
#endif

#if VM_TEST
            count++;
            var stall = _switches.StallSeconds is { } s && count % 10 == 0 ? s : 0;
#else
            const int stall = 0;
#endif
            var sampleWatch = Stopwatch.StartNew();
            var line = Sample(original, stall);
            lastSampleSeconds = sampleWatch.Elapsed.TotalSeconds;
            if (!_output.Write(line))
                Environment.Exit(4); // the pipe broke: the service ended or closed it
        }
    }

    // An exponential random time from a cryptographic source, so the child cannot predict the next sample (N4).
    private static double Exponential(double mean)
    {
        var u = RandomNumberGenerator.GetInt32(1, int.MaxValue) / (double)int.MaxValue;
        return -mean * Math.Log(u);
    }

    private byte[] Sample(IntPtr original, int stallSeconds)
    {
        var watch = Stopwatch.StartNew();
        var sample = new SampleBuilder();

        // Step 1.
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            sample.T = (long)GetTickCount64();
            sample.Blind = "desktop";
            sample.Duration = watch.ElapsedMilliseconds;
            return sample.ToJson();
        }

        try
        {
            // Step 2.
            if (!SetThreadDesktop(desktop))
            {
                sample.T = (long)GetTickCount64();
                sample.Blind = "desktop";
                sample.Desktop = DesktopName(desktop);
            }
            else
            {
                sample.Desktop = DesktopName(desktop);
                Fill(sample, stallSeconds);
            }
            sample.Duration = watch.ElapsedMilliseconds;
        }
        finally
        {
            // Step 8: restore first, then close. CloseDesktop fails while a thread of the process uses the desktop.
            if (!SetThreadDesktop(original))
            {
                _output.Log($"restore of the thread desktop failed: {Marshal.GetLastPInvokeError()}", force: true);
                Environment.Exit(3);
            }
            CloseDesktop(desktop);
        }
        return sample.ToJson();
    }

    private void Fill(SampleBuilder sample, int stallSeconds)
    {
        // Step 3.
        var fg = GetForegroundWindow();
        sample.T = (long)GetTickCount64();
        if (fg == IntPtr.Zero)
        {
            NoWindow(sample);
            return;
        }
        var tid = GetWindowThreadProcessId(fg, out var pid);
        if (tid == 0)
        {
            fg = GetForegroundWindow();
            sample.T = (long)GetTickCount64();
            if (fg == IntPtr.Zero)
            {
                NoWindow(sample);
                return;
            }
            tid = GetWindowThreadProcessId(fg, out pid);
            if (tid == 0)
            {
                sample.Blind = "window";
                return;
            }
        }
        sample.Foreground = new Entry(fg, tid, pid);
        var seen = new HashSet<int> { pid };

        if (!NoHung && ClassName(fg) == "Ghost")
        {
            var hung = HungWindowFromGhostWindow(fg);
            if (hung != IntPtr.Zero && IsHungAppWindow(hung))
            {
                var hungTid = GetWindowThreadProcessId(hung, out var hungPid);
                if (hungTid != 0)
                {
                    sample.Hung = new Entry(hung, hungTid, hungPid);
                    seen.Add(hungPid);
                }
            }
        }

#if VM_TEST
        if (stallSeconds > 0) Thread.Sleep(TimeSpan.FromSeconds(stallSeconds));
#endif

        // Step 4: the focus window, if another process owns it.
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
        {
            var focusTid = GetWindowThreadProcessId(info.hwndFocus, out var focusPid);
            if (focusTid != 0 && focusPid != pid)
            {
                sample.Focus = new Entry(info.hwndFocus, focusTid, focusPid);
                seen.Add(focusPid);
            }
        }

        // Step 5: one visible child window of each other process below fg.
        var children = new List<Entry>();
        var childPids = new HashSet<int>();
        var visited = 0;
        var tooMany = false;
        EnumChildWindows(fg, (window, _) =>
        {
            if (++visited > MaxWindows)
            {
                tooMany = true;
                return false;
            }
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return true;
            var childTid = GetWindowThreadProcessId(window, out var childPid);
            if (childTid == 0 || childPid == pid || !childPids.Add(childPid)) return true;
            if (childPids.Count > MaxOtherProcesses)
            {
                tooMany = true;
                return false;
            }
            children.Add(new Entry(window, childTid, childPid));
            return true;
        }, IntPtr.Zero);
        if (tooMany)
        {
            sample.Foreground = null;
            sample.Focus = null;
            sample.Hung = null;
            sample.Blind = "children";
            return;
        }
        sample.Children = children;
        seen.UnionWith(childPids);

        // Step 6: the window under the cursor and its parent chain.
        Cursor(sample, seen);

        // Step 7: the stored caption of fg (no message to a window of another process).
        var length = GetWindowTextW(fg, _text, _text.Length);
        sample.Title = Clean(new string(_text, 0, Math.Max(0, length)), MaxTitleChars);
    }

    // No Foreground Window: the sample is { "window": false } with step 6. A game with WS_EX_NOACTIVATE that the child
    // plays with the mouse can leave the Foreground Window NULL (final review, item 5).
    private static void NoWindow(SampleBuilder sample)
    {
        sample.NoWindow = true;
        Cursor(sample, []);
    }

    // Step 6: the window under the cursor and its parent chain up to the top-level window, one entry for each process
    // that is not in `seen`, at most 8.
    private static void Cursor(SampleBuilder sample, HashSet<int> seen)
    {
        if (!GetCursorPos(out var point)) return;
        var hit = WindowFromPoint(point);
        if (hit == IntPtr.Zero) return;
        var root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        var chain = new List<IntPtr> { hit };
        if (root != hit)
        {
            chain.Add(root);
            // Walk up to the top-level window, never past it: GA_PARENT of a top-level window gives the desktop window.
            var current = hit;
            for (var steps = 0; steps < MaxCursorSteps; steps++)
            {
                current = GetAncestor(current, GA_PARENT);
                if (current == IntPtr.Zero || current == root) break;
                chain.Add(current);
            }
        }
        var entries = new List<Entry>();
        foreach (var window in chain)
        {
            if (entries.Count >= MaxCursorEntries) break;
            var windowTid = GetWindowThreadProcessId(window, out var windowPid);
            if (windowTid == 0 || !seen.Add(windowPid)) continue;
            entries.Add(new Entry(window, windowTid, windowPid));
        }
        if (entries.Count > 0)
        {
            sample.CursorEntries = entries;
            sample.CursorNoActivate = ((long)GetWindowLongPtrW(root, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0;
        }
    }

    private string DesktopName(IntPtr desktop)
    {
        var buffer = new char[MaxDesktopChars + 1];
        if (!GetUserObjectInformationW(desktop, UOI_NAME, buffer, buffer.Length * 2, out _))
            return "";
        var end = Array.IndexOf(buffer, '\0');
        return Clean(new string(buffer, 0, end < 0 ? buffer.Length : end), MaxDesktopChars);
    }

    private string ClassName(IntPtr hwnd)
    {
        var length = GetClassNameW(hwnd, _text, _text.Length);
        return new string(_text, 0, Math.Max(0, length));
    }

    /// <summary>Cuts the text to <paramref name="max"/> UTF-16 characters and replaces each lone surrogate and each
    /// control character with U+FFFD (design D3). The text comes from programs of the child.</summary>
    public static string Clean(string text, int max)
    {
        if (text.Length > max) text = text[..max];
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.IsControl(c))
                builder.Append('�');
            else
                builder.Append(c);
        }
        return builder.ToString();
    }
}

/// <summary>The fields of one sample, written as one JSON line (design D3).</summary>
internal sealed class SampleBuilder
{
    public long T;
    public long Duration;
    public string? Blind;
    public bool NoWindow;
    public Entry? Foreground;
    public Entry? Focus;
    public Entry? Hung;
    public List<Entry>? Children;
    public List<Entry>? CursorEntries;
    public bool CursorNoActivate;
    public string? Title;
    public string? Desktop;

    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("t", T);
            json.WriteNumber("dur", Duration);
            if (Blind is not null)
                json.WriteString("blind", Blind);
            else if (NoWindow)
            {
                json.WriteBoolean("window", false);
                WriteCursor(json);
            }
            else if (Foreground is { } fg)
            {
                WriteEntry(json, "fg", fg);
                if (Focus is { } focus) WriteEntry(json, "focus", focus);
                if (Children is { Count: > 0 })
                {
                    json.WriteStartArray("children");
                    foreach (var child in Children) WriteEntry(json, null, child);
                    json.WriteEndArray();
                }
                if (Hung is { } hung) WriteEntry(json, "hung", hung);
                WriteCursor(json);
                if (Title is not null) json.WriteString("title", Title);
            }
            if (Desktop is not null) json.WriteString("desktop", Desktop);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteCursor(Utf8JsonWriter json)
    {
        if (CursorEntries is not { Count: > 0 }) return;
        json.WriteStartObject("cursor");
        json.WriteBoolean("noActivate", CursorNoActivate);
        json.WriteStartArray("entries");
        foreach (var entry in CursorEntries) WriteEntry(json, null, entry);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter json, string? name, Entry entry)
    {
        if (name is null) json.WriteStartObject();
        else json.WriteStartObject(name);
        json.WriteNumber("hwnd", (long)entry.Hwnd);
        json.WriteNumber("tid", entry.Tid);
        json.WriteNumber("pid", entry.Pid);
        json.WriteEndObject();
    }
}

/// <summary>
/// Writes the lines of the probe to the inherited pipe. A log line has at most 256 characters, and the writer sends at
/// most one log line per minute (except a forced one: the start and the last line before an exit).
/// </summary>
internal sealed class LineWriter
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);
    private readonly IntPtr _pipe;
    private readonly Lock _gate = new();
    private long _lastLog = long.MinValue;

    public LineWriter(IntPtr pipe) => _pipe = pipe;

    public bool Write(byte[] json)
    {
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        lock (_gate) return WriteFile(_pipe, line, line.Length, out _, IntPtr.Zero);
    }

    public void Log(string text, bool force = false)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!force && _lastLog != long.MinValue && now - _lastLog < (long)LogInterval.TotalMilliseconds) return;
            _lastLog = now;
        }
        var buffer = new ArrayBufferWriter<byte>(300);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("log", Sampler.Clean(text, 256));
            json.WriteEndObject();
        }
        Write(buffer.WrittenSpan.ToArray());
    }
}
