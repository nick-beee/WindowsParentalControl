using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ParentalControl.Core.Platform;

/// <summary>
/// The Windows calls of the service for the Session Probe (session-probe-and-agent-channel, design D2, D3, S20): the
/// anonymous pipe, the blocking read, the priority of the reader thread, the unbiased clock, and the session facts that
/// classify the end of a probe.
/// </summary>
public static class SessionProbePlatform
{
    private const int ErrorBrokenPipe = 109;
    private const int ErrorMoreData = 234;
    private const int ThreadPriorityTimeCritical = 15;

    /// <summary>
    /// Creates the anonymous pipe of one probe with a SYSTEM-only security descriptor (N1). Both ends are not
    /// inheritable; the launch duplicates the write end as inheritable only for the call (D2 step 5).
    /// </summary>
    public static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe()
    {
        if (!LaunchNative.ConvertStringSecurityDescriptorToSecurityDescriptorW(LaunchNative.SystemOnlySddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SYSTEM-only descriptor");
        try
        {
            var attributes = new LaunchNative.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<LaunchNative.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = false,
            };
            if (!CreatePipe(out var read, out var write, ref attributes, 64 * 1024))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe");
            return (read, write);
        }
        finally
        {
            LaunchNative.LocalFree(descriptor);
        }
    }

    /// <summary>A blocking read of the pipe. Returns 0 when the pipe broke (the probe ended).</summary>
    public static int Read(SafeFileHandle pipe, byte[] buffer)
    {
        if (ReadFile(pipe, buffer, buffer.Length, out var read, IntPtr.Zero))
            return read;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorMoreData) return read;
        if (error == ErrorBrokenPipe) return 0;
        throw new Win32Exception(error, "ReadFile of the probe pipe");
    }

    /// <summary>Sets the calling thread to <c>THREAD_PRIORITY_TIME_CRITICAL</c> (design D3, N19).</summary>
    public static bool SetCurrentThreadTimeCritical() => SetThreadPriority(GetCurrentThread(), ThreadPriorityTimeCritical);

    /// <summary>The unbiased interrupt time: it does not include sleep or hibernation (S20).</summary>
    public static TimeSpan UnbiasedNow()
    {
        QueryUnbiasedInterruptTime(out var value);
        return TimeSpan.FromTicks((long)value);
    }

    /// <summary>The logon time of the session (<c>WTSSessionInfo</c>), as a FILETIME value, or null if the query
    /// fails. The service keys the sign-out state by the session ID and this value (design D2).</summary>
    public static long? QueryLogonTime(int sessionId)
    {
        // WTSINFOW: State, SessionId, 6 counters (32 bytes), WinStationName[32], Domain[17], UserName[21] WCHARs (ends at
        // 172), then LARGE_INTEGERs aligned to 8: ConnectTime 176, DisconnectTime 184, LastInputTime 192, LogonTime 200.
        const int sessionIdOffset = 4, logonTimeOffset = 200;
        if (!NativeMethods.WTSQuerySessionInformationW(NativeMethods.WTS_CURRENT_SERVER_HANDLE, sessionId,
                NativeMethods.WTS_INFO_CLASS.WTSSessionInfo, out var buffer, out var bytes))
            return null;
        try
        {
            if (buffer == IntPtr.Zero || bytes < logonTimeOffset + 8 || Marshal.ReadInt32(buffer, sessionIdOffset) != sessionId)
                return null;
            return Marshal.ReadInt64(buffer, logonTimeOffset);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>The connect state of the session (<c>WTSConnectState</c>), or null if the query fails.</summary>
    public static int? QueryConnectState(int sessionId)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(NativeMethods.WTS_CURRENT_SERVER_HANDLE, sessionId,
                NativeMethods.WTS_INFO_CLASS.WTSConnectState, out var buffer, out var bytes))
            return null;
        try
        {
            return buffer == IntPtr.Zero || bytes < 4 ? null : Marshal.ReadInt32(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>True if the connect state of the session is <c>WTSActive</c> (N23).</summary>
    public static bool IsActive(int sessionId) =>
        QueryConnectState(sessionId) == (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive;

    /// <summary>True if <c>WTSEnumerateSessionsW</c> lists the session.</summary>
    public static bool IsListed(int sessionId)
    {
        if (!NativeMethods.WTSEnumerateSessionsW(NativeMethods.WTS_CURRENT_SERVER_HANDLE, 0, 1, out var info, out var count))
            return true; // the enumeration failed: never call a session gone on a failed call
        try
        {
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(info + i * size).SessionID == sessionId)
                    return true;
            }
            return false;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(info);
        }
    }

    /// <summary>True if the query of the session fails (the first half of the gone test of design D2).</summary>
    public static bool QueryFails(int sessionId) => QueryConnectState(sessionId) is null;

    /// <summary>
    /// The processes of an earlier service run: each process whose exe path is <paramref name="exePath"/> and whose token
    /// user is SYSTEM (N5). Also gives the processes with that path and another user, for the log.
    /// </summary>
    public static (List<(int Pid, DateTime StartUtc)> System, List<(int Pid, string? UserSid)> Others) FindLeftovers(string exePath)
    {
        var system = new List<(int, DateTime)>();
        var others = new List<(int, string?)>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (!string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(exePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                var (path, start) = ProcessControl.QueryPathAndStart(process.Id);
                if (path is null || start is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                var (_, userSid) = TcpConnectionOwner.ReadProcess(process.Id);
                if (userSid == SessionSystemProcess.SystemSid)
                    system.Add((process.Id, start.Value));
                else
                    others.Add((process.Id, userSid));
            }
        }
        return (system, others);
    }

    /// <summary>Ends the process of the kept handle (the probe, never a program of the child).</summary>
    public static bool Terminate(SafeProcessHandle process, uint exitCode) => TerminateProcess(process, exitCode);

    /// <summary>The exit code of an ended process, or null.</summary>
    public static int? ExitCode(SafeProcessHandle process) => GetExitCodeProcess(process, out var code) ? code : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref LaunchNative.SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int length, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);
}
