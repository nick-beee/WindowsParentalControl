using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Sessions;

/// <summary>
/// Self-checks of session-probe-and-agent-channel, tasks 3.1 and 3.3. The service runs as LocalSystem, and
/// <see cref="SessionSystemProcess.Start"/> needs <c>SeTcbPrivilege</c>, so <see cref="Run"/> (as the administrator of
/// the SSH session) starts this exe as SYSTEM through a scheduled task in the mode <c>--probe-system-host</c>. That host
/// runs the checks, starts this exe a third time as the SYSTEM child in the console session
/// (<c>--probe-system-child</c>), and writes one line per check to a result file. A failed Debug.Assert stops the run.
/// </summary>
internal static class SessionProbePlatformChecks
{
    private const string HostMode = "--probe-system-host";
    private const string ChildMode = "--probe-system-child";
    private const string ExitMode = "--probe-system-exit";
    private const string TaskName = "ParentalControlProbeSelfCheck";
    private static readonly string ResultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-probe-selfcheck");
    private static readonly string ResultFile = Path.Combine(ResultFolder, "result.txt");

    /// <summary>Handles the two SYSTEM modes. Returns true if the process must exit after it.</summary>
    public static bool HandleMode(string[] args)
    {
        if (args.Length >= 2 && args[0] == HostMode)
        {
            RunHost(args[1]);
            return true;
        }
        if (args.Length >= 1 && args[0] == ExitMode)
            return true;
        if (args.Length >= 2 && args[0] == ChildMode)
        {
            RunChild(args[1]);
            return true;
        }
        return false;
    }

