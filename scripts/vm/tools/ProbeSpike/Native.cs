using System.Runtime.InteropServices;

// P/Invoke declarations of the spike. Not part of the product.
internal static class Native
{
    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr proc, int flags, char[] buf, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr proc, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr proc, int code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessHandleCount(IntPtr h, out int count);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] public static extern bool QueryUnbiasedInterruptTime(out ulong t);
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] public static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerSessionId(IntPtr pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInst, uint outSize, uint inSize, uint timeout, ref SECURITY_ATTRIBUTES sa);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);

    // ---------- advapi32 ----------
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CreateRestrictedToken(IntPtr token, uint flags, int disableCount, IntPtr disable, int deleteCount, IntPtr delete, int restrictCount, SID_AND_ATTRIBUTES[]? restrict, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int impersonation, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetTokenInformation(IntPtr token, int cls, IntPtr info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int rev, out IntPtr sd, out int size);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSidToSidW(string sid, out IntPtr psid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetSecurityDescriptorDacl(IntPtr sd, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUserW(IntPtr token, string? app, System.Text.StringBuilder cmd, ref SECURITY_ATTRIBUTES pa, ref SECURITY_ATTRIBUTES ta,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool LookupPrivilegeValueW([MarshalAs(UnmanagedType.LPWStr)] string? system, [MarshalAs(UnmanagedType.LPWStr)] string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr ret);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);

    // ---------- userenv / wtsapi ----------
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(int session, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int cls, out IntPtr buf, out int bytes);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);

    // ---------- ntdll ----------
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr proc, int cls, ref PROCESS_BASIC_INFORMATION info, int len, out int ret);

    // ---------- user32 ----------
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desk);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(int tid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desk);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr obj, int index, char[] buf, int len, out int needed);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetGUIThreadInfo(int tid, ref GUITHREADINFO info);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, char[] buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, char[] buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hwnd);

    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;
    public const uint GA_PARENT = 1, GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;
    public const uint DUPLICATE_SAME_ACCESS = 2;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, DETACHED_PROCESS = 0x8, CREATE_NO_WINDOW = 0x08000000,
        CREATE_UNICODE_ENVIRONMENT = 0x400, HIGH_PRIORITY_CLASS = 0x80;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const int TokenUser = 1, TokenPrivileges = 3, TokenDefaultDacl = 6, TokenSessionId = 12;
    public const uint DISABLE_MAX_PRIVILEGE = 0x1, WRITE_RESTRICTED = 0x8;

    [StructLayout(LayoutKind.Sequential)] public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] public struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct TOKEN_PRIVILEGES { public int Count; public long Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct TOKEN_DEFAULT_DACL { public IntPtr DefaultDacl; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    public static SECURITY_ATTRIBUTES SystemOnly(bool inherit = false)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)", 1, out var sd, out _))
            throw new System.ComponentModel.Win32Exception();
        return new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = inherit };
    }

    public static string EnablePrivilege(string name, uint attributes = 2)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var tok)) return "OpenProcessToken " + Marshal.GetLastWin32Error();
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return "Lookup " + Marshal.GetLastWin32Error();
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = attributes };
            if (!AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero)) return "Adjust " + Marshal.GetLastWin32Error();
            var err = Marshal.GetLastWin32Error();
            return err == 0 ? "enabled" : "Adjust lasterror " + err;
        }
        finally { CloseHandle(tok); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint GetSecurityInfo(IntPtr h, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr sd);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd, int rev, uint info, out IntPtr s, out int len);
    public static string Sddl(IntPtr h)
    {
        var r = GetSecurityInfo(h, 6, 0x4, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var sd);
        if (r != 0) return "GetSecurityInfo " + r;
        ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, 0x4, out var s, out _);
        var str = Marshal.PtrToStringUni(s) ?? "?";
        LocalFree(s); LocalFree(sd);
        return str;
    }

    public static string TokenUserSid(IntPtr token)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var len);
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buf, len, out _)) return "?";
            var sid = Marshal.ReadIntPtr(buf);
            ConvertSidToStringSidW(sid, out var s);
            var r = Marshal.PtrToStringUni(s) ?? "?";
            LocalFree(s);
            return r;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string TokenPrivilegeCount(IntPtr token)
    {
        GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out var len);
        var buf = Marshal.AllocHGlobal(len);
        try { return GetTokenInformation(token, TokenPrivileges, buf, len, out _) ? Marshal.ReadInt32(buf).ToString() : "?"; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
