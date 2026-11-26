using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>The data folder is not safe to use. The caller must not open the database in it (fail closed).</summary>
public sealed class DataFolderUnsafeException : Exception
{
    public DataFolderUnsafeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <param name="QuarantinedTo">The new name of an untrusted data folder that the service moved away, or null.</param>
/// <param name="QuarantineReason">Why the content was not trusted, or null.</param>
/// <param name="Repairs">Links and hard links that the service removed or replaced, in plain English.</param>
public sealed record DataFolderResult(string? QuarantinedTo, string? QuarantineReason, IReadOnlyList<string> Repairs);

/// <summary>
/// Makes the data folder safe from the Restricted User (require-admin-sign-in, design D9 and findings B1, B2, S1, S2).
/// A standard user can create a folder, a junction or a symbolic link in <c>C:\ProgramData</c> before the install.
/// The service (LocalSystem) therefore never follows a link and never trusts content that another account could change:
/// <list type="bullet">
/// <item>A link as the data folder: the service deletes the link itself (not its target) and creates a real folder.</item>
/// <item>A data folder or an item in it that another account owns or can change: the service renames the folder to
/// <c>ParentalControl.untrusted-&lt;guid&gt;</c> and creates a new, empty folder.</item>
/// <item>A link inside the folder: the service deletes the link. A file with more than one name (a hard link) gets a new copy.</item>
/// </list>
/// Each check and each change uses a handle that is opened without a follow of a reparse point, so the item cannot
/// change between the check and the change. Each failure throws <see cref="DataFolderUnsafeException"/>.
/// </summary>
public static class DataFolderGuard
{
    // Folder: owner Administrators, a protected DACL with full control for SYSTEM and Administrators, inherited by all items.
    public const string FolderSddl = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    // Items below it: owner Administrators and only the inherited rules of the folder. The service writes the
    // inherited entries itself, so the result does not depend on an inheritance calculation of Windows.
    private const string DirectoryItemSddl = "O:BAD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)";
    private const string FileItemSddl = "O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)";
    // A file in the place of the data folder, after the move: only SYSTEM and Administrators.
    private const string LockedFileSddl = "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";

    private const int MaxTries = 5;

    /// <summary>
    /// The service step at each start. Needs LocalSystem (SeTakeOwnershipPrivilege, SeRestorePrivilege, SeBackupPrivilege).
    /// </summary>
    public static DataFolderResult Secure(string folder)
    {
        try
        {
            TokenPrivileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege");
            return SecureCore(Path.GetFullPath(folder));
        }
        catch (DataFolderUnsafeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DataFolderUnsafeException($"The service could not secure the data folder {folder}: {ex.Message}", ex);
        }
    }

    private static DataFolderResult SecureCore(string folder)
    {
        string? quarantinedTo = null;
        string? quarantineReason = null;
        var repairs = new List<string>();

        for (var attempt = 1; attempt <= MaxTries; attempt++)
        {
            using var root = Native.TryOpen(folder, Native.CheckAccess);
            if (root is null)
            {
                // No folder: create it with its final owner and rules in one step, so there is no moment with other rules.
                Native.CreateDirectoryWithSddl(folder, FolderSddl, allowExisting: true);
                continue;
            }

            var info = Native.GetInfo(root);
            if (info.IsReparsePoint)
            {
                // B1: a junction or a symbolic link as the data folder. Delete the link itself, never its target.
                Native.DeleteSameObject(folder, info);
                repairs.Add($"Deleted the link {folder} (a junction or a symbolic link as the data folder).");
                continue;
            }

            var reason = info.IsDirectory
                ? Describe(folder, DataFolderTrust.Check(Native.ReadFacts(root))) ?? FindUntrustedItem(folder)
                : $"{folder} is a file, not a folder";
            if (reason is not null)
            {
                // B2: the content is not trusted. Move it away and start with a new folder and a new database.
                quarantinedTo = Quarantine(folder, info);
                quarantineReason = reason;
                continue;
            }

            Native.SetSecurity(root, FolderSddl, Native.ProtectedDacl);
            ResetItems(folder, repairs);
            return new DataFolderResult(quarantinedTo, quarantineReason, repairs);
        }

        throw new DataFolderUnsafeException($"The data folder {folder} changed during each of {MaxTries} tries.");
    }

