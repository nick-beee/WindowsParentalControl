namespace ParentalControl.Domain.Categories;

/// <summary>A recorded process that runs again and that the planner suspends again (a <see cref="SuspendAction"/> with <c>IsRecorded</c>).</summary>
public sealed record ResumedProcess(int Pid, DateTime StartTimeUtc, int UserId, string ExePath);

/// <summary>A Resume Lock that starts now. <see cref="ExePaths"/> are the resumed exes of its Child Resumes.</summary>
public sealed record ResumeLockStart(int UserId, IReadOnlyList<string> ExePaths);

/// <summary>Why a Resume Lock ends.</summary>
public enum ResumeLockEndReason
{
    /// <summary>The day of the Control Time Zone changed.</summary>
    NewDay,

    /// <summary>The parent gave Bonus Minutes after the start of the lock.</summary>
    BonusMinutes,

    /// <summary>No resumed exe still has no time left or is still a Blocked Program.</summary>
    NoReasonLeft,
}

public sealed record ResumeLockEnd(int UserId, ResumeLockEndReason Reason);

/// <summary>The result of one check: the users with a counted Child Resume, and the Resume Locks that start.</summary>
public sealed record ResumeCheck(IReadOnlyList<int> Counted, IReadOnlyList<ResumeLockStart> Started);

/// <summary>
/// Counts the Child Resumes of each Restricted User and decides the Resume Lock (issue #24, change
/// <c>lock-after-repeated-resume</c>).
/// <para>
/// A process is watched after the service suspended it, or saw it suspended outside a Session End Hold and a Session End
/// Guard. A Child Resume is a check that finds a watched process running again while its reason still applies (the
/// caller passes the recorded processes that the planner suspends again). The service unwatches a process that it
/// resumed itself or that ran for the end of its session, so such a process does not count until it is suspended again.
/// </para>
/// <para>
/// One check counts at most one Child Resume per user, and a check less than <see cref="MinGap"/> after the last counted
/// Child Resume of the user does not count: one tool run resumes many processes at once. <see cref="Threshold"/> Child
/// Resumes of one user in <see cref="Window"/>, of any exes, start a Resume Lock. The lock ends on a new day, after new
/// Bonus Minutes, or when no resumed exe still has a reason to stay suspended; then the count starts again at 0.
/// </para>
/// <para>
/// The times are monotonic; the caller gives them. The state is in memory only. Not thread-safe: the caller holds a lock.
/// </para>
/// </summary>
public sealed class ResumeLocks
{
    /// <summary>The number of Child Resumes of one user in <see cref="Window"/> that start a Resume Lock.</summary>
    public const int Threshold = 3;

    /// <summary>The sliding window of the count.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>A check less than this time after the last counted Child Resume of the user does not count.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private sealed record Strike(DateTime At, IReadOnlyList<string> ExePaths);

    private sealed class Lock(DateOnly day, int bonusAtStart, HashSet<string> exePaths)
    {
        public DateOnly Day { get; } = day;
        public int BonusAtStart { get; } = bonusAtStart;
        public HashSet<string> ExePaths { get; } = exePaths;
    }

    private readonly HashSet<(int Pid, DateTime StartTimeUtc)> _watched = new();
    private readonly Dictionary<int, List<Strike>> _strikes = new();
    private readonly Dictionary<int, DateTime> _lastCounted = new();
    private readonly Dictionary<int, Lock> _locks = new();

    /// <summary>The service suspended the process, or saw it suspended outside a hold and a guard.</summary>
    public void MarkSuspended(int pid, DateTime startTimeUtc) => _watched.Add((pid, startTimeUtc));

    /// <summary>
    /// The service resumed the process itself (time left, block removed, service stop, the end of the session), or the
    /// Session Agent resumed it for the end of the session under a guard. It does not count until it is watched again.
    /// </summary>
    public void MarkResumedByService(int pid, DateTime startTimeUtc) => _watched.Remove((pid, startTimeUtc));

    /// <summary>True if the process is watched: a run of it again counts as a Child Resume.</summary>
    public bool IsWatched(int pid, DateTime startTimeUtc) => _watched.Contains((pid, startTimeUtc));

