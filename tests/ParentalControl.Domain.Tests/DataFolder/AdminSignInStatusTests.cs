using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class AdminSignInStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 8, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DataFolderReport Secured = new(DataFolderState.Secured, null, At, null, null);

    [Fact]
    public void Set_up_shows_the_date_only_when_the_folder_is_secured()
    {
        Assert.Equal("Set up on 2026-11-26 08:30", AdminSignInStatus.Describe(true, At, Secured, Utc));
        Assert.True(AdminSignInStatus.IsWebAdminUsable(true, Secured));
    }

    [Fact]
    public void Failed_folder_step_shows_the_error_and_not_set_up()
    {
        var failed = new DataFolderReport(DataFolderState.Failed, "Access is denied", At, null, null);
        var text = AdminSignInStatus.Describe(true, At, failed, Utc);
        Assert.StartsWith("Locked:", text);
        Assert.Contains("Access is denied", text);
        Assert.Contains("2026-11-26 08:30", text);
        Assert.DoesNotContain("Set up on", text);
        Assert.False(AdminSignInStatus.IsWebAdminUsable(true, failed));
    }

    [Fact]
    public void Unknown_folder_state_is_locked()
    {
        Assert.StartsWith("Locked:", AdminSignInStatus.Describe(true, At, DataFolderReport.None, Utc));
        Assert.False(AdminSignInStatus.IsWebAdminUsable(true, DataFolderReport.None));
    }

    [Fact]
    public void Not_set_up_on_a_secured_folder()
    {
        Assert.StartsWith("Not set up", AdminSignInStatus.Describe(false, null, Secured, Utc));
        Assert.False(AdminSignInStatus.IsWebAdminUsable(false, Secured));
    }

    [Fact]
    public void Quarantine_warning_names_the_moved_folder()
    {
        Assert.Null(AdminSignInStatus.QuarantineWarning(Secured, Utc));
        var moved = Secured with { QuarantinedTo = @"C:\ProgramData\ParentalControl.untrusted-abc", QuarantinedAtUtc = At };
        var warning = AdminSignInStatus.QuarantineWarning(moved, Utc);
        Assert.NotNull(warning);
        Assert.Contains(@"C:\ProgramData\ParentalControl.untrusted-abc", warning);
        Assert.Contains("2026-11-26 08:30", warning);
    }
}
