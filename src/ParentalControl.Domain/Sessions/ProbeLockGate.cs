namespace ParentalControl.Domain.Sessions;

/// <summary>The reason of a probe lock (session-probe-and-agent-channel, design D12).</summary>
public enum ProbeLockKind
{
    /// <summary>The 6th blind sample or budget item within 10 minutes of unlocked time (design D6a).</summary>
    Budget,
    /// <summary>A blind sample after the first lock of the day (design D6a).</summary>
    Strike,
    /// <summary>A program that is never suspended counted for more than 60 seconds in an exhausted category (design D5).</summary>
    NeverSuspended,
    /// <summary>The set of Foreground Window exes of the day is full (design D5, N14).</summary>
    FullSet,
}

/// <summary>What the service writes for one "would lock" decision.</summary>
/// <param name="WriteEvent">Write one <c>PROBE_WOULD_LOCK</c> event now (at most one per user and minute).</param>
/// <param name="Decisions">The number of decisions since the last event, this one included.</param>
/// <param name="Alert">Send one Telegram alert (one per user, day and reason; for a program that is never suspended,
/// one per exe and day).</param>
public readonly record struct WouldLockReport(bool WriteEvent, int Decisions, bool Alert);

/// <summary>
/// The shadow mode of the probe locks (session-probe-and-agent-channel, design D12, S19, S22). While the setting
/// <c>probe_locks_enforced</c> is off, each probe lock is a "would lock": the service logs it and alerts the parent, and
/// the counting and the suspensions go on. <see cref="ProbeOutageMonitor"/> decides the budget lock and the strike, and
/// clears them at the switch to on; this gate decides the lock of a program that is never suspended and the full-set lock,
/// and limits the reports of all four reasons:
/// <list type="bullet">
/// <item>at most one <c>PROBE_WOULD_LOCK</c> event per Restricted User and minute, with the number of decisions;</item>
/// <item>one alert per Restricted User, day and reason;</item>
/// <item>for a program that is never suspended, one alert per exe and day that names the exe path.</item>
/// </list>
/// The day is the day of the Control Time Zone; the minute is monotonic. Not thread-safe: the caller holds one lock.
/// </summary>
public sealed class ProbeLockGate
{
    public static readonly TimeSpan EventInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, (TimeSpan At, int Pending)> _events = new();
    private readonly HashSet<(int UserId, DateOnly Day, ProbeLockKind Kind, string Exe)> _alerts = [];
    private DateOnly _alertDay;

    public ProbeLockGate(bool enforced) => Enforced = enforced;

    /// <summary>The setting <c>probe_locks_enforced</c>.</summary>
    public bool Enforced { get; private set; }

    /// <summary>Sets the setting. Returns true if it changed.</summary>
    public bool SetEnforced(bool on)
    {
        if (on == Enforced) return false;
        Enforced = on;
        return true;
    }

    /// <summary>The action for a probe lock of a program that is never suspended or of a full set.</summary>
    public ProbeLockAction Decide() => Enforced ? ProbeLockAction.Lock : ProbeLockAction.WouldLock;

    /// <summary>
    /// Records one "would lock" decision and gives what to write.
    /// </summary>
    /// <param name="exePath">The exe of a program that is never suspended; null for the other reasons.</param>
    /// <param name="monotonicNow">A monotonic time (for example the unbiased interrupt time).</param>
    public WouldLockReport WouldLock(int userId, DateOnly today, ProbeLockKind kind, string? exePath, TimeSpan monotonicNow)
    {
        if (today != _alertDay)
        {
            _alerts.Clear();
            _alertDay = today;
        }

        var writeEvent = true;
        var decisions = 1;
        if (_events.TryGetValue(userId, out var last))
        {
            decisions = last.Pending + 1;
            if (monotonicNow >= last.At && monotonicNow - last.At < EventInterval)
            {
                writeEvent = false;
                _events[userId] = (last.At, decisions);
            }
        }
        if (writeEvent) _events[userId] = (monotonicNow, 0);

        var alertKey = kind == ProbeLockKind.NeverSuspended
            ? (userId, today, kind, (exePath ?? "").ToUpperInvariant())
            : (userId, today, kind, "");
        var alert = _alerts.Add(alertKey);
        return new WouldLockReport(writeEvent, decisions, alert);
    }
}

/// <summary>
/// The log of the review of false counts (session-probe-and-agent-channel, design D12): one line for each sample in
/// which an entry other than <c>fg</c> adds a category that <c>fg</c> does not add, at most one line per exe, role and
/// hour. Not thread-safe.
/// </summary>
public sealed class ExtraCategoryLog
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Dictionary<(string Exe, ProbeRole Role), TimeSpan> _last = new();

    /// <summary>The counted processes of a sample whose category <c>fg</c> does not add and whose line is due now.</summary>
    public IReadOnlyList<CountedProcess> Due(IReadOnlyList<CountedProcess> counted, TimeSpan monotonicNow)
    {
        var foreground = counted.Where(c => c.Role == ProbeRole.Foreground).Select(c => c.Category).ToHashSet();
        var due = new List<CountedProcess>();
        foreach (var process in counted)
        {
            if (process.Role == ProbeRole.Foreground || foreground.Contains(process.Category)) continue;
            var key = (process.ExePath.ToUpperInvariant(), process.Role);
            if (_last.TryGetValue(key, out var at) && monotonicNow >= at && monotonicNow - at < Interval) continue;
            _last[key] = monotonicNow;
            due.Add(process);
        }
        return due;
    }
}

/// <summary>A set of daily markers: each key gives true once per day of the Control Time Zone. Not thread-safe.</summary>
public sealed class DailyOnce
{
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _day;

    /// <summary>True the first time a key comes on a day.</summary>
    public bool Take(string key, DateOnly today)
    {
        if (today != _day)
        {
            _taken.Clear();
            _day = today;
        }
        return _taken.Add(key);
    }
}
