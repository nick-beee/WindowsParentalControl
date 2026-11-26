using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class LocalClientPolicyTests
{
    private const string Kid = "S-1-5-21-1-2-3-1001";
    private const string Parent = "S-1-5-21-1-2-3-1000";
    private static readonly IReadOnlySet<string> Restricted = new HashSet<string> { Kid };

    [Fact]
    public void Remote_source_is_allowed_without_an_owner()
    {
        Assert.Equal(LocalClientDecision.Allow, LocalClientPolicy.Decide("192.168.1.50", null, Restricted));
    }

    [Fact]
    public void Unknown_source_is_refused()
    {
        Assert.Equal(LocalClientDecision.RefuseUnknownOwner, LocalClientPolicy.Decide(LoginSource.Unknown, null, Restricted));
    }

    [Fact]
    public void Local_source_without_an_owner_is_refused()
    {
        Assert.Equal(LocalClientDecision.RefuseUnknownOwner, LocalClientPolicy.Decide(LoginSource.Local, null, Restricted));
        Assert.Equal(LocalClientDecision.RefuseUnknownOwner,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 1, null, Parent), Restricted));
    }

    [Fact]
    public void Process_of_the_child_is_refused()
    {
        Assert.Equal(LocalClientDecision.RefuseRestrictedUser,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 2, Kid, Kid), Restricted));
        // The SID compare ignores case.
        Assert.Equal(LocalClientDecision.RefuseRestrictedUser,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 2, Kid.ToLowerInvariant(), null), Restricted));
    }

    [Fact]
    public void Process_of_the_parent_in_the_session_of_the_child_is_refused()
    {
        // For example a browser that the parent starts with "Run as different user" on the desktop of the child.
        Assert.Equal(LocalClientDecision.RefuseRestrictedUser,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 2, Parent, Kid), Restricted));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    public void Service_account_is_refused(string sid)
    {
        Assert.Equal(LocalClientDecision.RefuseServiceAccount,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 0, sid, null), Restricted));
    }

    [Fact]
    public void Parent_in_the_own_session_is_allowed()
    {
        Assert.Equal(LocalClientDecision.Allow,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 1, Parent, Parent), Restricted));
        Assert.Equal(LocalClientDecision.Allow,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 0, Parent, null), Restricted));
    }
}

public class LocalClientPolicySecondReviewTests
{
    private const string Kid = "S-1-5-21-1-2-3-1001";
    private const string Parent = "S-1-5-21-1-2-3-1000";
    private static readonly IReadOnlySet<string> Restricted = new HashSet<string> { Kid };

    [Theory]
    [InlineData("S-1-5-90-0-1")]       // Window Manager (DWM)
    [InlineData("S-1-5-96-0-1")]       // font driver host (UMFD)
    [InlineData("S-1-5-80-123-456")]   // a service SID
    [InlineData("S-1-12-1-1-2-3-4")]   // an Azure AD account
    [InlineData("S-1-5-32-544")]       // a group, not a user
    public void Owner_that_is_not_a_user_account_is_refused(string sid)
    {
        Assert.Equal(LocalClientDecision.RefuseNotUserAccount,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 1, sid, Parent), Restricted));
    }

    [Fact]
    public void Session_user_that_is_not_a_user_account_is_refused()
    {
        Assert.Equal(LocalClientDecision.RefuseNotUserAccount,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 1, Parent, "S-1-5-90-0-1"), Restricted));
    }

    [Fact]
    public void Unknown_session_user_is_refused()
    {
        var owner = new ConnectionOwner(42, 2, Parent, null) { SessionUserKnown = false };
        Assert.Equal(LocalClientDecision.RefuseUnknownOwner, LocalClientPolicy.Decide(LoginSource.Local, owner, Restricted));
    }

    [Fact]
    public void Process_that_started_after_the_connection_is_refused()
    {
        var created = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
        var reused = new ConnectionOwner(42, 1, Parent, Parent) { ConnectionCreatedUtc = created, ProcessStartUtc = created.AddSeconds(5) };
        Assert.Equal(LocalClientDecision.RefuseProcessIdReused, LocalClientPolicy.Decide(LoginSource.Local, reused, Restricted));

        var normal = reused with { ProcessStartUtc = created.AddMinutes(-3) };
        Assert.Equal(LocalClientDecision.Allow, LocalClientPolicy.Decide(LoginSource.Local, normal, Restricted));
        // A small clock difference is allowed.
        var close = reused with { ProcessStartUtc = created.AddMilliseconds(500) };
        Assert.Equal(LocalClientDecision.Allow, LocalClientPolicy.Decide(LoginSource.Local, close, Restricted));
    }

    [Fact]
    public void Missing_times_do_not_refuse()
    {
        // N2: if Windows gives no create time, the compare is not possible (a recorded risk).
        var owner = new ConnectionOwner(42, 1, Parent, Parent) { ProcessStartUtc = DateTime.UtcNow };
        Assert.Equal(LocalClientDecision.Allow, LocalClientPolicy.Decide(LoginSource.Local, owner, Restricted));
    }

    [Fact]
    public void Child_is_still_refused_as_a_Restricted_User_first()
    {
        Assert.Equal(LocalClientDecision.RefuseRestrictedUser,
            LocalClientPolicy.Decide(LoginSource.Local, new ConnectionOwner(42, 2, Kid, Kid) { SessionUserKnown = true }, Restricted));
    }
}