    /// <summary>
    /// The check of the Admin app before it opens the database. It changes nothing, except that it creates a missing
    /// folder with the final rules. It throws if the folder is a link, if the folder or <c>data.db</c> is not trusted,
    /// or if <c>data.db</c> is a link or has more than one name.
    /// </summary>
    public static void EnsureSafeToOpen(string folder, string databaseFileName)
    {
        try
        {
            folder = Path.GetFullPath(folder);
            var root = Native.TryOpen(folder, Native.ReadAccess);
            if (root is null)
            {
                Native.CreateDirectoryWithSddl(folder, FolderSddl, allowExisting: true);
                root = Native.TryOpen(folder, Native.ReadAccess) ?? throw new DataFolderUnsafeException($"The data folder {folder} does not exist.");
            }

            using (root)
            {
                var info = Native.GetInfo(root);
                if (info.IsReparsePoint)
                    throw new DataFolderUnsafeException($"The data folder {folder} is a link (a junction or a symbolic link).");
                if (!info.IsDirectory)
                    throw new DataFolderUnsafeException($"The data folder {folder} is a file, not a folder.");
                var reason = Describe(folder, DataFolderTrust.Check(Native.ReadFacts(root)));
                if (reason is not null)
                    throw new DataFolderUnsafeException("The data folder is not trusted: " + reason);
            }

            var database = Path.Combine(folder, databaseFileName);
            using var db = Native.TryOpen(database, Native.ReadAccess);
            if (db is null)
                return;
            var dbInfo = Native.GetInfo(db);
            if (dbInfo.IsReparsePoint || dbInfo.IsDirectory)
                throw new DataFolderUnsafeException($"{database} is a link or a folder.");
            if (dbInfo.LinkCount > 1)
                throw new DataFolderUnsafeException($"{database} has more than one name (a hard link).");
            var dbReason = Describe(database, DataFolderTrust.Check(Native.ReadFacts(db)));
            if (dbReason is not null)
                throw new DataFolderUnsafeException("The database is not trusted: " + dbReason);
        }
        catch (DataFolderUnsafeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DataFolderUnsafeException($"The data folder {folder} could not be checked: {ex.Message}", ex);
        }
    }

    private static string? Describe(string path, string? reason) => reason is null ? null : $"{path}: {reason}";

    /// <summary>The reason for the first item below the folder that is not trusted, or null. Links are skipped.</summary>
    private static string? FindUntrustedItem(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            using var handle = Native.TryOpen(entry, Native.CheckAccess);
            if (handle is null)
                continue;
            var info = Native.GetInfo(handle);
            if (info.IsReparsePoint)
                continue; // ResetItems deletes it.
            var reason = Describe(entry, DataFolderTrust.Check(Native.ReadFacts(handle)));
            if (reason is not null)
                return reason;
            if (info.IsDirectory && FindUntrustedItem(entry) is { } inner)
                return inner;
        }
        return null;
    }

