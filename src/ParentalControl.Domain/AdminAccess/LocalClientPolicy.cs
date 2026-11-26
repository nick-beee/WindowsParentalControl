namespace ParentalControl.Domain.AdminAccess;

/// <summary>The process that owns the client end of a TCP connection from this PC.</summary>
/// <param name="ProcessId">The owner process from the TCP table.</param>
/// <param name="SessionId">The Windows session of the process.</param>
/// <param name="UserSid">The user of the process token, or null if the service could not read it.</param>
/// <param name="SessionUserSid">The user who is logged on to that session, or null if nobody is (session 0).</param>
public sealed record ConnectionOwner(int ProcessId, int SessionId, string? UserSid, string? SessionUserSid);

public enum LocalClientDecision
{
    /// <summary>A remote source, or a process on this PC that is not of a Restricted User.</summary>
    Allow,
    /// <summary>The process belongs to a Restricted User, or it runs in the session of a Restricted User.</summary>
    RefuseRestrictedUser,
    /// <summary>The source is this PC, but the service could not find the owner process or its user.</summary>
    RefuseUnknownOwner,
    /// <summary>The owner is a service account. Such a process can send requests for any user.</summary>
    RefuseServiceAccount,
}

/// <summary>
/// Decides whether a connection may log in or use an Admin Session (require-admin-sign-in, finding S4).
/// The parent must not sign in from the session of the child: the child sees the screen and can use the signed-in
/// browser. The service maps a connection from this PC to its owner process. If the owner is not known, the decision
/// is to refuse (fail closed); the parent then uses the Admin app or a phone or laptop on the LAN.
/// </summary>
public static class LocalClientPolicy
{
    private static readonly HashSet<string> ServiceAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-19", // LocalService
        "S-1-5-20", // NetworkService
    };

    /// <param name="source">The login source from <see cref="LoginSource.Of"/>.</param>
    /// <param name="owner">The owner of the client end of the connection, or null if it is not known.</param>
    /// <param name="restrictedSids">The SIDs of all Restricted Users.</param>
    public static LocalClientDecision Decide(string source, ConnectionOwner? owner, IReadOnlySet<string> restrictedSids)
    {
        // A connection without a remote address cannot be mapped to a process.
        if (source == LoginSource.Unknown)
            return LocalClientDecision.RefuseUnknownOwner;
        if (source != LoginSource.Local)
            return LocalClientDecision.Allow;

        if (owner is null || string.IsNullOrEmpty(owner.UserSid))
            return LocalClientDecision.RefuseUnknownOwner;
        if (IsRestricted(owner.UserSid, restrictedSids) || IsRestricted(owner.SessionUserSid, restrictedSids))
            return LocalClientDecision.RefuseRestrictedUser;
        if (ServiceAccounts.Contains(owner.UserSid))
            return LocalClientDecision.RefuseServiceAccount;
        return LocalClientDecision.Allow;
    }

    private static bool IsRestricted(string? sid, IReadOnlySet<string> restrictedSids) =>
        !string.IsNullOrEmpty(sid) && restrictedSids.Any(r => string.Equals(r, sid, StringComparison.OrdinalIgnoreCase));
}
