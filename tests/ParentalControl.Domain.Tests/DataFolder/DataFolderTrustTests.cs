using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class DataFolderTrustTests
{
    private const string Kid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const uint FullAccess = 0x001F01FF;
    private const uint ReadExecute = 0x001200A9;

    private static AceFact Allow(string sid, uint mask, bool inheritOnly = false) => new(AceKind.Allow, sid, mask, inheritOnly);

    private static SecurityFacts Secured(params AceFact[] extra) =>
        new(DataFolderTrust.AdministratorsSid,
            new[] { Allow(DataFolderTrust.SystemSid, FullAccess), Allow(DataFolderTrust.AdministratorsSid, FullAccess) }.Concat(extra).ToList());

    [Fact]
    public void Folder_of_the_service_is_trusted()
    {
        Assert.Null(DataFolderTrust.Check(Secured()));
        Assert.Null(DataFolderTrust.Check(Secured() with { OwnerSid = DataFolderTrust.SystemSid }));
    }

    [Fact]
    public void Read_access_for_users_is_trusted()
    {
        // Read access is a leak, not a change of the content. The service then replaces the rules.
        Assert.Null(DataFolderTrust.Check(Secured(Allow(Users, ReadExecute))));
    }

    [Fact]
    public void Folder_owned_by_the_child_is_not_trusted()
    {
        var reason = DataFolderTrust.Check(Secured() with { OwnerSid = Kid });
        Assert.NotNull(reason);
        Assert.Contains(Kid, reason);
    }

    [Fact]
    public void Missing_owner_is_not_trusted()
    {
        Assert.NotNull(DataFolderTrust.Check(Secured() with { OwnerSid = null }));
    }

    [Fact]
    public void Null_dacl_is_not_trusted()
    {
        Assert.NotNull(DataFolderTrust.Check(new SecurityFacts(DataFolderTrust.AdministratorsSid, null)));
    }

    [Theory]
    [InlineData(0x00000002u)] // FILE_WRITE_DATA / FILE_ADD_FILE
    [InlineData(0x00000004u)] // FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
    [InlineData(0x00000010u)] // FILE_WRITE_EA
    [InlineData(0x00000040u)] // FILE_DELETE_CHILD
    [InlineData(0x00000100u)] // FILE_WRITE_ATTRIBUTES
    [InlineData(0x00010000u)] // DELETE
    [InlineData(0x00040000u)] // WRITE_DAC
    [InlineData(0x00080000u)] // WRITE_OWNER
    [InlineData(0x10000000u)] // GENERIC_ALL
    [InlineData(0x40000000u)] // GENERIC_WRITE
    public void Each_write_right_of_another_account_is_not_trusted(uint mask)
    {
        Assert.NotNull(DataFolderTrust.Check(Secured(Allow(Kid, mask))));
        Assert.NotNull(DataFolderTrust.Check(Secured(Allow(AuthenticatedUsers, mask))));
    }

    [Fact]
    public void Inherited_rule_of_ProgramData_for_users_is_not_trusted()
    {
        // C:\ProgramData gives BUILTIN\Users "create files" and "create folders" on the folders below it.
        Assert.NotNull(DataFolderTrust.Check(Secured(Allow(Users, 0x00000006))));
    }

    [Fact]
    public void Inherit_only_entries_deny_entries_and_audit_entries_are_ignored()
    {
        var facts = Secured(
            Allow(Kid, FullAccess, inheritOnly: true),
            new AceFact(AceKind.Deny, Kid, FullAccess, false),
            new AceFact(AceKind.Audit, Kid, FullAccess, false));
        Assert.Null(DataFolderTrust.Check(facts));
    }

    [Fact]
    public void Creator_owner_and_owner_rights_are_trusted_when_the_owner_is_trusted()
    {
        var facts = Secured(
            Allow(DataFolderTrust.CreatorOwnerSid, FullAccess),
            Allow(DataFolderTrust.OwnerRightsSid, FullAccess));
        Assert.Null(DataFolderTrust.Check(facts));
    }

    [Fact]
    public void Unknown_entry_type_is_not_trusted()
    {
        Assert.NotNull(DataFolderTrust.Check(Secured(new AceFact(AceKind.Unknown, Kid, 0, false))));
    }
}