    /// <summary>Forgets the watched processes that have no suspension record any more.</summary>
    public void Retain(IReadOnlySet<(int Pid, DateTime StartTimeUtc)> recorded) => _watched.RemoveWhere(k => !recorded.Contains(k));

    /// <summary>True if a Resume Lock of the user runs.</summary>
    public bool IsLocked(int userId) => _locks.ContainsKey(userId);

    /// <summary>The counted Child Resumes of the user in the current window, for the log. 0 during a lock.</summary>
    public int ChildResumes(int userId) => _strikes.TryGetValue(userId, out var strikes) ? strikes.Count : 0;

    /// <summary>The users with a Resume Lock.</summary>
    public IReadOnlyCollection<int> LockedUsers => _locks.Keys;

    /// <summary>The resumed exes of the Resume Lock of the user, or an empty list.</summary>
    public IReadOnlyList<string> LockedExes(int userId) =>
        _locks.TryGetValue(userId, out var l) ? l.ExePaths.Order(StringComparer.OrdinalIgnoreCase).ToList() : [];

    /// <summary>
    /// One check. <paramref name="resumed"/> are the recorded processes that run again and that the planner suspends again.
    /// Each watched one is unwatched, so it does not count again while its Pending Suspension runs.
    /// </summary>
    /// <param name="now">A monotonic time.</param>
    /// <param name="day">The day of the Control Time Zone.</param>
    /// <param name="bonusMinutes">The Bonus Minutes of the user today.</param>
    public ResumeCheck Check(IEnumerable<ResumedProcess> resumed, DateTime now, DateOnly day, Func<int, int> bonusMinutes)
    {
        var byUser = new Dictionary<int, List<string>>();
        foreach (var p in resumed)
        {
            if (!_watched.Remove((p.Pid, p.StartTimeUtc)))
                continue;
            if (!byUser.TryGetValue(p.UserId, out var paths))
                byUser[p.UserId] = paths = [];
            if (!paths.Contains(p.ExePath, StringComparer.OrdinalIgnoreCase))
                paths.Add(p.ExePath);
        }

        var counted = new List<int>();
        var started = new List<ResumeLockStart>();
        foreach (var (userId, paths) in byUser.OrderBy(e => e.Key))
        {
            if (_lastCounted.TryGetValue(userId, out var last) && now - last < MinGap)
                continue;
            _lastCounted[userId] = now;
            counted.Add(userId);

            if (_locks.TryGetValue(userId, out var running))
            {
                // A Child Resume during a lock keeps the lock due for its exes too, and starts no second lock.
                running.ExePaths.UnionWith(paths);
                continue;
            }

            if (!_strikes.TryGetValue(userId, out var strikes))
                _strikes[userId] = strikes = [];
            strikes.Add(new Strike(now, paths));
            strikes.RemoveAll(s => now - s.At >= Window);
            if (strikes.Count < Threshold)
                continue;

            var exes = new HashSet<string>(strikes.SelectMany(s => s.ExePaths), StringComparer.OrdinalIgnoreCase);
            _locks[userId] = new Lock(day, bonusMinutes(userId), exes);
            _strikes.Remove(userId);
            started.Add(new ResumeLockStart(userId, exes.Order(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new ResumeCheck(counted, started);
    }

    /// <summary>
    /// Ends each Resume Lock whose reason ended: a new day, more Bonus Minutes than at its start, or no resumed exe that
    /// <paramref name="stillDue"/> keeps (no time left, or a Blocked Program). The count of the user starts again at 0.
    /// </summary>
    public IReadOnlyList<ResumeLockEnd> Release(DateOnly day, Func<int, int> bonusMinutes, Func<int, string, bool> stillDue)
    {
        var ended = new List<ResumeLockEnd>();
        foreach (var (userId, l) in _locks.OrderBy(e => e.Key).ToList())
        {
            ResumeLockEndReason? reason =
                l.Day != day ? ResumeLockEndReason.NewDay
                : bonusMinutes(userId) > l.BonusAtStart ? ResumeLockEndReason.BonusMinutes
                : !l.ExePaths.Any(path => stillDue(userId, path)) ? ResumeLockEndReason.NoReasonLeft
                : null;
            if (reason is not { } r)
                continue;

            _locks.Remove(userId);
            _strikes.Remove(userId);
            ended.Add(new ResumeLockEnd(userId, r));
        }
        return ended;
    }
}
