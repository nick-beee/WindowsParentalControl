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
