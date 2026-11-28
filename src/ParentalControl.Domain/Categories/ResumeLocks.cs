namespace ParentalControl.Domain.Categories;

/// <summary>
/// A recorded process in one check: a process that runs again and that the planner suspends again (a
/// <see cref="SuspendAction"/> with <c>IsRecorded</c>), or a process that the check sees suspended outside a Session End
/// Hold and a Session End Guard.
/// </summary>
/// <param name="CpuTime">The user-mode CPU time of the process in this check, or null if the read failed.</param>
public sealed record ResumedProcess(int Pid, DateTime StartTimeUtc, int UserId, string ExePath, TimeSpan? CpuTime);

/// <summary>
/// A watched process that the thread check sees running, but without evidence that it ran (design D10). It does not
/// count, and it stays watched with its old CPU time.
/// </summary>
/// <param name="CpuGrowth">The CPU time since the service last saw it suspended, or null if one of the two is unknown.</param>
public sealed record UnprovenResume(ResumedProcess Process, ResumeEvidence Evidence, TimeSpan? CpuGrowth);

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

/// <summary>
/// The stored form of a Resume Lock (table <c>resume_locks</c>): the day of the Control Time Zone and the Bonus Minutes of
/// the user at its start, and its resumed exes. The service restores it after a restart (design D6).
/// </summary>
public sealed record ResumeLockState(int UserId, DateOnly Day, int BonusAtStart, IReadOnlyList<string> ExePaths);

/// <summary>
/// The result of one check: the users with a counted Child Resume, the Resume Locks that start, the users whose running
/// Resume Lock got a new exe (<see cref="Extended"/>; the caller stores it again), and the watched processes that look
/// resumed without evidence (for the log).
/// </summary>
public sealed record ResumeCheck(IReadOnlyList<int> Counted, IReadOnlyList<ResumeLockStart> Started, IReadOnlyList<UnprovenResume> Unproven,
    IReadOnlyList<int> Extended);

