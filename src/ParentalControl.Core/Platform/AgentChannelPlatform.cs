using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ParentalControl.Core.Platform;

/// <summary>
/// The Windows calls of the Agent Channel (session-probe-and-agent-channel, design D7): the private named pipe of the
/// service, its client end for the agent, and the checks of the agent on the inherited handle.
/// </summary>
public static class AgentChannelPlatform
{
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint SECURITY_SQOS_PRESENT = 0x00100000;
    private const uint SECURITY_IDENTIFICATION = 0x00010000;
    private const int FILE_TYPE_PIPE = 3;
    private const uint WAIT_TIMEOUT = 0x102;

    /// <summary>The buffer size of each direction of the pipe. A frame of 64 KB fits; a capture goes in parts.</summary>
    public const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Creates the server end of a new Agent Channel and opens its client end (design D7 steps 1 and 2). The pipe has a
    /// random name, one instance, a SYSTEM-only DACL and rejects remote clients. Both handles are not inheritable and
    /// are opened for overlapped I/O (S12). The service binds only the server end to a stream; it gives the client end
    /// to the launch and closes it after the launch.
    /// </summary>
    public static (SafePipeHandle Server, SafeFileHandle Client) CreateChannel()
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
            var name = @"\\.\pipe\ParentalControl.Agent." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var server = CreateNamedPipeW(name,
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1, BufferBytes, BufferBytes, 0, ref attributes);
            if (server.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                server.Dispose();
                throw new Win32Exception(error, "CreateNamedPipeW");
            }

            var client = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, IntPtr.Zero);
            if (client.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                client.Dispose();
                server.Dispose();
                throw new Win32Exception(error, "CreateFileW of the client end");
            }
            return (server, client);
        }
        finally
        {
            LaunchNative.LocalFree(descriptor);
        }
    }

    /// <summary>True if the process of the handle still runs (<c>WaitForSingleObject</c> with no wait).</summary>
    public static bool IsRunning(SafeProcessHandle process)
    {
        if (process.IsInvalid || process.IsClosed) return false;
        var result = WaitForSingleObject(process, 0);
        // WAIT_FAILED: the handle cannot tell. A start of a second agent is worse than a late start, but a handle that
        // fails here fails for good, so the process counts as ended.
        return result == WAIT_TIMEOUT;
    }

    /// <summary>
    /// The check of the agent on its inherited handle (design D7): the handle is a pipe and its server runs in
    /// session 0. Only a guard for the user interface (N2); no security decision depends on it.
    /// </summary>
    /// <param name="handle">The handle value from the command line.</param>
    /// <param name="reason">The reason of a refusal.</param>
    public static bool IsServiceChannel(IntPtr handle, out string? reason)
    {
        reason = null;
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            reason = "no handle";
            return false;
        }
        var type = GetFileType(handle);
        if (type != FILE_TYPE_PIPE)
        {
            reason = $"not a pipe (type {type}, error {Marshal.GetLastWin32Error()})";
            return false;
        }
        if (!GetNamedPipeServerSessionId(handle, out var sessionId))
        {
            reason = $"GetNamedPipeServerSessionId failed ({Marshal.GetLastWin32Error()})";
            return false;
        }
        if (sessionId != 0)
        {
            reason = $"the server runs in session {sessionId}";
            return false;
        }
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref LaunchNative.SECURITY_ATTRIBUTES attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(IntPtr pipe, out uint serverSessionId);
}
