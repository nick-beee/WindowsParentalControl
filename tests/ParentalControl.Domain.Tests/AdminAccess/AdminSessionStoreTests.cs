using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class AdminSessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_of_11_hours_59_minutes_is_valid()
    {
        var time = new ManualTime(T0);
        var store = new AdminSessionStore(time);
        var token = store.Issue(1);
        time.Advance(TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1));
        Assert.True(store.IsValid(token, 1));
    }

    [Fact]
    public void Session_of_12_hours_is_not_valid()
    {
        var time = new ManualTime(T0);
        var store = new AdminSessionStore(time);
        var token = store.Issue(1);
        time.Advance(TimeSpan.FromHours(12));
        Assert.False(store.IsValid(token, 1));
    }

    [Fact]
    public void Session_of_an_earlier_epoch_is_not_valid()
    {
        var store = new AdminSessionStore(new ManualTime(T0));
        var token = store.Issue(1);
        Assert.False(store.IsValid(token, 2));
        Assert.False(store.IsValid(token, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000")]
    public void Unknown_token_is_not_valid(string? token)
    {
        var store = new AdminSessionStore(new ManualTime(T0));
        store.Issue(1);
        Assert.False(store.IsValid(token, 1));
    }

    [Fact]
    public void Token_is_64_hex_characters_and_unique()
    {
        var store = new AdminSessionStore(new ManualTime(T0));
        var a = store.Issue(1);
        var b = store.Issue(1);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9A-F]{64}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Twenty_first_session_removes_the_first_one()
    {
        var time = new ManualTime(T0);
        var store = new AdminSessionStore(time);
        var tokens = new List<string>();
        for (var i = 0; i < AdminSessionStore.MaxSessions + 1; i++)
        {
            tokens.Add(store.Issue(1));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(AdminSessionStore.MaxSessions, store.Count);
        Assert.False(store.IsValid(tokens[0], 1));
        Assert.True(store.IsValid(tokens[1], 1));
        Assert.True(store.IsValid(tokens[^1], 1));
    }

    [Fact]
    public void Issue_removes_expired_sessions()
    {
        var time = new ManualTime(T0);
        var store = new AdminSessionStore(time);
        store.Issue(1);
        store.Issue(1);
        time.Advance(TimeSpan.FromHours(13));
        store.Issue(1);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Logout_ends_the_session()
    {
        var store = new AdminSessionStore(new ManualTime(T0));
        var token = store.Issue(1);
        var other = store.Issue(1);
        store.End(token);
        Assert.False(store.IsValid(token, 1));
        Assert.True(store.IsValid(other, 1));
    }

    [Fact]
    public void New_store_after_a_restart_knows_no_session()
    {
        var token = new AdminSessionStore(new ManualTime(T0)).Issue(1);
        Assert.False(new AdminSessionStore(new ManualTime(T0)).IsValid(token, 1));
    }
}
