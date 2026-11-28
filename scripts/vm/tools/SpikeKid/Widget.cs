using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

// The widget of design D5 (B6): ShowActivated False, WS_EX_NOACTIVATE, MA_NOACTIVATE, Focusable False.
// Positions: the window is at (40, 40), 260 x 150 (device-independent units at 100 % scale = pixels).
// Drag strip: y 40..70. Buttons in a row at y 100: Collapse (x 60), Expand (x 125), Settings (x 190), Request (x 255).
internal static class WidgetApp
{
    public static int Run(string move)
    {
        var app = new System.Windows.Application();
        var w = new Window
        {
            Title = "Spike widget", Left = 40, Top = 40, Width = 260, Height = 150, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowActivated = false, Focusable = false, ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.LightSteelBlue,
        };
        var root = new StackPanel();
        var strip = new Border { Height = 30, Background = System.Windows.Media.Brushes.SteelBlue, Child = new TextBlock { Text = $"drag ({move})", Foreground = System.Windows.Media.Brushes.White } };
        root.Children.Add(strip);
        var status = new TextBlock { Text = "0:42 left", FontSize = 18, Margin = new Thickness(6) };
        root.Children.Add(status);
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        foreach (var name in new[] { "Collapse", "Expand", "Settings", "Request" })
        {
            var b = new System.Windows.Controls.Button { Content = name, Width = 60, Margin = new Thickness(2), Focusable = false };
            b.Click += (_, _) =>
            {
                Program.Log($"button {name} clicked");
                status.Text = $"{name} at {DateTime.Now:HH:mm:ss}";
                if (name == "Collapse") w.Height = 80;
                if (name == "Expand") w.Height = 150;
                if (name == "Settings")
                {
                    var dlg = new Window { Title = "Settings", WindowState = WindowState.Maximized, WindowStyle = WindowStyle.None, Topmost = true, Content = new TextBlock { Text = "Settings (closes in 8 s)", FontSize = 40 } };
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    t.Tick += (_, _) => { t.Stop(); dlg.Close(); };
                    t.Start();
                    dlg.ShowDialog();
                    Program.Log("settings dialog closed");
                }
            };
            row.Children.Add(b);
        }
        root.Children.Add(row);
        w.Content = root;
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            var ex = GetWindowLongPtrW(h, -20);
            SetWindowLongPtrW(h, -20, (IntPtr)((long)ex | 0x08000000L));
            HwndSource.FromHwnd(h).AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x0021) { handled = true; return (IntPtr)3; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                return IntPtr.Zero;
            });
            Program.Log($"widget hwnd=0x{h:X} exstyle=0x{(long)GetWindowLongPtrW(h, -20):X}");
        };
        strip.MouseLeftButtonDown += (_, _) =>
        {
            Program.Log($"drag start ({move})");
            if (move == "dragmove") { w.DragMove(); Program.Log($"DragMove end at {w.Left},{w.Top}"); return; }
            var h = new WindowInteropHelper(w).Handle;
            GetCursorPos(out var start);
            var left = (int)w.Left; var top = (int)w.Top;
            while ((GetAsyncKeyState(0x01) & 0x8000) != 0)
            {
                GetCursorPos(out var p);
                SetWindowPos(h, IntPtr.Zero, left + p.X - start.X, top + p.Y - start.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
                Thread.Sleep(15);
            }
            GetCursorPos(out var e);
            Program.Log($"poll move end at {left + e.X - start.X},{top + e.Y - start.Y}");
        };
        var tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true, Text = "Spike widget" };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Request more time", null, (_, _) => Program.Log("tray item clicked"));
        tray.ContextMenuStrip = menu;
        w.Show();
        app.Run();
        return 0;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out N.POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

// A WebView2 app: a page with a button that calls window.open (no NewWindowRequested handler, so WebView2 opens its
// own popup window). The user data folder is the default (<exe>.WebView2 next to the exe), so two instances share it.
internal static class Wv2
{
    public static int Run(string url)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        var f = new System.Windows.Forms.Form { Text = "WV2 app " + Environment.ProcessId, Width = 700, Height = 500, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = 300, Top = 120 };
        var wv = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
        f.Controls.Add(wv);
        f.Shown += async (_, _) =>
        {
            try
            {
                await wv.EnsureCoreWebView2Async();
                Program.Log($"webview2 browser pid={wv.CoreWebView2.BrowserProcessId} udf={wv.CoreWebView2.Environment.UserDataFolder}");
                if (url == "about:blank")
                    wv.CoreWebView2.NavigateToString("<html><body style='font:24px Segoe UI'><h1>WV2 app</h1><button style='font-size:30px' onclick=\"window.open('about:blank','p','width=500,height=300');\">Open popup</button><input autofocus style='font-size:30px'></body></html>");
                else wv.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) { Program.Log("webview2 " + ex); }
        };
        System.Windows.Forms.Application.Run(f);
        return 0;
    }
}
