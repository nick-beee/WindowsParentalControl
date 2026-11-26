using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Finds the process on this PC that owns the client end of a TCP connection to the web server, and the user and
/// the session of that process (require-admin-sign-in, finding S4). Each failure gives null; the caller then refuses
/// the request (fail closed).
/// </summary>
public static class TcpConnectionOwner
{
    /// <param name="client">The client end, as the server sees it (<c>RemoteIpAddress</c>, <c>RemotePort</c>).</param>
    /// <param name="server">The server end (<c>LocalIpAddress</c>, <c>LocalPort</c>).</param>
    public static ConnectionOwner? Find(IPEndPoint client, IPEndPoint server)
    {
        try
        {
            var match = FindOwner(client, server);
            if (match is null)
                return null;
            var pid = match.Value.Pid;
            var (sessionId, userSid) = ReadProcess(pid);
            if (sessionId is null || userSid is null)
                return null;

            // N3: a session without a user (session 0) has no session user. A session with a user whose SID the
            // service cannot read is not known, and the policy refuses it.
            string? sessionUser = null;
            var sessionUserKnown = true;
            try
            {
                if (SessionManager.GetSessionUsername(sessionId.Value) is { Length: > 0 })
                {
                    sessionUser = SessionManager.GetSessionUserSid(sessionId.Value);
                    sessionUserKnown = sessionUser is not null;
                }
            }
            catch
            {
                sessionUserKnown = false;
            }
            return new ConnectionOwner(pid, sessionId.Value, userSid, sessionUser)
            {
                SessionUserKnown = sessionUserKnown,
                ProcessStartUtc = ReadProcessStartUtc(pid),
                ConnectionCreatedUtc = match.Value.CreatedUtc,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The owner process of the client end: the row of the TCP table whose local end is the client and whose remote
    /// end is the server. Null if there is no such row, or if more than one process matches.
    /// </summary>
    public static int? FindOwnerPid(IPEndPoint client, IPEndPoint server) => FindOwner(client, server)?.Pid;

    /// <summary>
    /// The owner process of the client end and the time when Windows made the connection (null if the table does not
    /// give it). The time lets the policy see a process id that Windows gave to a new process (N2).
    /// </summary>
    public static (int Pid, DateTime? CreatedUtc)? FindOwner(IPEndPoint client, IPEndPoint server)
    {
        var clientAddress = LoginSource.Canonical(client.Address);
        var serverAddress = LoginSource.Canonical(server.Address);
        if (clientAddress.AddressFamily != serverAddress.AddressFamily)
            return null;

        int? found = null;
        DateTime? created = null;
        foreach (var row in ReadTable(clientAddress.AddressFamily))
        {
            if (row.LocalPort != client.Port || row.RemotePort != server.Port)
                continue;
            if (!row.LocalAddress.Equals(clientAddress) || !row.RemoteAddress.Equals(serverAddress))
                continue;
            if (found is not null && found != row.Pid)
                return null;
            found = row.Pid;
            created = row.CreatedUtc;
        }
        return found is null ? null : (found.Value, created);
    }

    private readonly record struct TcpRow(IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort, int Pid, DateTime? CreatedUtc = null);

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const int TCP_TABLE_OWNER_MODULE_CONNECTIONS = 7;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    /// <summary>
    /// Reads the table with the create time of each connection (<c>TCP_TABLE_OWNER_MODULE_CONNECTIONS</c>). If that
    /// fails, it reads the table with the owner process only.
    /// </summary>
    private static List<TcpRow> ReadTable(AddressFamily family)
    {
        var af = family == AddressFamily.InterNetworkV6 ? AF_INET6 : AF_INET;
        try
        {
            return ReadTable(af, TCP_TABLE_OWNER_MODULE_CONNECTIONS, af == AF_INET ? ParseV4Module : ParseV6Module);
        }
        catch
        {
            return ReadTable(af, TCP_TABLE_OWNER_PID_CONNECTIONS, af == AF_INET ? ParseV4 : ParseV6);
        }
    }

    private static List<TcpRow> ReadTable(int af, int tableClass, Func<IntPtr, List<TcpRow>> parse)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, af, tableClass, 0);
                if (result == 0)
                    return parse(buffer);
                if (result != ERROR_INSUFFICIENT_BUFFER)
                    throw new System.ComponentModel.Win32Exception((int)result, "GetExtendedTcpTable failed");
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                size += 4096; // The table can grow between the two calls.
                buffer = Marshal.AllocHGlobal(size);
            }
            throw new InvalidOperationException("GetExtendedTcpTable: the table changed during each try");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    // MIB_TCPTABLE_OWNER_MODULE: DWORD count, padding to 8 bytes, then rows of 160 bytes: state, local address, local
    // port, remote address, remote port, owner pid (6 DWORDs), the create time (LARGE_INTEGER at 24), then 16 ULONGLONGs.
    private static List<TcpRow> ParseV4Module(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var rows = new List<TcpRow>(count);
        var row = table + 8;
        for (var i = 0; i < count; i++, row += 160)
        {
            var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row + 4)));
            var localPort = Port(Marshal.ReadInt32(row + 8));
            var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row + 12)));
            var remotePort = Port(Marshal.ReadInt32(row + 16));
            rows.Add(new TcpRow(local, localPort, remote, remotePort, Marshal.ReadInt32(row + 20), FileTime(Marshal.ReadInt64(row + 24))));
        }
        return rows;
    }

    // MIB_TCP6TABLE_OWNER_MODULE: DWORD count, padding to 8 bytes, then rows of 192 bytes: local address (16 bytes),
    // local scope ID, local port, remote address (16 bytes), remote scope ID, remote port, state, owner pid (56 bytes),
    // the create time (LARGE_INTEGER at 56), then 16 ULONGLONGs.
    private static List<TcpRow> ParseV6Module(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var rows = new List<TcpRow>(count);
        var row = table + 8;
        var bytes = new byte[16];
        for (var i = 0; i < count; i++, row += 192)
        {
            Marshal.Copy(row, bytes, 0, 16);
            var local = new IPAddress(bytes);
            var localPort = Port(Marshal.ReadInt32(row + 20));
            Marshal.Copy(row + 24, bytes, 0, 16);
            var remote = new IPAddress(bytes);
            var remotePort = Port(Marshal.ReadInt32(row + 44));
            rows.Add(new TcpRow(local, localPort, remote, remotePort, Marshal.ReadInt32(row + 52), FileTime(Marshal.ReadInt64(row + 56))));
        }
        return rows;
    }

    /// <summary>A FILETIME in UTC, or null for 0 or a value that is not a valid time.</summary>
    private static DateTime? FileTime(long value)
    {
        if (value <= 0)
            return null;
        try { return DateTime.FromFileTimeUtc(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // MIB_TCPTABLE_OWNER_PID: DWORD count, then rows of 6 DWORDs:
    // state, local address, local port, remote address, remote port, owner pid.
    private static List<TcpRow> ParseV4(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var rows = new List<TcpRow>(count);
        var row = table + 4;
        for (var i = 0; i < count; i++, row += 24)
        {
            var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row + 4)));
            var localPort = Port(Marshal.ReadInt32(row + 8));
            var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row + 12)));
            var remotePort = Port(Marshal.ReadInt32(row + 16));
            rows.Add(new TcpRow(local, localPort, remote, remotePort, Marshal.ReadInt32(row + 20)));
        }
        return rows;
    }

    // MIB_TCP6TABLE_OWNER_PID: DWORD count, then rows: local address (16 bytes), local scope ID, local port,
    // remote address (16 bytes), remote scope ID, remote port, state, owner pid (56 bytes in all).
    private static List<TcpRow> ParseV6(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var rows = new List<TcpRow>(count);
        var row = table + 4;
        var bytes = new byte[16];
        for (var i = 0; i < count; i++, row += 56)
        {
            Marshal.Copy(row, bytes, 0, 16);
            var local = new IPAddress(bytes);
            var localPort = Port(Marshal.ReadInt32(row + 20));
            Marshal.Copy(row + 24, bytes, 0, 16);
            var remote = new IPAddress(bytes);
            var remotePort = Port(Marshal.ReadInt32(row + 44));
            rows.Add(new TcpRow(local, localPort, remote, remotePort, Marshal.ReadInt32(row + 52)));
        }
        return rows;
    }

    // The port is in network byte order in the low 16 bits.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int pid, out int sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>The start time of a process (UTC), or null.</summary>
    public static DateTime? ReadProcessStartUtc(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            return GetProcessTimes(process, out var creation, out _, out _, out _) ? FileTime(creation) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>The session and the user SID of a process, or nulls.</summary>
    public static (int? SessionId, string? UserSid) ReadProcess(int pid)
    {
        int? sessionId = ProcessIdToSessionId(pid, out var session) ? session : null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return (sessionId, null);
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
                return (sessionId, null);
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
                if (length <= 0)
                    return (sessionId, null);
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buffer, length, out _))
                        return (sessionId, null);
                    // TOKEN_USER starts with SID_AND_ATTRIBUTES: a pointer to the SID, then the attributes.
                    var sidPointer = Marshal.ReadIntPtr(buffer);
                    return (sessionId, new SecurityIdentifier(sidPointer).Value);
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
        finally
        {
            CloseHandle(process);
        }
    }
}
