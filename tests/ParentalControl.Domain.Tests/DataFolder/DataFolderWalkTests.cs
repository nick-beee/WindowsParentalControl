using ParentalControl.Domain.DataFolder;
using ParentalControl.Domain.Tests.AdminAccess;

namespace ParentalControl.Domain.Tests.DataFolder;

public class DataFolderWalkTests
{
    private const string Sy = DataFolderTrust.SystemSid;
    private const string Ba = DataFolderTrust.AdministratorsSid;
    private const string Users = "S-1-5-32-545";
    private const string Folder = @"C:\ProgramData\ParentalControl";

    private static RootAce Full(string sid) => new(AceKind.Allow, sid, DataFolderFastPath.FullAccess, true, true, false);
    private static RootRules Expected() => new(Ba, true, new[] { Full(Sy), Full(Ba) });

    [Fact]
    public void Budget_ends_after_the_time_limit()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var budget = new WalkBudget(TimeSpan.FromSeconds(30), time);
        Assert.True(budget.TryVisit());
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(budget.TryVisit());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(budget.TryVisit());
        Assert.Equal(3, budget.ItemsVisited);
    }

    [Fact]
    public void Budget_with_an_item_limit_ends_after_that_many_items()
    {
        var budget = new WalkBudget(TimeSpan.FromHours(1), maxItems: 2);
        Assert.True(budget.TryVisit());
        Assert.True(budget.TryVisit());
        Assert.False(budget.TryVisit());
    }

    [Fact]
    public void Rules_of_the_service_are_expected_in_any_order()
    {
        Assert.True(DataFolderFastPath.HasExpectedRules(Expected()));
        Assert.True(DataFolderFastPath.HasExpectedRules(new RootRules(Ba, true, new[] { Full(Ba), Full(Sy) })));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("unprotected")]
    [InlineData("users-read")]
    [InlineData("no-inherit")]
    [InlineData("deny")]
    [InlineData("less-access")]
    [InlineData("null-dacl")]
    [InlineData("one-entry")]
    public void Other_rules_are_not_expected(string change)
    {
        var rules = change switch
        {
            "owner" => Expected() with { OwnerSid = Sy },
            "unprotected" => Expected() with { DaclProtected = false },
            "users-read" => Expected() with { Dacl = new[] { Full(Sy), Full(Ba), new RootAce(AceKind.Allow, Users, 0x1200A9, true, true, false) } },
            "no-inherit" => Expected() with { Dacl = new[] { Full(Sy), Full(Ba) with { ObjectInherit = false } } },
            "deny" => Expected() with { Dacl = new[] { Full(Sy), Full(Ba) with { Kind = AceKind.Deny } } },
            "less-access" => Expected() with { Dacl = new[] { Full(Sy), Full(Ba) with { Mask = 0x1200A9 } } },
            "null-dacl" => Expected() with { Dacl = null },
            _ => Expected() with { Dacl = new[] { Full(Ba) } },
        };
        Assert.False(DataFolderFastPath.HasExpectedRules(rules));
    }

    [Fact]
    public void Walk_is_skipped_only_for_the_folder_that_the_last_start_secured()
    {
        Assert.True(DataFolderFastPath.CanSkipWalk(Folder, Folder, Expected(), quickItemsOk: true));
        Assert.True(DataFolderFastPath.CanSkipWalk(Folder.ToUpperInvariant() + "\\", Folder, Expected(), quickItemsOk: true));
        Assert.False(DataFolderFastPath.CanSkipWalk(null, Folder, Expected(), quickItemsOk: true));
        Assert.False(DataFolderFastPath.CanSkipWalk(Folder + "-0123456789abcdef0123456789abcdef", Folder, Expected(), quickItemsOk: true));
        Assert.False(DataFolderFastPath.CanSkipWalk(Folder, Folder, Expected(), quickItemsOk: false));
        Assert.False(DataFolderFastPath.CanSkipWalk(Folder, Folder, Expected() with { DaclProtected = false }, quickItemsOk: true));
    }

    [Fact]
    public void Last_secured_folder_needs_a_Secured_state_without_a_detail()
    {
        var secured = DataFolderReport.None with { State = DataFolderState.Secured, Folder = Folder };
        Assert.Equal(Folder, secured.LastSecuredFolder);
        Assert.Null((secured with { Detail = "a step failed" }).LastSecuredFolder);
        Assert.Null((secured with { State = DataFolderState.Lockdown }).LastSecuredFolder);
    }

    [Fact]
    public void Walk_time_limit_never_gives_a_new_folder()
    {
        // CB1 c: a flood of files must not give an empty database.
        Assert.Equal(DataFolderMode.UseConfiguredWithWarning, DataFolderRecovery.Decide(ConfiguredFolderResult.TimedOutButTrusted));
        Assert.Equal(DataFolderMode.Lockdown, DataFolderRecovery.Decide(ConfiguredFolderResult.TimedOut));
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.TimedOut, 1));
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.TimedOutButTrusted, 1));
    }
}
