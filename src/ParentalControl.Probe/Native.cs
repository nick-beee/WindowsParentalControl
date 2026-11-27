using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ParentalControl.Probe;

/// <summary>
/// The P/Invoke declarations of the Session Probe. The probe references no other project, so it has its own small set.
/// No function here opens a process: the service reads each process itself (design D3b).
/// </summary>
internal static class Native
{
    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(IntPtr handle, byte[] buffer, int length, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDefaultDllDirectories(uint flags);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern uint GetFileType(IntPtr handle);

    // ---------- user32 ----------
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(int threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUserObjectInformationW(IntPtr obj, int index, char[] buffer, int length, out int needed);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(int threadId, ref GUITHREADINFO info);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, char[] buffer, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, char[] buffer, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    // Not in the public docs: an export of user32.dll. Task 4.1 proves it on the VM. Without it, the service falls back
    // to the last category of the session (design D4).
    [DllImport("user32.dll")]
    public static extern IntPtr HungWindowFromGhostWindow(IntPtr ghost);

    public const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;
    public const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200, LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;
    public const uint GA_PARENT = 1, GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;
    public const uint FILE_TYPE_PIPE = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
}
