using System.Runtime.InteropServices;

// Test modes for task 4.6 of session-probe-and-agent-channel. Not part of the product.
//
//   rethread [period=<ms>] [embed=<proc>] [total=<s>]
//       A game window that the program creates again on a new thread every <period> ms (default 150). The program
//       ends the old thread after the new window shows. Without embed, the new window takes the foreground.
//       With embed, the new window goes into the main window of <proc> (for example notepad) as a child window.
//       Scenarios "Game window on a new thread before each read" and "Embedded game window on a new thread before
//       each read".
//   ghostclass [title=<t>]
//       A top-level window of the window class "Ghost" that takes the foreground. Scenario "Fake ghost window".
internal static class Rethread
{
    private sealed class Slot
    {
        public System.Windows.Forms.Form? Form;
        public Thread? Thread;
        public readonly ManualResetEventSlim Shown = new();
    }

    public static int Run(int periodMs, string embed, int totalSeconds)
    {
        var host = string.IsNullOrEmpty(embed) ? IntPtr.Zero : N.MainWindowOf(embed);
        if (!string.IsNullOrEmpty(embed) && host == IntPtr.Zero) { Program.Log($"no window of {embed}"); return 1; }
        Program.Log($"rethread period={periodMs} embed={embed} host=0x{host:X} total={totalSeconds}");
        Slot? old = null;
        var n = 0;
        if (host == IntPtr.Zero)
        {
            // A program that a scheduled task starts cannot take the foreground. Wait for a click on the first window;
            // then this process owns the foreground and each new window can take it.
            old = Start(host, ++n);
            old.Shown.Wait(5000);
            var wait = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < wait)
            {
                N.GetWindowThreadProcessId(N.GetForegroundWindow(), out var fgPid);
                if (fgPid == Environment.ProcessId) break;
                Thread.Sleep(200);
            }
            Program.Log("first window has the foreground; loop starts");
        }
        var end = DateTime.UtcNow.AddSeconds(totalSeconds);
        while (DateTime.UtcNow < end)
        {
            var slot = Start(host, ++n);
            if (!slot.Shown.Wait(5000)) Program.Log($"window {n} did not show");
            if (old != null)
            {
                try { old.Form!.BeginInvoke(new Action(() => old.Form!.Close())); } catch (Exception ex) { Program.Log("close " + ex.Message); }
                old.Thread!.Join(5000);
            }
            if (n % 100 == 0) Program.Log($"window {n} tid={slot.Form?.Tag}");
            old = slot;
            Thread.Sleep(periodMs);
        }
        try { old?.Form!.BeginInvoke(new Action(() => old.Form!.Close())); } catch { }
        Program.Log($"end after {n} windows");
        return 0;
    }

    private static Slot Start(IntPtr host, int n)
    {
        var slot = new Slot();
        var t = new Thread(() =>
        {
            var f = new System.Windows.Forms.Form
            {
                Text = "Rethread game " + n, Width = 400, Height = 250,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = 240, Top = 160,
                Tag = GetCurrentThreadId(),
            };
            f.Controls.Add(new System.Windows.Forms.Label
            {
                Dock = System.Windows.Forms.DockStyle.Fill, Font = new System.Drawing.Font("Segoe UI", 20),
                Text = $"Game window {n}\n{DateTime.Now:HH:mm:ss.f}",
            });
            f.Shown += (_, _) =>
            {
                if (host != IntPtr.Zero)
                {
                    var style = (long)N.GetWindowLongPtrW(f.Handle, -16);
                    style = (style | 0x40000000L /*WS_CHILD*/) & ~0x80000000L /*WS_POPUP*/ & ~0x00C00000L /*WS_CAPTION*/;
                    N.SetWindowLongPtrW(f.Handle, -16, (IntPtr)style);
                    N.SetParent(f.Handle, host);
                    N.MoveWindow(f.Handle, 20, 60, 360, 220, true);
                    N.ShowWindow(f.Handle, 5);
                }
                else
                {
                    SetForegroundWindow(f.Handle);
                    f.Activate();
                }
                slot.Shown.Set();
            };
            slot.Form = f;
            System.Windows.Forms.Application.Run(f);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        slot.Thread = t;
        t.Start();
        return slot;
    }

    // A raw Win32 window, because WinForms cannot set the class name.
    public static int Ghost(string title)
    {
        WndProc proc = DefWindowProcW;
        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = (IntPtr)6, // COLOR_WINDOW + 1
            lpszClassName = "Ghost",
        };
        var atom = RegisterClassExW(ref wc);
        Program.Log($"RegisterClassExW Ghost atom={atom} err={Marshal.GetLastWin32Error()}");
        if (atom == 0) return 1;
        var h = CreateWindowExW(0, "Ghost", title, 0x10CF0000 /*WS_OVERLAPPEDWINDOW | WS_VISIBLE*/, 260, 180, 420, 260,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Program.Log($"window hwnd=0x{h:X} class={N.Cls(h)}");
        if (h == IntPtr.Zero) return 1;
        SetForegroundWindow(h);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        GC.KeepAlive(proc);
        return 0;
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public N.POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
}
