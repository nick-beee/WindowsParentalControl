using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Makes the data folder safe from the Restricted User (design D9 of require-admin-sign-in): the Administrators group
/// owns the folder and each file in it, and only SYSTEM and the Administrators group have access.
/// Only the service (LocalSystem) calls it, because it needs SeTakeOwnershipPrivilege and SeRestorePrivilege.
/// Each failure throws; the caller then treats the Remote Admin Interface as not set up.
/// </summary>
public static class DataFolderSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    public static void Apply(string folder)
    {
        var root = new DirectoryInfo(folder);
        // A junction or a symbolic link as the data folder can point to any folder of the system.
        // The service never changes the rules of a link target.
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"The data folder {folder} is a reparse point (a link). The service does not change its access rules.");

        TokenPrivileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");

        // A new DirectorySecurity, not one from GetAccessControl: explicit rules that are there now must go.
        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetOwner(Administrators);
        rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            rootSecurity.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        root.SetAccessControl(rootSecurity);

        // Now the Restricted User has no access to the folder. Reset each item below it:
        // owner Administrators, no explicit rules, the inherited rules of the folder only.
        var links = new List<string>();
        ResetChildren(root, links);
        if (links.Count > 0)
            throw new InvalidOperationException("The data folder contains reparse points (links). The service did not follow them: " + string.Join(", ", links));
    }

    private static void ResetChildren(DirectoryInfo directory, List<string> links)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            // SetAccessControl on a link changes its target. Do not touch it and do not walk into it.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(entry.FullName);
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                var security = new DirectorySecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                subdirectory.SetAccessControl(security);
                ResetChildren(subdirectory, links);
            }
            else if (entry is FileInfo file)
            {
                var security = new FileSecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                file.SetAccessControl(security);
            }
        }
    }

    private static class TokenPrivileges
    {
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        /// <summary>Enables each privilege on the token of this process. Throws if one is missing.</summary>
        public static void Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
            try
            {
                foreach (var name in names)
                {
                    if (!LookupPrivilegeValueW(null, name, out var luid))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue({name}) failed");

                    var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"AdjustTokenPrivileges({name}) failed");
                    // AdjustTokenPrivileges returns true also when the token does not have the privilege.
                    var error = Marshal.GetLastWin32Error();
                    if (error == ERROR_NOT_ALL_ASSIGNED)
                        throw new Win32Exception(error, $"The process does not have the privilege {name}");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