/// <summary>
/// Counts the Child Resumes of each Restricted User and decides the Resume Lock (issue #24, change
/// <c>lock-after-repeated-resume</c>).
/// <para>
/// A process is watched after the service suspended it, or saw it suspended outside a Session End Hold and a Session End
/// Guard. For each watched process it keeps the user-mode CPU time from the last time that it saw the process suspended.
/// A Child Resume is a check that finds a watched process with evidence of a run (its CPU time grew by at least
/// <see cref="ChildResumeEvidence.MinCpuGrowth"/> since then, <see cref="ChildResumeEvidence"/>, design D10) while its
/// reason still applies. The check finds it running again (the caller passes the recorded processes that the planner
/// suspends again), or suspended again (the caller passes the recorded processes that it sees suspended outside a hold and
/// a guard, design D11): a script of the child can suspend the game before each check and resume it after. A running
/// process without this evidence does not count and stays watched. The service unwatches a process that it resumed
/// itself or that ran for the end of its session, so such a process does not count until it is suspended again.
/// </para>
/// <para>
/// One check counts at most one Child Resume per user, and a check less than <see cref="MinGap"/> after the last counted
/// Child Resume of the user does not count: one tool run resumes many processes at once. <see cref="Threshold"/> Child
/// Resumes of one user in <see cref="Window"/>, of any exes, start a Resume Lock. The lock ends on a new day, after new
/// Bonus Minutes, or when no resumed exe still has a reason to stay suspended; then the count starts again at 0.
/// </para>
/// <para>
/// The times are monotonic; the caller gives them. The class keeps its state in memory; the caller stores the Resume
/// Locks (<see cref="LockState"/>) and gives them back after a restart (<see cref="Restore"/>). The count of Child Resumes
/// is not stored. Thread-safe: each member takes a small lock of its own, so the service can unwatch the processes of a
/// session at a session-end request without the gate of the enforcer (design D12).
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

    private readonly object _sync = new();
    // The watched processes, with the CPU time when the service last saw each one suspended (null: unknown).
    private readonly Dictionary<(int Pid, DateTime StartTimeUtc), TimeSpan?> _watched = new();
    private readonly Dictionary<int, List<Strike>> _strikes = new();
    private readonly Dictionary<int, DateTime> _lastCounted = new();
    private readonly Dictionary<int, Lock> _locks = new();

    /// <summary>
    /// The service suspended the process, or saw it suspended outside a hold and a guard. <paramref name="cpuTime"/> is the
    /// CPU time of the process at that time, read after the suspension. A null value (a failed read) keeps a known CPU time.
    /// </summary>
    /// <remarks>
    /// Call it after <c>Suspend</c> = <c>Ok</c>: it sets the CPU time without a comparison. A check that sees a recorded
    /// process suspended goes through <see cref="Check(IEnumerable{ResumedProcess}, IEnumerable{ResumedProcess}, DateTime, DateOnly, Func{int, int})"/>,
    /// which compares the CPU time first (design D11).
    /// </remarks>
    public void MarkSuspended(int pid, DateTime startTimeUtc, TimeSpan? cpuTime)
    {
        lock (_sync)
            SetBaseline((pid, startTimeUtc), cpuTime);
    }

    // A null value (a failed read) keeps a known CPU time.
    private void SetBaseline((int Pid, DateTime StartTimeUtc) key, TimeSpan? cpuTime)
    {
        if (cpuTime is null && _watched.TryGetValue(key, out var known) && known is not null)
            return;
        _watched[key] = cpuTime;
    }

    /// <summary>
    /// The service resumed the process itself (time left, block removed, service stop, the end of the session), or the
    /// Session Agent resumed it for the end of the session under a guard. It does not count until it is watched again.
    /// </summary>
    public void MarkResumedByService(int pid, DateTime startTimeUtc)
    {
        lock (_sync)
            _watched.Remove((pid, startTimeUtc));
    }

    /// <summary>True if the process is watched: a run of it again counts as a Child Resume.</summary>
    public bool IsWatched(int pid, DateTime startTimeUtc)
    {
        lock (_sync)
            return _watched.ContainsKey((pid, startTimeUtc));
    }

    /// <summary>Forgets the watched processes that have no suspension record any more.</summary>
    public void Retain(IReadOnlySet<(int Pid, DateTime StartTimeUtc)> recorded)
    {
        lock (_sync)
        {
            foreach (var key in _watched.Keys.Where(k => !recorded.Contains(k)).ToList())
                _watched.Remove(key);
        }
    }

    /// <summary>True if a Resume Lock of the user runs.</summary>
    public bool IsLocked(int userId)
    {
        lock (_sync)
            return _locks.ContainsKey(userId);
    }

    /// <summary>The counted Child Resumes of the user in the current window, for the log. 0 during a lock.</summary>
    public int ChildResumes(int userId)
    {
        lock (_sync)
            return _strikes.TryGetValue(userId, out var strikes) ? strikes.Count : 0;
    }

    /// <summary>A copy of the users with a Resume Lock.</summary>
    public IReadOnlyList<int> LockedUsers
    {
        get
        {
            lock (_sync)
                return _locks.Keys.Order().ToList();
        }
    }

    /// <summary>The resumed exes of the Resume Lock of the user, or an empty list.</summary>
    public IReadOnlyList<string> LockedExes(int userId)
    {
        lock (_sync)
            return _locks.TryGetValue(userId, out var l) ? l.ExePaths.Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
    }

    /// <summary>The stored form of the Resume Lock of the user, or null if no lock runs.</summary>
    public ResumeLockState? LockState(int userId)
    {
        lock (_sync)
            return _locks.TryGetValue(userId, out var l)
                ? new ResumeLockState(userId, l.Day, l.BonusAtStart, l.ExePaths.Order(StringComparer.OrdinalIgnoreCase).ToList())
                : null;
    }

    /// <summary>
    /// Restores a stored Resume Lock after a restart. It starts no new lock in the sense of <see cref="ResumeCheck.Started"/>
    /// (no event, no alert). It keeps a lock of the user that runs already. The next <see cref="Release"/> ends it if its
    /// reason ended. Returns false if a lock of the user runs already or the state has no exe.
    /// </summary>
    public bool Restore(ResumeLockState state)
    {
        lock (_sync)
        {
            if (_locks.ContainsKey(state.UserId) || state.ExePaths.Count == 0)
                return false;
            _locks[state.UserId] = new Lock(state.Day, state.BonusAtStart,
                new HashSet<string>(state.ExePaths, StringComparer.OrdinalIgnoreCase));
            return true;
        }
    }

    /// <summary>One check without processes seen suspended. See the other overload.</summary>
    public ResumeCheck Check(IEnumerable<ResumedProcess> resumed, DateTime now, DateOnly day, Func<int, int> bonusMinutes) =>
        Check(resumed, [], now, day, bonusMinutes);

    /// <summary>
    /// One check. <paramref name="resumed"/> are the recorded processes that run again and that the planner suspends again.
    /// Each watched one with evidence (design D10) is unwatched, so it does not count again while its Pending Suspension
    /// runs. A watched one without evidence does not count and stays watched with its old CPU time.
    /// <paramref name="seenSuspended"/> are the recorded processes that this check sees suspended outside a hold and a
    /// guard, while their reason still applies (design D11). An unwatched one is watched now with its CPU time. A watched
    /// one with evidence counts like a running one and stays watched: its CPU time now is the new baseline. If the
    /// 30-second gap stops the count, it keeps its old baseline, so the evidence counts at a later check. A watched one
    /// without evidence gets its CPU time now as the new baseline (a failed read keeps the known CPU time).
    /// </summary>
    /// <param name="now">A monotonic time.</param>
    /// <param name="day">The day of the Control Time Zone.</param>
    /// <param name="bonusMinutes">The Bonus Minutes of the user today.</param>
    public ResumeCheck Check(IEnumerable<ResumedProcess> resumed, IEnumerable<ResumedProcess> seenSuspended, DateTime now, DateOnly day,
        Func<int, int> bonusMinutes)
    {
        lock (_sync)
        {
            var byUser = new Dictionary<int, List<string>>();
            // The processes seen suspended with evidence: their new baseline if the check counts.
            var ranWhileUnseen = new Dictionary<int, List<ResumedProcess>>();
            var unproven = new List<UnprovenResume>();

            void AddPath(int userId, string path)
            {
                if (!byUser.TryGetValue(userId, out var paths))
                    byUser[userId] = paths = [];
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            foreach (var p in resumed)
            {
                var key = (p.Pid, p.StartTimeUtc);
                if (!_watched.TryGetValue(key, out var suspendedCpu))
                    continue;
                var evidence = ChildResumeEvidence.Judge(suspendedCpu, p.CpuTime);
                if (evidence != ResumeEvidence.Ran)
                {
                    unproven.Add(new UnprovenResume(p, evidence, p.CpuTime - suspendedCpu));
                    continue;
                }
                _watched.Remove(key);
                AddPath(p.UserId, p.ExePath);
            }

            foreach (var p in seenSuspended)
            {
                var key = (p.Pid, p.StartTimeUtc);
                if (!_watched.TryGetValue(key, out var suspendedCpu)
                    || ChildResumeEvidence.Judge(suspendedCpu, p.CpuTime) != ResumeEvidence.Ran)
                {
                    // Watched now, or no run since the last baseline: the CPU time now is the new baseline.
                    SetBaseline(key, p.CpuTime);
                    continue;
                }
                AddPath(p.UserId, p.ExePath);
                if (!ranWhileUnseen.TryGetValue(p.UserId, out var list))
                    ranWhileUnseen[p.UserId] = list = [];
                list.Add(p);
            }

            var counted = new List<int>();
            var started = new List<ResumeLockStart>();
            var extended = new List<int>();
            foreach (var (userId, paths) in byUser.OrderBy(e => e.Key))
            {
                if (_lastCounted.TryGetValue(userId, out var last) && now - last < MinGap)
                    continue;
                _lastCounted[userId] = now;
                counted.Add(userId);
                if (ranWhileUnseen.TryGetValue(userId, out var seen))
                    foreach (var p in seen)
                        _watched[(p.Pid, p.StartTimeUtc)] = p.CpuTime;

                if (_locks.TryGetValue(userId, out var running))
                {
                    // A Child Resume during a lock keeps the lock due for its exes too, and starts no second lock.
                    var before = running.ExePaths.Count;
                    running.ExePaths.UnionWith(paths);
                    if (running.ExePaths.Count != before)
                        extended.Add(userId);
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

            return new ResumeCheck(counted, started, unproven, extended);
        }
    }

    /// <summary>
    /// Ends each Resume Lock whose reason ended: a new day, more Bonus Minutes than at its start, or no resumed exe that
    /// <paramref name="stillDue"/> keeps (no time left, or a Blocked Program). The count of the user starts again at 0, and
    /// the 30-second gap of the user ends too.
    /// </summary>
    public IReadOnlyList<ResumeLockEnd> Release(DateOnly day, Func<int, int> bonusMinutes, Func<int, string, bool> stillDue)
    {
        lock (_sync)
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
                _lastCounted.Remove(userId);
                ended.Add(new ResumeLockEnd(userId, r));
            }
            return ended;
        }
    }
}
