namespace ParentalControl.Domain.DataFolder;

/// <summary>The kind of one access control entry (ACE).</summary>
public enum AceKind
{
    Allow,
    Deny,
    /// <summary>An audit or alarm entry. It gives no access.</summary>
    Audit,
    /// <summary>A type that the check does not know. It counts as a write right (fail closed).</summary>
    Unknown,
}

/// <param name="Sid">The SID in string form, for example <c>S-1-5-32-545</c>.</param>
/// <param name="Mask">The access mask of the entry.</param>
/// <param name="InheritOnly">True if the entry applies only to items below the folder, not to the folder itself.</param>
public sealed record AceFact(AceKind Kind, string Sid, uint Mask, bool InheritOnly);

/// <summary>The owner and the DACL of one file or folder, as Windows reports them.</summary>
/// <param name="OwnerSid">The owner SID, or null if Windows reported none.</param>
/// <param name="Dacl">The entries of the DACL, or null for a null DACL (each account has full access).</param>
public sealed record SecurityFacts(string? OwnerSid, IReadOnlyList<AceFact>? Dacl);

/// <summary>
/// Decides whether the content of the data folder is trusted (require-admin-sign-in, finding B2).
/// An item is trusted only if SYSTEM or the Administrators group owns it and no other account has a right that
/// lets it change the item: write, append, write attributes, delete, WRITE_DAC or WRITE_OWNER.
/// A Restricted User can create a folder in <c>C:\ProgramData</c> before the install; the content of such a folder is
/// not trusted, because the child could put a database, a link or an open handle into it.
/// </summary>
public static class DataFolderTrust
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    /// <summary>OWNER RIGHTS: the entry applies to the current owner.</summary>
    public const string OwnerRightsSid = "S-1-3-4";
    /// <summary>CREATOR OWNER and CREATOR GROUP: placeholders for inheritance. They give no access to the item itself.</summary>
    public const string CreatorOwnerSid = "S-1-3-0";
    public const string CreatorGroupSid = "S-1-3-1";

    // FILE_WRITE_DATA / FILE_ADD_FILE, FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY, FILE_WRITE_EA, FILE_DELETE_CHILD,
    // FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
    public const uint WriteRightsMask =
        0x0002 | 0x0004 | 0x0010 | 0x0040 | 0x0100 | 0x0001_0000 | 0x0004_0000 | 0x0008_0000 | 0x1000_0000 | 0x4000_0000;

    public static bool IsTrustedAccount(string? sid) =>
        string.Equals(sid, SystemSid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns null if the item is trusted, else the reason in plain English.</summary>
    public static string? Check(SecurityFacts facts)
    {
        if (!IsTrustedAccount(facts.OwnerSid))
            return $"the owner is {facts.OwnerSid ?? "unknown"}, not SYSTEM or Administrators";
        if (facts.Dacl is null)
            return "the item has a null DACL (each account has full access)";

        foreach (var ace in facts.Dacl)
        {
            if (ace.InheritOnly || ace.Kind is AceKind.Deny or AceKind.Audit)
                continue;
            if (ace.Kind == AceKind.Unknown)
                return $"the DACL has an entry of an unknown type for {ace.Sid}";
            if ((ace.Mask & WriteRightsMask) == 0)
                continue;
            if (IsTrustedAccount(ace.Sid))
                continue;
            // The owner is trusted here, so OWNER RIGHTS gives rights to a trusted account only.
            if (string.Equals(ace.Sid, OwnerRightsSid, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(ace.Sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ace.Sid, CreatorGroupSid, StringComparison.OrdinalIgnoreCase))
                continue;
            return $"{ace.Sid} has write rights (0x{ace.Mask & WriteRightsMask:X})";
        }
        return null;
    }
}