    /// <summary>
    /// S2: owner Administrators and only the inherited rules for each item. A link is deleted (the folders that the
    /// service needs are created again as real folders when it uses them). A file with more than one name gets a new copy.
    /// </summary>
    private static void ResetItems(string directory, List<string> repairs)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var handle = Native.TryOpen(entry, Native.CheckAccess);
            if (handle is null)
                continue;
            using (handle)
            {
                var info = Native.GetInfo(handle);
                if (info.IsReparsePoint)
                {
                    Native.DeleteSameObject(entry, info);
                    repairs.Add($"Deleted the link {entry}.");
                    continue;
                }

                if (info.IsDirectory)
                {
                    Native.SetSecurity(handle, DirectoryItemSddl, Native.UnprotectedDacl);
                    ResetItems(entry, repairs);
                    continue;
                }

                // Lock this file first, so its other names also lose each other rule.
                Native.SetSecurity(handle, FileItemSddl, Native.UnprotectedDacl);
                if (info.LinkCount > 1)
                {
                    handle.Dispose();
                    ReplaceWithCopy(entry);
                    repairs.Add($"Replaced {entry} with a copy, because it had more than one name (a hard link).");
                }
            }
        }
    }

    /// <summary>
    /// Puts a new file with the same content at the name. The other names keep the old file. The old name is removed
    /// with POSIX semantics, so a scanner that holds the old file open does not keep the name.
    /// </summary>
    private static void ReplaceWithCopy(string path)
    {
        var copy = path + ".copy-" + Guid.NewGuid().ToString("N");
        File.Copy(path, copy);
        using (var old = Native.TryOpen(path, Native.FILE_READ_ATTRIBUTES) ?? throw new IOException($"{path} is gone."))
            Native.DeleteSameObject(path, Native.GetInfo(old));
        File.Move(copy, path);
        using var handle = Native.TryOpen(path, Native.CheckAccess) ?? throw new IOException($"{path} is missing after the copy.");
        var info = Native.GetInfo(handle);
        if (info.IsReparsePoint || info.LinkCount != 1)
            throw new IOException($"{path} has {info.LinkCount} names after the copy.");
        Native.SetSecurity(handle, FileItemSddl, Native.UnprotectedDacl);
    }

    /// <summary>
    /// Renames the checked folder (the same object, through a handle) to <c>&lt;name&gt;.untrusted-&lt;guid&gt;</c>.
    /// The rename fails if a process holds a handle to the folder or to an item in it without delete sharing; then the
    /// start fails (fail closed). After the rename, the service locks the moved folder itself (through the same handle).
    /// It does not walk into the moved folder: the child controlled its content, so a walk by path could follow a link.
    /// </summary>
    private static string Quarantine(string folder, Native.FileInfoByHandle checkedInfo)
    {
        var parent = Path.GetDirectoryName(folder) ?? throw new IOException($"{folder} has no parent folder.");
        var target = Path.Combine(parent, Path.GetFileName(folder) + ".untrusted-" + Guid.NewGuid().ToString("N"));
        using var handle = Native.OpenSameObject(folder, checkedInfo, Native.CheckAccess | Native.DELETE);
        Native.Rename(handle, target);
        try
        {
            Native.SetSecurity(handle, checkedInfo.IsDirectory ? FolderSddl : LockedFileSddl, Native.ProtectedDacl);
        }
        catch
        {
            // Best effort: the moved folder is not used any more.
        }
        return target;
    }

    /// <summary>The Win32 calls. Each open uses FILE_FLAG_OPEN_REPARSE_POINT, so no call follows a link.</summary>
    internal static class Native
    {
        public const uint FILE_READ_ATTRIBUTES = 0x0080;
        public const uint DELETE = 0x0001_0000;
        public const uint READ_CONTROL = 0x0002_0000;
        public const uint WRITE_DAC = 0x0004_0000;
        public const uint WRITE_OWNER = 0x0008_0000;
        // READ_CONTROL, WRITE_DAC, WRITE_OWNER and FILE_READ_ATTRIBUTES take no part in the share check, so an open
        // handle of another process (for example SQLite in the Admin app) does not block them.
        public const uint CheckAccess = READ_CONTROL | WRITE_DAC | WRITE_OWNER | FILE_READ_ATTRIBUTES;
        public const uint ReadAccess = READ_CONTROL | FILE_READ_ATTRIBUTES;

        private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ALREADY_EXISTS = 183;
        private const int FileRenameInfo = 3;
        private const int FileDispositionInfo = 4;
        private const int FileDispositionInfoEx = 21;
        private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
        private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;

        private const uint OWNER_SECURITY_INFORMATION = 0x1;
        private const uint DACL_SECURITY_INFORMATION = 0x4;
        public const uint ProtectedDacl = 0x8000_0000;   // PROTECTED_DACL_SECURITY_INFORMATION
        public const uint UnprotectedDacl = 0x2000_0000; // UNPROTECTED_DACL_SECURITY_INFORMATION

        // FILETIME is two DWORDs with 4-byte alignment, so the times are uint pairs here (a long would add padding).
        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh;
            public uint LastAccessTimeLow, LastAccessTimeHigh;
            public uint LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_DISPOSITION_INFO
        {
            [MarshalAs(UnmanagedType.U1)]
            public bool DeleteFile;
        }

        public readonly record struct FileInfoByHandle(uint Attributes, uint LinkCount, uint VolumeSerial, ulong FileIndex)
        {
            public bool IsReparsePoint => (Attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            public bool IsDirectory => (Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            public bool IsSameObject(FileInfoByHandle other) => VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string pathName, ref SECURITY_ATTRIBUTES securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint flags, uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint requestedInformation, byte[]? descriptor, uint length, out uint lengthNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte[] descriptor);

        /// <summary>Opens the item itself (also a link) or returns null if it does not exist.</summary>
        public static SafeFileHandle? TryOpen(string path, uint access)
        {
            var handle = CreateFileW(path, access, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (!handle.IsInvalid)
                return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
                return null;
            throw new Win32Exception(error, $"Could not open {path}");
        }

        /// <summary>Opens the item again with more access and checks that it is the same object as the checked one.</summary>
        public static SafeFileHandle OpenSameObject(string path, FileInfoByHandle checkedInfo, uint access)
        {
            var handle = TryOpen(path, access) ?? throw new IOException($"{path} is gone.");
            if (!GetInfo(handle).IsSameObject(checkedInfo))
            {
                handle.Dispose();
                throw new IOException($"{path} changed during the check.");
            }
            return handle;
        }

        public static FileInfoByHandle GetInfo(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileInformationByHandle failed");
            return new FileInfoByHandle(info.FileAttributes, info.NumberOfLinks, info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }

        /// <summary>
        /// Deletes the checked name itself. For a link, this deletes the link and not its target; for a file with more
        /// than one name, only this name. POSIX semantics remove the name at once, also while another process (for
        /// example a virus scanner) holds the item open; Windows versions without them use the classic delete.
        /// </summary>
        public static void DeleteSameObject(string path, FileInfoByHandle checkedInfo)
        {
            using var handle = OpenSameObject(path, checkedInfo, DELETE | FILE_READ_ATTRIBUTES);
            var flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS;
            if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, 4))
                return;
            var disposition = new FILE_DISPOSITION_INFO { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not delete {path}");
        }

        public static void Rename(SafeFileHandle handle, string newFullPath)
        {
            // FILE_RENAME_INFO: a BOOLEAN (padded to a DWORD), a HANDLE, a DWORD length in bytes, then the name.
            var rootOffset = IntPtr.Size;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + 4;
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(newFullPath);
            var size = nameOffset + nameBytes.Length + 2;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (var i = 0; i < size; i++)
                    Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
                if (!SetFileInformationByHandle(handle, FileRenameInfo, buffer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not rename the data folder to {newFullPath}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static SecurityFacts ReadFacts(SafeFileHandle handle)
        {
            const uint info = OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION;
            GetKernelObjectSecurity(handle, info, null, 0, out var needed);
            if (needed == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetKernelObjectSecurity failed");
            var buffer = new byte[needed];
            if (!GetKernelObjectSecurity(handle, info, buffer, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetKernelObjectSecurity failed");

            var descriptor = new RawSecurityDescriptor(buffer, 0);
            List<AceFact>? dacl = null;
            if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && descriptor.DiscretionaryAcl is { } acl)
            {
                dacl = new List<AceFact>();
                foreach (GenericAce ace in acl)
                {
                    var inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
                    if (ace is QualifiedAce qualified)
                    {
                        var kind = qualified.AceQualifier switch
                        {
                            AceQualifier.AccessAllowed => AceKind.Allow,
                            AceQualifier.AccessDenied => AceKind.Deny,
                            _ => AceKind.Audit,
                        };
                        dacl.Add(new AceFact(kind, qualified.SecurityIdentifier.Value, unchecked((uint)qualified.AccessMask), inheritOnly));
                    }
                    else if (ace is CompoundAce compound)
                    {
                        // A compound ACE is an allow entry (for impersonation).
                        dacl.Add(new AceFact(AceKind.Allow, compound.SecurityIdentifier.Value, unchecked((uint)compound.AccessMask), inheritOnly));
                    }
                    else
                    {
                        dacl.Add(new AceFact(AceKind.Unknown, "unknown", 0, inheritOnly));
                    }
                }
            }
            return new SecurityFacts(descriptor.Owner?.Value, dacl);
        }

        /// <summary>Writes the owner and the DACL of the SDDL on the handle. No inheritance step of Windows runs.</summary>
        public static void SetSecurity(SafeFileHandle handle, string sddl, uint daclProtection)
        {
            if (!SetKernelObjectSecurity(handle, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | daclProtection, Binary(sddl)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetKernelObjectSecurity failed");
        }

        /// <summary>Creates a folder with its owner and DACL in one step.</summary>
        public static void CreateDirectoryWithSddl(string path, string sddl, bool allowExisting)
        {
            var descriptor = Binary(sddl);
            var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SECURITY_ATTRIBUTES
                {
                    Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    SecurityDescriptor = pin.AddrOfPinnedObject(),
                    InheritHandle = 0,
                };
                if (CreateDirectoryW(path, ref attributes))
                    return;
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_ALREADY_EXISTS && allowExisting)
                    return;
                throw new Win32Exception(error, $"Could not create {path}");
            }
            finally
            {
                pin.Free();
            }
        }

        private static byte[] Binary(string sddl)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
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
