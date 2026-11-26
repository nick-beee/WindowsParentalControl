using System.Runtime.InteropServices;
using System.Security.Principal;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Core.Platform;

public static class SessionManager
{
    public static bool ForceLogoff(int sessionId)
    {
        return NativeMethods.WTSLogoffSession(
            NativeMethods.WTS_CURRENT_SERVER_HANDLE,
            sessionId,
            false);
    }

    public static bool LockSession(int sessionId)
    {
        return NativeMethods.WTSDisconnectSession(
            NativeMethods.WTS_CURRENT_SERVER_HANDLE,
            sessionId,
            false);
    }
    public static int GetActiveConsoleSessionId()
    {
        var id = NativeMethods.WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? -1 : (int)id;
    }


    public static string? GetSessionUsername(int sessionId)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                NativeMethods.WTS_INFO_CLASS.WTSUserName,
                out var buffer,
                out _))
        {
            return null;
        }

        try
        {
            var username = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(username) ? null : username;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public static string? GetSessionDomain(int sessionId)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                NativeMethods.WTS_INFO_CLASS.WTSDomainName,
                out var buffer,
                out _))
        {
            return null;
        }

        try
        {
            var domain = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(domain) ? null : domain;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public static string? GetSessionUserSid(int sessionId)
    {
        var username = GetSessionUsername(sessionId);
        if (username is null) return null;

        var domain = GetSessionDomain(sessionId);
        var fullName = string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";

        try
        {
            var account = new NTAccount(fullName);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sessions with a user, in the state WTSActive or WTSDisconnected. <c>IsDisconnected</c> is the connect state only:
    /// WTSActive does not show a Win+L lock. For the lock state, use <see cref="QueryLockFlag"/>.
    /// </summary>
    public static List<(int SessionId, string Username, string? Sid, bool IsDisconnected)> GetLoggedOnSessions()
    {
        var sessions = new List<(int, string, string?, bool)>();

        if (!NativeMethods.WTSEnumerateSessionsW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                0,
                1,
                out var pSessionInfo,
                out var count))
        {
            return sessions;
        }

        try
        {
            var structSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var current = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(
                    pSessionInfo + i * structSize);

                if (current.State != NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive &&
                    current.State != NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                {
                    continue;
                }

                var username = GetSessionUsername(current.SessionID);
                if (string.IsNullOrEmpty(username))
                    continue;

                var sid = GetSessionUserSid(current.SessionID);
                bool isDisconnected = current.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected;
                sessions.Add((current.SessionID, username, sid, isDisconnected));
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pSessionInfo);
        }

        return sessions;
    }

    public static List<(int SessionId, string Username, string? Sid)> GetActiveSessions()
    {
        return GetLoggedOnSessions()
            .Where(s => !s.IsDisconnected)
            .Select(s => (s.SessionId, s.Username, s.Sid))
            .ToList();
    }

    /// <summary>
    /// Reads the lock flag of a session (<c>WTSSessionInfoEx</c>, <c>SessionFlags</c>). This flag shows a Win+L lock.
    /// <c>RawFlags</c> is the value from Windows, for the log (null if the query failed).
    /// </summary>
    public static (WtsLockFlag Flag, int? RawFlags) QueryLockFlag(int sessionId)
    {
        // WTSINFOEXW: DWORD Level at 0, then the union WTSINFOEX_LEVEL1_W at 8 (it holds a LARGE_INTEGER, so it is
        // aligned to 8 bytes). In WTSINFOEX_LEVEL1_W: SessionId at 0, SessionState at 4, SessionFlags at 8.
        const int levelOffset = 0, sessionIdOffset = 8, sessionStateOffset = 12, sessionFlagsOffset = 16;

        if (!NativeMethods.WTSQuerySessionInformationW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                NativeMethods.WTS_INFO_CLASS.WTSSessionInfoEx,
                out var buffer,
                out var bytes))
        {
            return (WtsLockFlag.Unknown, null);
        }

        try
        {
            if (buffer == IntPtr.Zero || bytes < sessionFlagsOffset + 4)
                return (WtsLockFlag.Unknown, null);

            // A check of the layout: the level must be 1, and the session id and the state must be correct values.
            // If not, the offsets are wrong and the flag is not known.
            var level = Marshal.ReadInt32(buffer, levelOffset);
            var reportedId = Marshal.ReadInt32(buffer, sessionIdOffset);
            var state = Marshal.ReadInt32(buffer, sessionStateOffset);
            var rawFlags = Marshal.ReadInt32(buffer, sessionFlagsOffset);
            if (level != 1 || reportedId != sessionId ||
                state < (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive || state > (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSInit)
            {
                return (WtsLockFlag.Unknown, rawFlags);
            }

            var os = Environment.OSVersion.Version;
            var inverted = SessionLockRules.IsInvertedFlagOs(os.Major, os.Minor);
            return (SessionLockRules.DecodeSessionFlags(rawFlags, inverted), rawFlags);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The processes of a session that have the process name of the exe (the file name without ".exe"),
    /// with their full exe path (null if Windows refuses it). The caller decides which process is the real exe.
    /// </summary>
    public static List<(int Pid, string? ImagePath)> FindProcessesByExeName(string exePath, int sessionId)
    {
        var result = new List<(int, string?)>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(SessionAgentMatch.ProcessName(exePath)))
        {
            using (p)
            {
                int processSessionId;
                try { processSessionId = p.SessionId; }
                catch (InvalidOperationException) { continue; } // the process ended
                if (processSessionId != sessionId)
                    continue;

                result.Add((p.Id, ProcessControl.QueryPathAndStart(p.Id).Path));
            }
        }
        return result;
    }

    public static bool LaunchProcessInSession(int sessionId, string exePath, string? arguments = null)
    {
        if (!File.Exists(exePath))
        {
            return false;
        }

        var hUserToken = IntPtr.Zero;
        var hPrimaryToken = IntPtr.Zero;
        var lpEnvironment = IntPtr.Zero;

        try
        {
            if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out hUserToken))
            {
                return false;
            }

            if (!NativeMethods.DuplicateTokenEx(
                    hUserToken,
                    NativeMethods.MAXIMUM_ALLOWED,
                    IntPtr.Zero,
                    NativeMethods.SecurityImpersonation,
                    NativeMethods.TokenPrimary,
                    out hPrimaryToken))
            {
                return false;
            }

            if (!NativeMethods.CreateEnvironmentBlock(out lpEnvironment, hPrimaryToken, false))
            {
                lpEnvironment = IntPtr.Zero;
            }

            var si = new NativeMethods.STARTUPINFO();
            si.cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>();
            si.lpDesktop = @"winsta0\default";

            var workingDir = Path.GetDirectoryName(exePath);
            var cmdLine = string.IsNullOrEmpty(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";

            var success = NativeMethods.CreateProcessAsUserW(
                hPrimaryToken,
                null,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                lpEnvironment,
                workingDir,
                ref si,
                out var pi);

            if (success)
            {
                if (pi.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) NativeMethods.CloseHandle(pi.hThread);
            }

            return success;
        }
        finally
        {
            if (lpEnvironment != IntPtr.Zero)
                NativeMethods.DestroyEnvironmentBlock(lpEnvironment);
            if (hPrimaryToken != IntPtr.Zero)
                NativeMethods.CloseHandle(hPrimaryToken);
            if (hUserToken != IntPtr.Zero)
                NativeMethods.CloseHandle(hUserToken);
        }
    }
}