    /// <summary>The main checks: run the SYSTEM host and assert each of its results.</summary>
    public static void Run()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "ParentalControl.Tests.exe");
        Directory.CreateDirectory(ResultFolder);
        File.Delete(ResultFile);
        Schtasks($"/create /tn {TaskName} /tr \"\\\"{exe}\\\" {HostMode} \\\"{ResultFile}\\\"\" /ru SYSTEM /sc once /st 23:59 /f");
        try
        {
            Schtasks($"/run /tn {TaskName}");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(120) && !(File.Exists(ResultFile) && ReadShared(ResultFile).Contains("DONE")))
                Thread.Sleep(500);
        }
        finally
        {
            Schtasks($"/delete /tn {TaskName} /f");
        }

        var lines = File.Exists(ResultFile) ? ReadShared(ResultFile).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        foreach (var line in lines) Console.WriteLine("   probe self-check: " + line);
        Debug.Assert(lines.Contains("DONE"), "The SYSTEM host of the probe self-checks did not finish within 120 s");
        var failed = lines.Where(l => l.StartsWith("FAIL", StringComparison.Ordinal)).ToList();
        Debug.Assert(failed.Count == 0, "Probe self-checks failed: " + string.Join(" | ", failed));
        var passed = lines.Count(l => l.StartsWith("PASS", StringComparison.Ordinal));
        var skipped = lines.Any(l => l.StartsWith("SKIP", StringComparison.Ordinal));
        var onCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        Debug.Assert(!skipped || onCi, "The SessionSystemProcess checks need a console session outside CI");
        Debug.Assert(passed == (skipped ? 14 : 28), $"The probe self-checks should give {(skipped ? 14 : 28)} passes, gave {passed}");
        Console.WriteLine("✅ Session Probe platform (SeDebugPrivilege, ReadSampleEntry, WebView2 creator, SessionSystemProcess) Passed.");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Schtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Debug.Assert(process.ExitCode == 0, $"schtasks {arguments} failed: {output}");
    }

    // ---------- the SYSTEM host ----------

    private static StreamWriter? _out;

    private static void Check(bool ok, string name, string detail = "")
    {
        var line = $"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}";
        _out!.WriteLine(line);
        _out.Flush();
    }

    private static void RunHost(string resultFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        using var writer = new StreamWriter(new FileStream(resultFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        _out = writer;
        try
        {
            Check(WindowsIdentity.GetCurrent().User?.Value == SessionSystemProcess.SystemSid, "host runs as SYSTEM", WindowsIdentity.GetCurrent().User?.Value ?? "");
            ReadSampleEntryChecks();
            DebugPrivilegeMonitorCheck();
            CreatorChecks();
            SystemProcessChecks();
        }
        catch (Exception ex)
        {
            Check(false, "host exception", ex.GetType().Name + ": " + ex.Message.ReplaceLineEndings(" "));
        }
        writer.WriteLine("DONE");
    }

    private static Process StartHidden(string file, string arguments) =>
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })!;

    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    // Task 3.1: SeDebugPrivilege and the outcomes of ReadSampleEntry.
    private static void ReadSampleEntryChecks()
    {
        Check(ProcessControl.EnableDebugPrivilege(out var error), "SeDebugPrivilege is enabled", error ?? "");
        using var target = StartHidden(System32("PING.EXE"), "-n 60 127.0.0.1");
        try
        {
            Thread.Sleep(500);
            target.Refresh();
            var tid = target.Threads[0].Id;
            var session = target.SessionId;

            // The DACL of the process and of each thread denies everybody, also SYSTEM, as a child can do it.
            SetDenyAllDacl(target.Handle, "process");
            foreach (ProcessThread thread in target.Threads)
            {
                var th = TestProbeNative.OpenThread(0x00040000 /* WRITE_DAC */, false, thread.Id);
                if (th == IntPtr.Zero) continue;
                try { SetDenyAllDacl(th, "thread"); }
                finally { TestProbeNative.CloseHandle(th); }
            }

            var read = ProcessControl.ReadSampleEntry(target.Id, tid, session);
            Check(read.Outcome == EntryOutcome.Read && read.ExePath?.EndsWith("PING.EXE", StringComparison.OrdinalIgnoreCase) == true
                  && read.Process.Pid == target.Id, "a process whose DACL denies SYSTEM gives Read", $"{read.Outcome} {read.ExePath} {read.Detail}");

            TestProbeNative.SetDebugPrivilege(false);
            var withoutPrivilege = ProcessControl.ReadSampleEntry(target.Id, tid, session);
            Check(withoutPrivilege.Outcome == EntryOutcome.Unreadable, "without SeDebugPrivilege the same entry is Unreadable",
                $"{withoutPrivilege.Outcome} {withoutPrivilege.Detail}");
            Check(ProcessControl.EnableDebugPrivilege(out error), "SeDebugPrivilege is enabled again", error ?? "");

            var otherSession = ProcessControl.ReadSampleEntry(target.Id, tid, session + 1);
            Check(otherSession.Outcome == EntryOutcome.Unreadable, "a pid of another session gives Unreadable", $"{otherSession.Outcome} {otherSession.Detail}");

            var foreignThread = ProcessControl.ReadSampleEntry(target.Id, TestProbeNative.GetCurrentThreadId(), session);
            Check(foreignThread.Outcome == EntryOutcome.Ended, "a thread of another process gives Ended", $"{foreignThread.Outcome} {foreignThread.Detail}");

            var late = ProcessControl.ReadSampleEntry(target.Id, tid, session, DateTime.UtcNow.AddMinutes(-5));
            Check(late is { Outcome: EntryOutcome.Read, StartAfterSample: true }, "a start time after the sample time is still Read, with the log sign",
                $"{late.Outcome} {late.StartAfterSample}");
        }
        finally
        {
            TryKill(target);
        }

        int endedPid, endedTid;
        using (var shortLived = StartHidden(System32("PING.EXE"), "-n 2 127.0.0.1"))
        {
            Thread.Sleep(300);
            shortLived.Refresh();
            endedPid = shortLived.Id;
            endedTid = shortLived.Threads[0].Id;
            shortLived.WaitForExit();
        }
        Thread.Sleep(300);
        var ended = ProcessControl.ReadSampleEntry(endedPid, endedTid, 0);
        Check(ended.Outcome == EntryOutcome.Ended, "an ended process gives Ended", $"{ended.Outcome} {ended.Detail}");

        // Review S2: the process exited, but this host keeps a handle to its process and to its thread, so both
        // objects stay. The entry must still give Ended, not Unreadable.
        using (var held = StartHidden(System32("PING.EXE"), "-n 2 127.0.0.1"))
        {
            Thread.Sleep(300);
            held.Refresh();
            var heldPid = held.Id;
            var heldTid = held.Threads[0].Id;
            var heldSession = held.SessionId;
            var threadHandle = TestProbeNative.OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, heldTid);
            var processHandle = TestProbeNative.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, heldPid);
            try
            {
                held.WaitForExit();
                Thread.Sleep(300);
                var exited = ProcessControl.ReadSampleEntry(heldPid, heldTid, heldSession);
                Check(threadHandle != IntPtr.Zero && processHandle != IntPtr.Zero && exited.Outcome == EntryOutcome.Ended,
                    "an exited process with its handles still open elsewhere gives Ended",
                    $"{exited.Outcome} {exited.Detail} thread handle {threadHandle != IntPtr.Zero} process handle {processHandle != IntPtr.Zero}");
            }
            finally
            {
                if (threadHandle != IntPtr.Zero) TestProbeNative.CloseHandle(threadHandle);
                if (processHandle != IntPtr.Zero) TestProbeNative.CloseHandle(processHandle);
            }
        }
    }

    // Review S6: the service enables SeDebugPrivilege before the next hosted service starts, not later in ExecuteAsync.
    private static void DebugPrivilegeMonitorCheck()
    {
        TestProbeNative.SetDebugPrivilege(false);
        var before = TestProbeNative.DebugPrivilegeEnabled();
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var outbox = new ParentalControl.Service.ParentAlertOutbox((_, _) => Task.FromResult(true), () => null, logger);
        using var monitor = new ParentalControl.Service.DebugPrivilegeMonitor(logger, outbox);
        var start = monitor.StartAsync(CancellationToken.None);
        var after = TestProbeNative.DebugPrivilegeEnabled();
        Check(!before && after, "DebugPrivilegeMonitor enables SeDebugPrivilege before StartAsync returns", $"before {before}, after {after}");
        start.Wait(TimeSpan.FromSeconds(5));
        monitor.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
        if (!TestProbeNative.DebugPrivilegeEnabled()) TestProbeNative.SetDebugPrivilege(true);
    }

    // Task 3.1: the creator of a trusted WebView2 (a test child stands for msedgewebview2.exe).
    private static void CreatorChecks()
    {
        static bool IsPing(string path) => path.EndsWith(@"\PING.EXE", StringComparison.OrdinalIgnoreCase);

        using var host = StartHidden(System32("cmd.exe"), "/c ping -n 30 127.0.0.1 >nul");
        try
        {
            var child = WaitForChild(host.Id, "PING.EXE");
            var creator = child is { } pid ? ProcessControl.ResolveWebView2Creator(pid, IsPing) : WebView2Creator.NotAttributed;
            Check(creator.Outcome == CreatorOutcome.Read && creator.Process.Pid == host.Id
                  && creator.ExePath?.EndsWith(@"\cmd.exe", StringComparison.OrdinalIgnoreCase) == true,
                "a test child of a test host gives the host as creator", $"{creator.Outcome} {creator.Process.Pid} {creator.ExePath}");

            var self = Environment.ProcessId;
            var twoSteps = child is { } pid2
                ? ProcessControl.ResolveWebView2Creator(pid2, p => IsPing(p) || p.EndsWith(@"\cmd.exe", StringComparison.OrdinalIgnoreCase))
                : WebView2Creator.NotAttributed;
            Check(twoSteps.Outcome == CreatorOutcome.Read && twoSteps.Process.Pid == self, "a chain of two trusted steps gives the next creator",
                $"{twoSteps.Outcome} {twoSteps.Process.Pid}");
        }
        finally
        {
            KillChildren(host.Id);
            TryKill(host);
        }

        using var launcher = StartHidden(System32("cmd.exe"), "/c start \"\" /b ping -n 30 127.0.0.1");
        var orphan = WaitForChild(launcher.Id, "PING.EXE");
        launcher.WaitForExit(10_000);
        Thread.Sleep(300);
        try
        {
            var creator = orphan is { } pid ? ProcessControl.ResolveWebView2Creator(pid, IsPing) : null;
            Check(orphan is not null && launcher.HasExited && creator?.Outcome == CreatorOutcome.NotAttributed,
                "a child whose parent ended gives not attributed", $"{orphan} {launcher.HasExited} {creator?.Outcome}");
        }
        finally
        {
            if (orphan is { } pid)
                try { using var p = Process.GetProcessById(pid); TryKill(p); } catch (ArgumentException) { }
        }
    }

    // Task 3.3: SessionSystemProcess.Start in the console session.
    private static void SystemProcessChecks()
    {
        var session = (int)TestProbeNative.WTSGetActiveConsoleSessionId();
        if (session <= 0)
        {
            // A CI runner can have no console session. The VM run must not take this path (Run asserts it).
            _out!.WriteLine($"SKIP no console session ({session}): the SessionSystemProcess checks did not run");
            _out.Flush();
            return;
        }
        Check(session > 0, "the console session exists", session.ToString());
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", @"C:\Users\Kid\hook.dll");

        var hostErrorMode = TestProbeNative.GetErrorMode();
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var child = SessionSystemProcess.Start(session, Path.Combine(AppContext.BaseDirectory, "ParentalControl.Tests.exe"), ChildMode + " {handle}",
            pipe.ClientSafePipeHandle.DangerousGetHandle());
        pipe.DisposeLocalCopyOfClientHandle();
        Check(child.UserSid == SessionSystemProcess.SystemSid, "the launch record has the SID of SYSTEM", child.UserSid);
        // Code review of group 4: the launch sets the error mode only for the probe and restores the mode of the service.
        Check(TestProbeNative.GetErrorMode() == hostErrorMode, "the launch restores the error mode of the service",
            $"0x{TestProbeNative.GetErrorMode():X} vs 0x{hostErrorMode:X}");

        // The child writes its report and then waits 5 s, so the host can check the running process.
        var report = new Dictionary<string, string>();
        var reader = new StreamReader(pipe, Encoding.UTF8);
        var readTask = Task.Run(() =>
        {
            string? line;
            while ((line = reader.ReadLine()) is not null && line != "end=1")
            {
                var eq = line.IndexOf('=');
                if (eq > 0) report[line[..eq]] = line[(eq + 1)..];
            }
        });
        Check(readTask.Wait(TimeSpan.FromSeconds(30)), "the child writes its report through the inherited handle");

        var priority = TestProbeNative.GetPriorityClass(child.Handle.DangerousGetHandle());
        Check(priority == 0x80, "the process runs at High priority", $"0x{priority:X}");
        var conhosts = Process.GetProcessesByName("conhost").Where(p =>
        {
            using (p) return ProcessControl.QueryParentPid(p.Id) == child.Pid;
        }).Count();
        Check(conhosts == 0, "the process has no conhost.exe child", conhosts.ToString());

        report.TryGetValue("sid", out var sid);
        report.TryGetValue("session", out var childSession);
        report.TryGetValue("privileges", out var privileges);
        report.TryGetValue("hooks", out var hooks);
        report.TryGetValue("diagnostics", out var diagnostics);
        report.TryGetValue("priority", out var childPriority);
        report.TryGetValue("errormode", out var childErrorMode);
        Check(sid == SessionSystemProcess.SystemSid, "the child runs as S-1-5-18", sid ?? "no report");
        Check(childSession == session.ToString(), "the child runs in the console session", $"{childSession} vs {session}");
        Check(privileges == "SeChangeNotifyPrivilege", "the child has only SeChangeNotifyPrivilege", privileges ?? "");
        Check(hooks == "", "the child has no DOTNET_STARTUP_HOOKS", hooks ?? "no report");
        Check(diagnostics == "0", "the child has DOTNET_EnableDiagnostics=0", diagnostics ?? "");
        Check(childPriority == "High", "the child reads its own class as High", childPriority ?? "");
        // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX, inherited before the loader runs.
        Check(uint.TryParse(childErrorMode, out var mode) && (mode & 0x8003) == 0x8003, "the child inherits the error mode without dialogs",
            childErrorMode ?? "no report");
        TestProbeNative.WaitForSingleObject(child.Handle.DangerousGetHandle(), 10_000);

        // A start without an inherited handle (no attribute list) runs and exits with 0.
        using var plain = SessionSystemProcess.Start(session, Path.Combine(AppContext.BaseDirectory, "ParentalControl.Tests.exe"), ExitMode, IntPtr.Zero);
        var waited = TestProbeNative.WaitForSingleObject(plain.Handle.DangerousGetHandle(), 30_000);
        TestProbeNative.GetExitCodeProcess(plain.Handle.DangerousGetHandle(), out var exitCode);
        Check(waited == 0 && exitCode == 0, "a start without an inherited handle runs and exits with 0", $"wait {waited} exit {exitCode}");
    }

    private static int? WaitForChild(int parentPid, string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        for (var i = 0; i < 50; i++)
        {
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    if (ProcessControl.QueryParentPid(p.Id) == parentPid)
                        return p.Id;
            Thread.Sleep(100);
        }
        return null;
    }

    private static void KillChildren(int parentPid)
    {
        foreach (var p in Process.GetProcesses())
            using (p)
                if (ProcessControl.QueryParentPid(p.Id) == parentPid)
                    TryKill(p);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
    }

    private static void SetDenyAllDacl(IntPtr handle, string what)
    {
        if (!TestProbeNative.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P", 1, out var sd, out _))
            throw new Win32Exception();
        try
        {
            if (!TestProbeNative.SetKernelObjectSecurity(handle, 0x4 /* DACL_SECURITY_INFORMATION */, sd))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetKernelObjectSecurity " + what);
        }
        finally
        {
            TestProbeNative.LocalFree(sd);
        }
    }

    // ---------- the SYSTEM child ----------

    private static void RunChild(string handleText)
    {
        var handle = new IntPtr(long.Parse(handleText));
        using var pipe = new AnonymousPipeClientStream(PipeDirection.Out, new Microsoft.Win32.SafeHandles.SafePipeHandle(handle, true));
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine("sid=" + WindowsIdentity.GetCurrent().User?.Value);
        writer.WriteLine("session=" + Process.GetCurrentProcess().SessionId);
        writer.WriteLine("privileges=" + string.Join(",", TestProbeNative.OwnPrivileges()));
        writer.WriteLine("hooks=" + Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"));
        writer.WriteLine("diagnostics=" + Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics"));
        writer.WriteLine("priority=" + Process.GetCurrentProcess().PriorityClass);
        writer.WriteLine("errormode=" + TestProbeNative.GetErrorMode().ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("end=1");
        writer.Flush();
        Thread.Sleep(5000);
    }
}

internal static class TestProbeNative
{
    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern uint GetErrorMode();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, out int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetKernelObjectSecurity(IntPtr handle, int information, IntPtr sd);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeNameW(string? system, ref long luid, StringBuilder name, ref int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    /// <summary>Enables or disables SeDebugPrivilege in the token of this process.</summary>
    public static void SetDebugPrivilege(bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid)) throw new Win32Exception();
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = enable ? 0x2u : 0u };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>True if SeDebugPrivilege is in the token of this process and enabled.</summary>
    public static bool DebugPrivilegeEnabled()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var debug)) return false;
            GetTokenInformation(token, 3 /* TokenPrivileges */, IntPtr.Zero, 0, out var size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 3, buffer, size, out _)) return false;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    // LUID_AND_ATTRIBUTES: an 8-byte LUID and 4 bytes of attributes; SE_PRIVILEGE_ENABLED is 0x2.
                    if (Marshal.ReadInt64(buffer, 4 + i * 12) == debug)
                        return (Marshal.ReadInt32(buffer, 4 + i * 12 + 8) & 0x2) != 0;
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>The names of the privileges in the token of this process (enabled or not).</summary>
    public static List<string> OwnPrivileges()
    {
        var names = new List<string>();
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token)) return ["(no token)"];
        try
        {
            GetTokenInformation(token, 3 /* TokenPrivileges */, IntPtr.Zero, 0, out var size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 3, buffer, size, out _)) return ["(no privileges)"];
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var luid = Marshal.ReadInt64(buffer, 4 + i * 12);
                    var length = 64;
                    var name = new StringBuilder(length);
                    names.Add(LookupPrivilegeNameW(null, ref luid, name, ref length) ? name.ToString() : $"luid {luid}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
        return names;
    }
}
