using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;

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

/// <summary>The read of one entry of a Session Probe sample (session-probe-and-agent-channel, design D3b).</summary>
/// <param name="StartAfterSample">The start time is more than 1 s after the converted sample time: only a sign of a
/// clock step for one log line, never a change of the outcome (S14).</param>
public sealed record SampleEntryRead(EntryOutcome Outcome, ProcessIdentity Process, string? ExePath, string Detail, bool StartAfterSample = false);

/// <summary>
/// Lists the processes of sessions and suspends or resumes a process. The pid and the start time identify
/// a process, because Windows reuses pids.
/// It closes a process only in <see cref="Terminate"/>, and only the block enforcer calls that method,
/// for a Blocked Start. A limit, a break or a schedule end never closes a program.
/// </summary>
public static class ProcessControl
{
    /// <summary>The most parent steps from a trusted WebView2 process to its creator (design D5, S16).</summary>
    public const int MaxCreatorSteps = 4;

    /// <summary>
    /// Enables <c>SeDebugPrivilege</c> in the token of the service (design D3b). With it, <c>OpenProcess</c> and
    /// <c>OpenThread</c> grant the access regardless of the DACL that the child sets on its own process. Returns false
    /// with the error if the enable fails.
    /// </summary>
    public static bool EnableDebugPrivilege(out string? error)
    {
        error = null;
        if (!LaunchNative.OpenProcessToken(LaunchNative.GetCurrentProcess(), LaunchNative.TOKEN_ADJUST_PRIVILEGES | LaunchNative.TOKEN_QUERY, out var token))
        {
            error = $"OpenProcessToken: {Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            if (!LaunchNative.LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
            {
                error = $"LookupPrivilegeValueW: {Marshal.GetLastWin32Error()}";
                return false;
            }
            var state = new LaunchNative.TOKEN_PRIVILEGES_ONE { PrivilegeCount = 1, Luid = luid, Attributes = LaunchNative.SE_PRIVILEGE_ENABLED };
            var ok = LaunchNative.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            var code = Marshal.GetLastWin32Error();
            if (!ok || code == LaunchNative.ERROR_NOT_ALL_ASSIGNED)
            {
                error = $"AdjustTokenPrivileges: {code}";
                return false;
            }
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// Reads one entry of a Session Probe sample (design D3b, S14): the thread <paramref name="tid"/> must belong to
    /// <paramref name="pid"/>, and the process must run in <paramref name="sessionId"/>.
    /// <list type="bullet">
    /// <item>Read: the exe path and the start time.</item>
    /// <item>Ended: the thread or the process does not exist (<c>ERROR_INVALID_PARAMETER</c>), it exited but another
    /// handle keeps its object (the exit code is not <c>STILL_ACTIVE</c>), or the thread belongs to another
    /// process.</item>
    /// <item>Unreadable: access denied, a process of another session, or no path or start time.</item>
    /// </list>
    /// No start-time test decides the outcome.
    /// </summary>
    /// <param name="sampleUtc">The sample time converted to UTC, for the clock-step sign only; null for none.</param>
    public static SampleEntryRead ReadSampleEntry(int pid, int tid, int sessionId, DateTime? sampleUtc = null)
    {
        var thread = LaunchNative.OpenThread(LaunchNative.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (thread == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new SampleEntryRead(error == ErrorInvalidParameter ? EntryOutcome.Ended : EntryOutcome.Unreadable, default, null, $"OpenThread {error}");
        }
        int owner;
        bool threadExited;
        try
        {
            threadExited = HasExited(LaunchNative.GetExitCodeThread(thread, out var threadCode), threadCode);
            owner = LaunchNative.GetProcessIdOfThread(thread);
        }
        finally { NativeMethods.CloseHandle(thread); }
        if (threadExited)
            return new SampleEntryRead(EntryOutcome.Ended, default, null, $"thread {tid} exited");
        if (owner != pid)
            return new SampleEntryRead(EntryOutcome.Ended, default, null, $"thread {tid} belongs to process {owner}");

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new SampleEntryRead(error == ErrorInvalidParameter ? EntryOutcome.Ended : EntryOutcome.Unreadable, default, null, $"OpenProcess {error}");
        }
        try
        {
            // An exited process whose object another handle keeps: Ended, not Unreadable (review S2).
            if (HasExited(LaunchNative.GetExitCodeProcess(handle, out var processCode), processCode))
                return new SampleEntryRead(EntryOutcome.Ended, default, null, $"process {pid} exited");
            if (!LaunchNative.ProcessIdToSessionId(pid, out var processSession))
                return new SampleEntryRead(EntryOutcome.Unreadable, default, null, $"ProcessIdToSessionId {Marshal.GetLastWin32Error()}");
            if (processSession != sessionId)
                return new SampleEntryRead(EntryOutcome.Unreadable, default, null, $"process of session {processSession}");
            var path = QueryPath(handle);
            var start = QueryStart(handle);
            if (path is null || start is null)
                return new SampleEntryRead(EntryOutcome.Unreadable, default, null, "no path or start time");
            var late = sampleUtc is { } sample && start.Value > sample.AddSeconds(1);
            return new SampleEntryRead(EntryOutcome.Read, new ProcessIdentity(pid, start.Value), path, "", late);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The creator of a trusted WebView2 process (design D5, S16, S21): the first process of its parent chain that is
    /// not a trusted WebView2, at most <see cref="MaxCreatorSteps"/> steps. Each parent must run, must have started
    /// before its child (else the pid was reused), and must run in the same session. Else the creator is not
    /// attributed; there is no Ended outcome.
    /// </summary>
    /// <param name="isTrustedWebView2">The path test of <see cref="TrustedWebView2"/>.</param>
    public static WebView2Creator ResolveWebView2Creator(int pid, Func<string, bool> isTrustedWebView2)
    {
        var (childPath, childStart) = QueryPathAndStart(pid);
        if (childPath is null || childStart is null || !LaunchNative.ProcessIdToSessionId(pid, out var session))
            return WebView2Creator.NotAttributed;

        var current = pid;
        var currentStart = childStart.Value;
        for (var step = 0; step < MaxCreatorSteps; step++)
        {
            if (QueryParentPid(current) is not { } parent || parent <= 4)
                return WebView2Creator.NotAttributed;
            var (path, start) = QueryPathAndStart(parent);
            if (path is null || start is null || start.Value > currentStart)
                return WebView2Creator.NotAttributed;
            if (!LaunchNative.ProcessIdToSessionId(parent, out var parentSession) || parentSession != session)
                return WebView2Creator.NotAttributed;
            if (!isTrustedWebView2(path))
                return new WebView2Creator(CreatorOutcome.Read, new ProcessIdentity(parent, start.Value), path);
            current = parent;
            currentStart = start.Value;
        }
        return WebView2Creator.NotAttributed;
    }

    // True only if the exit-code call worked and gave another code than STILL_ACTIVE. A failed call decides nothing.
    private static bool HasExited(bool queried, uint exitCode) => queried && exitCode != LaunchNative.STILL_ACTIVE;

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int MaxResumeCalls = 10;

    /// <summary>
    /// The processes of the given sessions, with the full exe path (null if Windows refuses it), the start time (UTC),
    /// whether all threads are suspended, and the CPU time (null if Windows refuses it). A process without a readable start
    /// time is left out.
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

                var (path, start, cpu) = QueryPathStartAndCpu(process.Id);
                if (start is null)
                    continue;

                list.Add(new SessionProcess(process.Id, start.Value, path, sessionId, AreAllThreadsSuspended(process), cpu));
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
        Suspend(pid, expectedStartUtc, out detail, out _);

    /// <summary>
    /// Suspends the process if it still has the expected start time. <paramref name="cpuAfter"/> is the user-mode CPU time
    /// of the process, read on the same handle after the suspension, or null (lock-after-repeated-resume D10).
    /// </summary>
    public static ProcessOpResult Suspend(int pid, DateTime expectedStartUtc, out string detail, out TimeSpan? cpuAfter)
    {
        TimeSpan? cpu = null;
        var result = WithVerifiedHandle(pid, expectedStartUtc, NativeMethods.PROCESS_SUSPEND_RESUME, out detail, handle =>
        {
            var status = NativeMethods.NtSuspendProcess(handle);
            if (status < 0)
                return (status == StatusAccessDenied ? ProcessOpResult.AccessDenied : ProcessOpResult.Failed, $"NtSuspendProcess 0x{status:X8}");
            cpu = QueryCpu(handle);
            return (ProcessOpResult.Ok, "");
        });
        cpuAfter = result == ProcessOpResult.Ok ? cpu : null;
        return result;
    }

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
    /// the block enforcer calls it for a Blocked Start. The only other caller is the Session Probe host, for a leftover
    /// probe of an earlier service run (a SYSTEM process at the probe path, not a program of the child).
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

    /// <summary>The full exe path, the start time (UTC) and the CPU time of a process, from one handle, or nulls.</summary>
    private static (string? Path, DateTime? StartUtc, TimeSpan? Cpu) QueryPathStartAndCpu(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return (null, null, null);
        try
        {
            return (QueryPath(handle), QueryStart(handle), QueryCpu(handle));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The user-mode CPU time of the process, or null. Windows charges it in clock ticks. The kernel time is not used: a
    /// suspended thread cannot run user code, but a kernel APC or a late system call of a suspended process adds kernel
    /// time (lock-after-repeated-resume D10).
    /// </summary>
    private static TimeSpan? QueryCpu(IntPtr handle) =>
        NativeMethods.GetProcessTimes(handle, out _, out _, out _, out var user) && user >= 0
            ? TimeSpan.FromTicks(user)
            : null;

    private static DateTime? QueryStart(IntPtr handle) =>
        NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
            ? DateTime.FromFileTimeUtc(creation)
            : null;

    private static bool AreAllThreadsSuspended(Process process)
    {
        try
        {
            // WaitReason throws for a thread that does not wait, so read it only for a waiting thread.
            var states = process.Threads.Cast<ProcessThread>()
                .Select(t => (t.ThreadState, t.ThreadState == System.Diagnostics.ThreadState.Wait ? t.WaitReason : (ThreadWaitReason?)null))
                .ToList();
            return ThreadSuspension.AllSuspended(states);
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
