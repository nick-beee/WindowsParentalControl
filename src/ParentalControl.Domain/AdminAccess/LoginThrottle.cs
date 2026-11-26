namespace ParentalControl.Domain.AdminAccess;

/// <summary>One failed login: the source (see <see cref="LoginSource"/>) and the time.</summary>
public sealed record LoginFailure(string Source, DateTimeOffset FailedAtUtc);

public enum LoginLimitScope
{
    None,
    /// <summary>The source has <see cref="LoginThrottle.MaxPerSource"/> failures in the window.</summary>
    Source,
    /// <summary>All sources together have <see cref="LoginThrottle.MaxTotal"/> failures in the window.</summary>
    AllSources,
}

/// <param name="Blocked">True if the login must refuse the code without a check.</param>
/// <param name="Scope">The limit that blocks. If both limits block, <see cref="LoginLimitScope.AllSources"/>.</param>
/// <param name="BlockedUntilUtc">The time when the block ends, if the login is blocked.</param>
public sealed record LoginLimitStatus(bool Blocked, LoginLimitScope Scope, DateTimeOffset? BlockedUntilUtc)
{
    public static readonly LoginLimitStatus Open = new(false, LoginLimitScope.None, null);
}

/// <summary>
/// The limit on failed logins: at most <see cref="MaxPerSource"/> failures from one source and
/// <see cref="MaxTotal"/> from all sources in a moving window of 24 hours.
/// The caller records a failure only for a code that it checked. A login that the limit refused
/// is not a new failure, so a block always ends when the oldest failure in it is 24 hours old.
/// </summary>
public static class LoginThrottle
{
    public const int MaxPerSource = 10;
    public const int MaxTotal = 30;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Tells whether a login from <paramref name="source"/> may be checked now.</summary>
    public static LoginLimitStatus Check(IEnumerable<LoginFailure> failures, string source, DateTimeOffset nowUtc)
    {
        var inWindow = InWindow(failures, nowUtc);
        var ofSource = inWindow.Where(f => f.Source == source).ToList();

        var sourceEnd = BlockEnd(ofSource, MaxPerSource);
        var totalEnd = BlockEnd(inWindow, MaxTotal);

        if (totalEnd is not null)
        {
            var end = sourceEnd is not null && sourceEnd > totalEnd ? sourceEnd : totalEnd;
            return new LoginLimitStatus(true, LoginLimitScope.AllSources, end);
        }
        if (sourceEnd is not null)
            return new LoginLimitStatus(true, LoginLimitScope.Source, sourceEnd);
        return LoginLimitStatus.Open;
    }

    /// <summary>
    /// Tells whether one new failure from <paramref name="source"/> at <paramref name="nowUtc"/> makes a limit apply.
    /// <paramref name="failuresBefore"/> are the failures without the new one. The result is true for the one failure
    /// that reaches a limit, also when it reaches both limits together, so the caller sends one alert.
    /// </summary>
    public static LoginLimitStatus AfterFailure(IEnumerable<LoginFailure> failuresBefore, string source, DateTimeOffset nowUtc)
    {
        var before = InWindow(failuresBefore, nowUtc);
        var wasBlocked = Check(before, source, nowUtc).Blocked;
        var after = Check(before.Append(new LoginFailure(source, nowUtc)), source, nowUtc);
        return !wasBlocked && after.Blocked ? after : LoginLimitStatus.Open;
    }

    private static List<LoginFailure> InWindow(IEnumerable<LoginFailure> failures, DateTimeOffset nowUtc) =>
        failures.Where(f => f.FailedAtUtc > nowUtc - Window && f.FailedAtUtc <= nowUtc).ToList();

    /// <summary>
    /// With n failures and a limit of m (n ≥ m), the block ends when n − m + 1 failures have left the window,
    /// that is 24 hours after the failure at index n − m in time order.
    /// </summary>
    private static DateTimeOffset? BlockEnd(List<LoginFailure> failures, int limit)
    {
        if (failures.Count < limit)
            return null;
        var ordered = failures.OrderBy(f => f.FailedAtUtc).ToList();
        return ordered[ordered.Count - limit].FailedAtUtc + Window;
    }
}
