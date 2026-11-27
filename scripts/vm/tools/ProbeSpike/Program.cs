// Spike for task group 1 of session-probe-and-agent-channel (VM 102). Not part of the product.
//
//   ProbeSpike.exe host                 the spike service (install with sc create ProbeSpike binPath= "...\ProbeSpike.exe host")
//   ProbeSpike.exe probe --pipe <h>     the sampler (design D4); the service starts it as SYSTEM in the session of the child
//   ProbeSpike.exe client --channel <h> name=<pipe>   task 1.1: reads and writes through the inherited pipe handle
//
// See Host.cs for the config and the log. The client log is C:\Users\Public\spike\client-<pid>.log.
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "";
        switch (mode)
        {
            case "host": ServiceBase.Run(new Host()); return 0;
            case "probe": return Probe.Run(args);
            case "client": return Client(args);
            default: Console.Error.WriteLine("usage: host | probe --pipe <h> | client --channel <h> name=<pipe>"); return 2;
        }
    }

    public static string Arg(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }

    public static string Kv(string[] args, string key) =>
        args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..] ?? "";

    private static int Client(string[] args)
    {
        Directory.CreateDirectory(@"C:\Users\Public\spike");
        using var log = new StreamWriter(@$"C:\Users\Public\spike\client-{Environment.ProcessId}.log", true) { AutoFlush = true };
        void L(string s) => log.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
        try
        {
            var h = (IntPtr)long.Parse(Arg(args, "--channel"));
            L($"client user={Environment.UserName} pid={Environment.ProcessId} handle={h} hooksEnv={Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") ?? "-"} " +
              $"webview2Env={Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? "-"}");
            L($"GetFileType={Native.GetFileType(h)} (3 = FILE_TYPE_PIPE)");
            var ok = Native.GetNamedPipeServerSessionId(h, out var serverSession);
            L($"GetNamedPipeServerSessionId ok={ok} session={serverSession} err={(ok ? 0 : Marshal.GetLastWin32Error())}");
            using var stream = new NamedPipeClientStream(PipeDirection.InOut, true, true, new SafePipeHandle(h, true));
            var reader = new StreamReader(stream);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).Result;
            L($"read [{line}]");
            writer.WriteLine($"hello from {Environment.UserName} pid {Environment.ProcessId}");
            L("wrote reply");
            var name = Kv(args, "name");
            if (name.Length > 0)
            {
                try
                {
                    using var other = new NamedPipeClientStream(".", name.Replace(@"\\.\pipe\", ""), PipeDirection.InOut);
                    other.Connect(2000);
                    L($"open by name {name}: CONNECTED (unexpected)");
                }
                catch (Exception ex) { L($"open by name {name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Thread.Sleep(2000);
            return 0;
        }
        catch (Exception ex) { L("error " + ex); return 1; }
    }
}
