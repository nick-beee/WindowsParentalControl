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
    public void Lockdown_shows_the_error_and_not_set_up()
    {
        var failed = new DataFolderReport(DataFolderState.Lockdown, "Access is denied", At, null, null);
        var text = AdminSignInStatus.Describe(true, At, failed, Utc);
        Assert.StartsWith("Locked:", text);
        Assert.Contains("Access is denied", text);
        Assert.Contains("2026-11-26 08:30", text);
        Assert.DoesNotContain("Set up on", text);
        Assert.Contains("tries again every minute", text);
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

    [Fact]
    public void Relocation_warning_names_the_old_and_the_new_folder()
    {
        Assert.Null(AdminSignInStatus.RelocationWarning(Secured, Utc));
        var relocated = Secured with
        {
            RelocatedFrom = @"C:\ProgramData\ParentalControl",
            RelocatedTo = @"C:\ProgramData\ParentalControl-0123456789abcdef0123456789abcdef",
            RelocatedAtUtc = At,
            RelocationReason = "The process cannot access the file",
        };
        var warning = AdminSignInStatus.RelocationWarning(relocated, Utc);
        Assert.NotNull(warning);
        Assert.Contains(@"C:\ProgramData\ParentalControl ", warning);
        Assert.Contains(@"C:\ProgramData\ParentalControl-0123456789abcdef0123456789abcdef", warning);
        Assert.Contains("The process cannot access the file", warning);
        Assert.Contains("2026-11-26 08:30", warning);
    }

    [Fact]
    public void Trusted_folder_with_a_failed_reset_keeps_the_web_admin_locked()
    {
        var warned = Secured with { Detail = "Access is denied" };
        Assert.False(AdminSignInStatus.IsWebAdminUsable(true, warned));
        var text = AdminSignInStatus.Describe(true, At, warned, Utc);
        Assert.StartsWith("Locked:", text);
        Assert.Contains("Access is denied", text);
        Assert.DoesNotContain("Set up on", text);
    }

    [Fact]
    public void Step_warning_shows_only_for_a_secured_folder_with_a_detail()
    {
        Assert.Null(AdminSignInStatus.StepWarning(Secured));
        Assert.Contains("Access is denied", AdminSignInStatus.StepWarning(Secured with { Detail = "Access is denied" }));
        Assert.Null(AdminSignInStatus.StepWarning(new DataFolderReport(DataFolderState.Lockdown, "x", At, null, null)));
    }

    [Fact]
    public void Program_on_the_port_locks_the_web_admin_and_names_the_program()
    {
        // SF3: while a program holds port 5050, the parent must not type a code into http://localhost:5050.
        Assert.Null(AdminSignInStatus.PortBlockedWarning(Secured, Utc));
        var blocked = Secured with { WebPortBlockedBy = "evil.exe (process 4242, user S-1-5-21-1-2-3-1001)", WebPortBlockedAtUtc = At };
        Assert.False(AdminSignInStatus.IsWebAdminUsable(true, blocked));
        var warning = AdminSignInStatus.PortBlockedWarning(blocked, Utc);
        Assert.NotNull(warning);
        Assert.Contains("evil.exe (process 4242", warning);
        Assert.Contains("2026-11-26 08:30", warning);
        Assert.Contains("Do not type a sign-in code", warning);
        var text = AdminSignInStatus.Describe(true, At, blocked, Utc);
        Assert.StartsWith("Locked:", text);
        Assert.DoesNotContain("Set up on", text);
    }
}
