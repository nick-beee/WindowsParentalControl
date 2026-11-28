using ParentalControl.Domain.Requests;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Requests;

/// <summary>The Request Tokens (request-identity-from-launch, design D3).</summary>
public class RequestTokenStoreTests
{
    private const string KidSid = "S-1-5-21-1-2-3-1001";
    private const string Kid2Sid = "S-1-5-21-1-2-3-1002";

    private static readonly AgentLaunchRecord KidAgent =
        new(2, new ProcessIdentity(4100, new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc)), KidSid);

    private static readonly AgentLaunchRecord Kid2Agent =
        new(3, new ProcessIdentity(4200, new DateTime(2026, 11, 28, 8, 1, 0, DateTimeKind.Utc)), Kid2Sid);

    private long _nowMs = 1_000_000;
    private readonly AgentLaunchRegistry _registry = new();

    private RequestTokenStore NewStore()
    {
        _registry.Record(KidAgent);
        _registry.Record(Kid2Agent);
        return new RequestTokenStore(_registry, () => _nowMs);
    }

    [Fact]
    public void AnIssuedTokenGivesItsLaunchRecord()
    {
        var store = NewStore();
        var kid = store.Issue(KidAgent);
        var kid2 = store.Issue(Kid2Agent);

        Assert.NotNull(kid);
        Assert.Equal(RequestTokenStore.TokenChars, kid!.Length);
        Assert.True(RequestTokenStore.IsWellFormed(kid));
        Assert.Equal(KidAgent, store.Verify(kid));
        Assert.Equal(Kid2Agent, store.Verify(kid2));
        Assert.NotEqual(kid, kid2);
    }

    [Fact]
    public void TheTokenNamesNoUser()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent)!;
        Assert.DoesNotContain("1001", token, StringComparison.Ordinal);
        Assert.DoesNotContain("Kid", token, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // the right form, but no issued token
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 42 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 44 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padding
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // base64, not base64url
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    [InlineData("Kid")]
    public void AWrongOrMalformedTokenIsRefused(string? token)
    {
        var store = NewStore();
        store.Issue(KidAgent);
        Assert.Null(store.Verify(token));
    }

    [Fact]
    public void AVeryLongTokenIsRefusedBeforeTheHash()
    {
        var store = NewStore();
        store.Issue(KidAgent);
        Assert.False(RequestTokenStore.IsWellFormed(new string('A', 1_000_000)));
        Assert.Null(store.Verify(new string('A', 1_000_000)));
    }

    [Fact]
    public void AChangedCharacterIsRefused()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent)!;
        var changed = (token[0] == 'A' ? 'B' : 'A') + token[1..];
        Assert.Null(store.Verify(changed));
        Assert.Null(store.Verify(token.ToUpperInvariant() == token ? token.ToLowerInvariant() : token.ToUpperInvariant()));
    }

    [Fact]
    public void ATokenExpiresAfter60Minutes()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent);

        _nowMs += (long)RequestTokenStore.Lifetime.TotalMilliseconds - 1;
        Assert.Equal(KidAgent, store.Verify(token));

        _nowMs += 1;
        Assert.Null(store.Verify(token));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ANewTokenEndsTheOldTokenOfTheSameAgent()
    {
        var store = NewStore();
        var first = store.Issue(KidAgent);
        var other = store.Issue(Kid2Agent);
        var second = store.Issue(KidAgent);

        Assert.Null(store.Verify(first));
        Assert.Equal(KidAgent, store.Verify(second));
        Assert.Equal(Kid2Agent, store.Verify(other));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ATokenEndsWhenTheChannelOfItsAgentFails()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent);
        _registry.MarkEnded(KidAgent.SessionId, KidAgent.Process);
        Assert.Null(store.Verify(token));
    }

    [Fact]
    public void ATokenEndsWhenANewAgentStartsInTheSession()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent);
        var newAgent = KidAgent with { Process = new ProcessIdentity(5100, KidAgent.Process.StartUtc.AddMinutes(5)) };
        _registry.Record(newAgent);

        Assert.Null(store.Verify(token));
        var newToken = store.Issue(newAgent);
        Assert.Equal(newAgent, store.Verify(newToken));
    }

    [Fact]
    public void ATokenEndsWithItsSession()
    {
        var store = NewStore();
        var token = store.Issue(KidAgent);
        _registry.SessionEnded(KidAgent.SessionId);
        Assert.Null(store.Verify(token));
        Assert.Null(store.Issue(KidAgent));
    }

    [Fact]
    public void NoTokenForAnAgentThatDoesNotRun()
    {
        var store = NewStore();
        var stranger = new AgentLaunchRecord(7, new ProcessIdentity(9999, DateTime.UnixEpoch), KidSid);
        Assert.Null(store.Issue(stranger));
        Assert.Equal(0, store.Count);

        _registry.MarkEnded(KidAgent.SessionId, KidAgent.Process);
        Assert.Null(store.Issue(KidAgent));
    }

    [Fact]
    public void AStoreWithAnEmptyRegistryRefusesEachToken()
    {
        var store = new RequestTokenStore(new AgentLaunchRegistry(), () => _nowMs);
        Assert.Null(store.Issue(KidAgent));
        Assert.Null(store.Verify("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void TheTokenIsBase64UrlOfTheRandomBytes()
    {
        _registry.Record(KidAgent);
        var bytes = Enumerable.Range(0, RequestTokenStore.TokenBytes).Select(i => (byte)(0xF8 + i % 8)).ToArray();
        var store = new RequestTokenStore(_registry, () => _nowMs, () => bytes);
        var token = store.Issue(KidAgent)!;

        Assert.Equal(RequestTokenStore.TokenChars, token.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        var padded = token.Replace('-', '+').Replace('_', '/') + "=";
        Assert.Equal(bytes, Convert.FromBase64String(padded));
    }

    [Fact]
    public void ARandomSourceWithTheWrongLengthThrows()
    {
        _registry.Record(KidAgent);
        var store = new RequestTokenStore(_registry, () => _nowMs, () => new byte[16]);
        Assert.Throws<InvalidOperationException>(() => store.Issue(KidAgent));
    }

    [Fact]
    public void TheDefaultRandomSourceGivesNewTokens()
    {
        _registry.Record(KidAgent);
        var store = new RequestTokenStore(_registry);
        var tokens = Enumerable.Range(0, 20).Select(_ => store.Issue(KidAgent)).ToHashSet();
        Assert.Equal(20, tokens.Count);
    }
}
