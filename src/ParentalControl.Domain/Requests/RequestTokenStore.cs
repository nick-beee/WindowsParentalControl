using System.Security.Cryptography;
using System.Text;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Requests;

/// <summary>
/// The Request Tokens of the Request Window (request-identity-from-launch, design D3). The service gives a token to the
/// Session Agent through its Agent Channel, and the routes <c>/request</c> and <c>/api/request</c> take the user and
/// the session only from the launch record of a valid token, never from the request.
/// <para>
/// A token is 32 random bytes as base64url without padding. The store keeps only its SHA-256 hash. A token is valid
/// for <see cref="Lifetime"/> after its issue, only while its launch record is the running record of its session in the
/// <see cref="AgentLaunchRegistry"/>, and only until the same record gets a new token. So a channel fault, a new agent
/// in the session and the end of the session each end the token. The child can read its own token: it proves only
/// which user sends a request. The class is thread-safe and uses a monotonic clock.
/// </para>
/// </summary>
public sealed class RequestTokenStore
{
    /// <summary>The cookie that carries the token on each request of the Request Window.</summary>
    public const string CookieName = "pc_request_token";

    /// <summary>The random bytes of a token.</summary>
    public const int TokenBytes = 32;

    /// <summary>The characters of a token: base64url of <see cref="TokenBytes"/> without padding.</summary>
    public const int TokenChars = 43;

    /// <summary>A token is valid for this time after its issue.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    private readonly Lock _gate = new();
    private readonly AgentLaunchRegistry _agents;
    private readonly Func<long> _nowMs;
    private readonly Func<byte[]> _random;
    private readonly Dictionary<AgentLaunchRecord, Entry> _byLaunch = new();

    /// <param name="agents">The launch records of the running agents.</param>
    /// <param name="nowMs">The monotonic clock in milliseconds; default <see cref="Environment.TickCount64"/>.</param>
    /// <param name="random">The source of the <see cref="TokenBytes"/> random bytes of a token (for a test).</param>
    public RequestTokenStore(AgentLaunchRegistry agents, Func<long>? nowMs = null, Func<byte[]>? random = null)
    {
        _agents = agents;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _random = random ?? (() => RandomNumberGenerator.GetBytes(TokenBytes));
    }

    /// <summary>
    /// A new token for the launch record, or null if the record is not the running record of its session. The token
    /// replaces the earlier token of the same record.
    /// </summary>
    public string? Issue(AgentLaunchRecord launch)
    {
        var bytes = _random();
        if (bytes.Length != TokenBytes)
            throw new InvalidOperationException($"A token needs {TokenBytes} random bytes.");
        var token = Base64Url(bytes);
        var now = _nowMs();
        lock (_gate)
        {
            Prune(now);
            if (!IsRunning(launch)) return null;
            _byLaunch[launch] = new Entry(Hash(token), now + (long)Lifetime.TotalMilliseconds);
        }
        return token;
    }

    /// <summary>The launch record of a valid token, else null.</summary>
    public AgentLaunchRecord? Verify(string? token)
    {
        if (!IsWellFormed(token)) return null;
        var hash = Hash(token!);
        var now = _nowMs();
        lock (_gate)
        {
            Prune(now);
            AgentLaunchRecord? found = null;
            // At most one entry per session. Each entry is compared, so the time does not tell which one matched.
            foreach (var (launch, entry) in _byLaunch)
            {
                if (CryptographicOperations.FixedTimeEquals(entry.Hash, hash))
                    found = launch;
            }
            return found;
        }
    }

    /// <summary>The number of tokens that the store holds now (for a test).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byLaunch.Count;
        }
    }

    /// <summary>True for a string of exactly <see cref="TokenChars"/> base64url characters.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TokenChars) return false;
        foreach (var c in token)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        }
        return true;
    }

    // Under _gate: removes each expired token and each token whose record is no longer the running record.
    private void Prune(long now)
    {
        List<AgentLaunchRecord>? remove = null;
        foreach (var (launch, entry) in _byLaunch)
        {
            if (now >= entry.ExpiresMs || !IsRunning(launch))
                (remove ??= new()).Add(launch);
        }
        if (remove is null) return;
        foreach (var launch in remove)
            _byLaunch.Remove(launch);
    }

    private bool IsRunning(AgentLaunchRecord launch) => _agents.Running(launch.SessionId) == launch;

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(byte[] Hash, long ExpiresMs);
}
