using System.Globalization;

namespace ParentalControl.Probe;

/// <summary>
/// The Session Probe (session-probe-and-agent-channel, design D2 to D4). The service starts it as SYSTEM in a session of
/// a Restricted User with one inherited handle: the write end of an anonymous pipe. The probe samples the windows of
/// the input desktop at random times and writes one JSON line per sample to the pipe. It exits when the pipe breaks
/// (the service ended) and when the restore of its thread desktop fails. It writes no file and shows nothing.
/// <para>
/// Usage: <c>ParentalControl.Probe.exe --pipe &lt;handle&gt; [test switches]</c>. The test switches exist only in a
/// build for VM 102 (<c>-p:VmTest=true</c>, compile symbol <c>VM_TEST</c>, design D15); the release build ignores each
/// argument but <c>--pipe</c>. They come only from the service, which takes them from its own environment:
/// <c>crash=&lt;s&gt;</c> ends the process with a crash after s seconds; <c>hang=&lt;s&gt;</c> blocks the sampler after
/// s seconds; <c>gap=&lt;s&gt;</c> waits s more seconds after each 10th sample; <c>stall=&lt;s&gt;</c> stalls each 10th
/// sample for s seconds inside the sample; <c>nohung</c> drops the <c>hung</c> entry.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Design D2: no error dialog of any kind, and DLLs only from System32 and the folder of the exe.
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS | Native.SEM_NOGPFAULTERRORBOX | Native.SEM_NOOPENFILEERRORBOX);
        // Without the safe DLL search, a later LoadLibrary could take a DLL from another folder: exit, the service logs the
        // end and starts the probe again.
        if (!Native.SetDefaultDllDirectories(Native.LOAD_LIBRARY_SEARCH_SYSTEM32 | Native.LOAD_LIBRARY_SEARCH_APPLICATION_DIR))
            return 3;

        var pipeIndex = Array.IndexOf(args, "--pipe");
        if (pipeIndex < 0 || pipeIndex + 1 >= args.Length
            || !long.TryParse(args[pipeIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            return 2;
        var pipe = (IntPtr)value;
        if (Native.GetFileType(pipe) != Native.FILE_TYPE_PIPE)
            return 2;

        var output = new LineWriter(pipe);
#if VM_TEST
        var switches = TestSwitches.Parse(args);
        output.Log($"probe start pid {Environment.ProcessId}{switches.Describe()}", force: true);

        if (switches.CrashAfterSeconds is { } crash)
        {
            var crashThread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(crash));
                Environment.FailFast("Session Probe test switch: crash");
            }) { IsBackground = true };
            crashThread.Start();
        }

        var sampler = new Thread(() => new Sampler(output, switches).Run()) { IsBackground = false, Name = "sampler" };
#else
        output.Log($"probe start pid {Environment.ProcessId}", force: true);
        var sampler = new Thread(() => new Sampler(output).Run()) { IsBackground = false, Name = "sampler" };
#endif
        sampler.SetApartmentState(ApartmentState.MTA);
        sampler.Start();
        sampler.Join();
        return 0;
    }
}

#if VM_TEST
/// <summary>The test switches of the probe (VM_TEST build only). The service passes them only on VM 102.</summary>
internal sealed record TestSwitches(int? CrashAfterSeconds, int? HangAfterSeconds, int? GapSeconds, int? StallSeconds, bool NoHung)
{
    public static TestSwitches Parse(string[] args)
    {
        int? Number(string key)
        {
            var arg = args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal));
            return arg is not null && int.TryParse(arg.AsSpan(key.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n is >= 0 and <= 3600 ? n : null;
        }

        return new TestSwitches(Number("crash"), Number("hang"), Number("gap"), Number("stall"), args.Contains("nohung"));
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (CrashAfterSeconds is { } c) parts.Add($"crash={c}");
        if (HangAfterSeconds is { } h) parts.Add($"hang={h}");
        if (GapSeconds is { } g) parts.Add($"gap={g}");
        if (StallSeconds is { } s) parts.Add($"stall={s}");
        if (NoHung) parts.Add("nohung");
        return parts.Count == 0 ? "" : " test switches: " + string.Join(' ', parts);
    }
}
#endif
