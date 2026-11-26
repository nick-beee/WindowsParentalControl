using System.Runtime.InteropServices;
using System.Security.Principal;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Finds out whether the user of a session is an administrator (require-admin-sign-in, D14: the lockdown spares the
/// sessions of administrators). With UAC, the token of a session of an administrator is filtered: the Administrators
/// group is in it as "deny only", so <c>WindowsPrincipal.IsInRole</c> returns false. This check therefore reads the
/// groups of the token and accepts the Administrators group when it is enabled or deny-only.
/// </summary>
public static class SessionAdminCheck
{
    private const int TokenGroups = 2;
    private const uint SE_GROUP_ENABLED = 0x00000004;
    private const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x00000010;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>The status of the user of a session. Needs LocalSystem (WTSQueryUserToken).</summary>
    public static SessionAdminStatus ForSession(int sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out var token))
            return SessionAdminStatus.Unknown;
        try
        {
            return ForToken(token);
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>The status of the user of a token (it needs TOKEN_QUERY access).</summary>
    public static SessionAdminStatus ForToken(IntPtr token)
    {
        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
            return SessionAdminStatus.Unknown;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, needed, out _))
                return SessionAdminStatus.Unknown;
            // TOKEN_GROUPS: a DWORD count, then SID_AND_ATTRIBUTES entries (a SID pointer and a DWORD), pointer-aligned.
            var count = Marshal.ReadInt32(buffer);
            var entrySize = IntPtr.Size * 2;
            var first = buffer + IntPtr.Size;
            for (var i = 0; i < count; i++)
            {
                var entry = first + i * entrySize;
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(entry));
                var attributes = unchecked((uint)Marshal.ReadInt32(entry + IntPtr.Size));
                if (sid.Equals(Administrators) && (attributes & (SE_GROUP_ENABLED | SE_GROUP_USE_FOR_DENY_ONLY)) != 0)
                    return SessionAdminStatus.Administrator;
            }
            return SessionAdminStatus.NotAdministrator;
        }
        catch
        {
            return SessionAdminStatus.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
