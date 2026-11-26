using System.Security.AccessControl;
using Microsoft.Win32;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>
/// The registry key of Parental Control, <c>HKLM\SOFTWARE\ParentalControl</c> (64-bit view). It holds the location of
/// the data folder (require-admin-sign-in, D14) and the result of the data folder step (finding S3).
/// Only SYSTEM and the Administrators group can change the key; the other accounts can only read it. The service writes
/// this rule on the key each time it writes. Each reader checks the owner and the rules of the key before it uses the
/// location (the same check as for the data folder). The values contain no secret.
/// </summary>
public sealed class DataFolderRegistry
{
    public const string DefaultKeyPath = @"SOFTWARE\ParentalControl";
    public const string LocationValue = "DataFolderPath";

    // Owner Administrators; a protected DACL: full control for SYSTEM and Administrators, read for Users.
    public const string KeySddl = "O:BAD:PAI(A;CI;KA;;;SY)(A;CI;KA;;;BA)(A;CI;KR;;;BU)";

    /// <summary>The key of the product. Tests use another key path.</summary>
    public static readonly DataFolderRegistry Default = new(DefaultKeyPath);

    public DataFolderRegistry(string keyPath) => KeyPath = keyPath;

    public string KeyPath { get; }

    /// <summary>The parent folder of the data folder: <c>C:\ProgramData</c> on a normal PC.</summary>
    public static string ProgramDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// The data folder from the registry value, or the default if the value is absent. If the key is not trusted or the
    /// value is not accepted, the result is not usable: the caller never falls back to the default then.
    /// </summary>
    public DataFolderLocationReading ReadLocation(string root)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(KeyPath, writable: false);
            if (key is null)
                return DataFolderLocation.Resolve(null, null, root);
            var keyError = CheckKey(key);
            var raw = key.GetValue(LocationValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is not null && raw is not string)
                return new DataFolderLocationReading(null, DataFolderLocationSource.Registry, $"The registry value {LocationValue} is not a string.");
            return DataFolderLocation.Resolve(keyError, (string?)raw, root);
        }
        catch (Exception ex)
        {
            return new DataFolderLocationReading(null, DataFolderLocationSource.Registry, "The registry key of Parental Control could not be read: " + ex.Message);
        }
    }

    /// <summary>Stores the location of the data folder. Only the service (LocalSystem) calls this. Throws on an error.</summary>
    public void WriteLocation(string folder)
    {
        using var key = OpenForWrite();
        key.SetValue(LocationValue, folder, RegistryValueKind.String);
    }

    /// <summary>
    /// Opens the key for a write. It creates a missing key and writes the owner and the rules of <see cref="KeySddl"/>.
    /// An error of the rules does not stop the write of the values: each reader checks the key with <see cref="CheckKey"/>.
    /// </summary>
    public RegistryKey OpenForWrite()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        hklm.CreateSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree).Dispose();
        // CreateSubKey gives KEY_READ | KEY_WRITE only. The write of the owner and the DACL needs WRITE_OWNER and WRITE_DAC.
        var key = hklm.OpenSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                      RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership)
                  ?? throw new IOException($"HKLM\\{KeyPath} is gone after the create.");
        try
        {
            var security = new RegistrySecurity();
            security.SetSecurityDescriptorSddlForm(KeySddl, AccessControlSections.Owner | AccessControlSections.Access);
            key.SetAccessControl(security);
        }
        catch
        {
            // The values are still written. A reader refuses the key if its rules are not trusted.
        }
        return key;
    }

    /// <summary>Null if the owner and the rules of the key are trusted, else the reason.</summary>
    public static string? CheckKey(RegistryKey key)
    {
        var binary = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var facts = DataFolderGuard.Native.FactsOf(new RawSecurityDescriptor(binary, 0));
        return DataFolderTrust.Check(facts, DataFolderTrust.RegistryKeyWriteRightsMask);
    }

    /// <summary>Deletes the key (tests only).</summary>
    public void DeleteKey()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        hklm.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}
