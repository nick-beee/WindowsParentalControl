using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Platform;

public enum ProcessOpResult
{
    Ok,
    /// <summary>The process does not exist, or a new process uses the pid (other start time).</summary>
    NotFound,
    /// <summary>Windows refused the handle or the call (for example a protected anti-cheat process).</summary>
    AccessDenied,
    Failed,
}

/// <summary>
/// Lists the processes of sessions and suspends or resumes a process. The pid and the start time identify
/// a process, because Windows reuses pids.
/// It closes a process only in <see cref="Terminate"/>, and only the block enforcer calls that method,
/// for a Blocked Start. A limit, a break or a schedule end never closes a program.
/// </summary>
public static class ProcessControl
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int MaxResumeCalls = 10;

    /// <summary>
    /// The processes of the given sessions, with the full exe path (null if Windows refuses it),
    /// the start time (UTC) and whether all threads are suspended. A process without a readable start time is left out.
    /// </summary>
    public static List<SessionProcess> ListProcesses(IReadOnlySet<int> sessionIds)
    {
        var list = new List<SessionProcess>();
        if (sessionIds.Count == 0)
            return list;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int sessionId;
                try { sessionId = process.SessionId; }
                catch (Exception) { continue; }
                if (!sessionIds.Contains(sessionId) || process.Id is 0 or 4)
                    continue;

                var (path, start) = QueryPathAndStart(process.Id);
                if (start is null)
                    continue;

                list.Add(new SessionProcess(process.Id, start.Value, path, sessionId, AreAllThreadsSuspended(process)));
            }
        }
        return list;
    }

    /// <summary>The full exe path and the start time (UTC) of a process, or nulls.</summary>
    public static (string? Path, DateTime? StartUtc) QueryPathAndStart(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return (null, null);
        try
        {
            return (QueryPath(handle), QueryStart(handle));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Suspends the process if it still has the expected start time.</summary>
    public static ProcessOpResult Suspend(int pid, DateTime expectedStartUtc, out string detail) =>
        WithVerifiedHandle(pid, expectedStartUtc, NativeMethods.PROCESS_SUSPEND_RESUME, out detail, handle =>
        {
            var status = NativeMethods.NtSuspendProcess(handle);
            return status >= 0 ? (ProcessOpResult.Ok, "")
                : (status == StatusAccessDenied ? ProcessOpResult.AccessDenied : ProcessOpResult.Failed, $"NtSuspendProcess 0x{status:X8}");
        });

    /// <summary>
    /// Resumes the process if it still has the expected start time. The child can resume single threads
    /// and the service then suspends the process again, so a thread can have a suspend count above 1.
    /// For this reason the method calls NtResumeProcess again while a thread is still suspended
    /// (at most 10 calls).
    /// </summary>
    public static ProcessOpResult Resume(int pid, DateTime expectedStartUtc, out string detail) =>
        WithVerifiedHandle(pid, expectedStartUtc, NativeMethods.PROCESS_SUSPEND_RESUME, out detail, handle =>
        {
            for (var i = 0; i < MaxResumeCalls; i++)
            {
                var status = NativeMethods.NtResumeProcess(handle);
                if (status < 0)
                    return (status == StatusAccessDenied ? ProcessOpResult.AccessDenied : ProcessOpResult.Failed, $"NtResumeProcess 0x{status:X8}");
                if (!HasSuspendedThread(pid))
                    break;
            }
            return (ProcessOpResult.Ok, "");
        });

    /// <summary>
    /// Closes the process if it still has the expected start time. This is the only close of a program of the child:
    /// the block enforcer calls it for a Blocked Start, and no other code calls it.
    /// </summary>
    public static ProcessOpResult Terminate(int pid, DateTime expectedStartUtc, out string detail) =>
        WithVerifiedHandle(pid, expectedStartUtc, NativeMethods.PROCESS_TERMINATE, out detail, handle =>
        {
            if (NativeMethods.TerminateProcess(handle, 1))
                return (ProcessOpResult.Ok, "");
            var error = Marshal.GetLastWin32Error();
            return (error == ErrorAccessDenied ? ProcessOpResult.AccessDenied : ProcessOpResult.Failed,
                $"TerminateProcess: {new Win32Exception(error).Message} ({error})");
        });

    /// <summary>The command line of a process, or null. The text is untrusted.</summary>
    public static string? QueryCommandLine(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessCommandLineInformation, buffer, size, out var needed);
                    if (status is NativeMethods.StatusInfoLengthMismatch or NativeMethods.StatusBufferTooSmall or NativeMethods.StatusBufferOverflow)
                    {
                        size = Math.Max(needed, size * 2);
                        continue;
                    }
                    if (status < 0)
                        return null;
                    var text = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buffer);
                    return text.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The pid of the process that created this process, or null. The parent can have ended, and Windows can
    /// reuse its pid: compare the start time of the parent with the start time of the process.
    /// </summary>
    public static int? QueryParentPid(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        var size = Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessBasicInformation, buffer, size, out _);
            if (status < 0)
                return null;
            var info = Marshal.PtrToStructure<NativeMethods.PROCESS_BASIC_INFORMATION>(buffer);
            return (int)info.InheritedFromUniqueProcessId.ToInt64();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            NativeMethods.CloseHandle(handle);
        }
    }

    private static ProcessOpResult WithVerifiedHandle(int pid, DateTime expectedStartUtc, uint access, out string detail,
        Func<IntPtr, (ProcessOpResult, string)> action)
    {
        var handle = NativeMethods.OpenProcess(access | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            detail = $"OpenProcess: {new Win32Exception(error).Message} ({error})";
            return error switch
            {
                ErrorInvalidParameter => ProcessOpResult.NotFound,
                ErrorAccessDenied => ProcessOpResult.AccessDenied,
                _ => ProcessOpResult.Failed,
            };
        }

        try
        {
            if (QueryStart(handle) is not { } start || start != expectedStartUtc)
            {
                detail = "The pid belongs to another process now.";
                return ProcessOpResult.NotFound;
            }

            (var result, detail) = action(handle);
            return result;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? QueryPath(IntPtr handle)
    {
        var buffer = new char[32768];
        var size = buffer.Length;
        return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private static DateTime? QueryStart(IntPtr handle) =>
        NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
            ? DateTime.FromFileTimeUtc(creation)
            : null;

    private static bool AreAllThreadsSuspended(Process process)
    {
        try
        {
            var threads = process.Threads;
            if (threads.Count == 0)
                return false;
            foreach (ProcessThread thread in threads)
            {
                if (thread.ThreadState != System.Diagnostics.ThreadState.Wait || thread.WaitReason != ThreadWaitReason.Suspended)
                    return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasSuspendedThread(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.ThreadState == System.Diagnostics.ThreadState.Wait && thread.WaitReason == ThreadWaitReason.Suspended)
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
