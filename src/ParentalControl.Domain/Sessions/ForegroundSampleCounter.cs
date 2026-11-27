using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Sessions;

/// <summary>The time that one Session Probe sample adds, and what it reports.</summary>
/// <param name="Time">The time for each category of the counted processes of the sample (zero without one).</param>
/// <param name="GapItem">The sample is one budget item of the gap rule (never the strike).</param>
/// <param name="LongGap">The gap or the sample took more than 12 seconds: one <c>PROBE_GAP</c> event.</param>
/// <param name="Gap">The time since the last accepted sample of the session (zero for a first sample).</param>
public readonly record struct SampleCredit(TimeSpan Time, bool GapItem, bool LongGap, TimeSpan Gap);

/// <summary>
/// Counts the time of the Session Probe samples per session (session-probe-and-agent-channel, design D5, the gap rule).
/// The sample time is the <c>t</c> of the probe (<c>GetTickCount64</c>, S9), so a late read of the service does not
/// change the time.
/// <list type="bullet">
/// <item>A gap of 12 seconds or less counts in full.</item>
/// <item>A longer gap counts in full, at most 60 seconds, if neither sample is blind and both have the same counted
/// processes: the same categories and App Rule exes, without the <c>cursor</c> entries. Two samples without a counted
/// process are the same; they add nothing. A gap over 60 seconds is also one gap item.</item>
/// <item>Each other longer gap counts 12 seconds and is one gap item.</item>
/// <item>A sample that took more than 12 seconds counts at most 12 seconds and is one gap item.</item>
/// <item>A gap over an unexpected end of the probe gives no gap item (S15); the last-sample time stays.</item>
/// <item>The first sample after a planned start (sign-in, unlock, connect, service start) or a resume adds zero, and so
/// does a sample after a sleep that the backstop finds (S20).</item>
/// </list>
/// <para>
/// The sleep backstop: at each accepted line the service gives its own <c>GetTickCount64</c> and its unbiased interrupt
/// time (without sleep). Both clocks move together while the PC runs, so their difference changes only by the time of a
/// sleep or a hibernation. If the difference grew by more than 1 second since the last accepted line, the system slept,
/// and the line counts as the first sample after a resume. Both values are read at the receipt, so a burst of queued
/// lines does not look like a sleep. Thread-safe: each probe has its own reader thread.
/// </para>
/// </summary>
public sealed class ForegroundSampleCounter
{
    public static readonly TimeSpan FullGap = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan MaxSameGap = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SleepTolerance = TimeSpan.FromSeconds(1);

    private sealed class SessionState
    {
        public long LastT;
        public bool HasLast;
        public bool LastBlind;
        public HashSet<(AppCategory, string)> LastSignature = [];
        public long LastOffsetMs;
        public bool HasOffset;
        public bool SkipNext = true;
        public bool SpansUnexpectedEnd;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<int, SessionState> _sessions = new();

    /// <summary>A planned start of the probe (sign-in, unlock, connect, service start): the next sample adds zero.</summary>
    public void PlannedStart(int sessionId)
    {
        lock (_gate) State(sessionId).SkipNext = true;
    }

    /// <summary>A resume from sleep or hibernation: the next sample of each session adds zero.</summary>
    public void Resumed()
    {
        lock (_gate)
            foreach (var state in _sessions.Values) state.SkipNext = true;
    }

    /// <summary>An unexpected end of the probe: the last-sample time stays, and the next gap gives no gap item.</summary>
    public void UnexpectedEnd(int sessionId)
    {
        lock (_gate) State(sessionId).SpansUnexpectedEnd = true;
    }

    /// <summary>The end of the session: the state goes.</summary>
    public void SessionEnded(int sessionId)
    {
        lock (_gate) _sessions.Remove(sessionId);
    }

    /// <summary>
    /// Accepts one sample line (a window in front, <c>window: false</c>, or a blind sample) and gives its time.
    /// </summary>
    /// <param name="t">The <c>t</c> of the sample, in milliseconds.</param>
    /// <param name="duration">The time that the probe took for the sample, or null.</param>
    /// <param name="receiptTickMs">The <c>Environment.TickCount64</c> of the service at the read.</param>
    /// <param name="receiptUnbiased">The unbiased interrupt time of the service at the read.</param>
    /// <param name="blind">The sample is a blind sample.</param>
    /// <param name="counted">The counted processes of the sample.</param>
    public SampleCredit Accept(int sessionId, long t, TimeSpan? duration, long receiptTickMs, TimeSpan receiptUnbiased, bool blind,
        IReadOnlyCollection<CountedProcess> counted)
    {
        lock (_gate) return AcceptLocked(sessionId, t, duration, receiptTickMs, receiptUnbiased, blind, counted);
    }

    private SampleCredit AcceptLocked(int sessionId, long t, TimeSpan? duration, long receiptTickMs, TimeSpan receiptUnbiased, bool blind,
        IReadOnlyCollection<CountedProcess> counted)
    {
        var state = State(sessionId);
        var offset = receiptTickMs - (long)receiptUnbiased.TotalMilliseconds;
        var slept = state.HasOffset && offset - state.LastOffsetMs > SleepTolerance.TotalMilliseconds;
        state.LastOffsetMs = offset;
        state.HasOffset = true;

        var signature = Signature(counted);
        var first = !state.HasLast || state.SkipNext || slept || t <= state.LastT;
        var gap = first ? TimeSpan.Zero : TimeSpan.FromMilliseconds(t - state.LastT);
        var spansEnd = state.SpansUnexpectedEnd;
        var same = !blind && !state.LastBlind && signature.SetEquals(state.LastSignature);

        state.LastT = t;
        state.HasLast = true;
        state.LastBlind = blind;
        state.LastSignature = signature;
        state.SkipNext = false;
        state.SpansUnexpectedEnd = false;

        if (first) return new SampleCredit(TimeSpan.Zero, false, false, TimeSpan.Zero);

        TimeSpan time;
        var item = false;
        if (gap <= FullGap)
            time = gap;
        else if (same)
        {
            time = gap <= MaxSameGap ? gap : MaxSameGap;
            item = gap > MaxSameGap;
        }
        else
        {
            time = FullGap;
            item = true;
        }
        if (spansEnd) item = false;

        var longSample = duration is { } d && d > FullGap;
        if (longSample)
        {
            if (time > FullGap) time = FullGap;
            item = true;
        }

        return new SampleCredit(counted.Count == 0 ? TimeSpan.Zero : time, item, gap > FullGap || longSample, gap);
    }

    private SessionState State(int sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            _sessions[sessionId] = state = new SessionState();
        return state;
    }

    // The categories and App Rule exes of the counted processes, without the cursor entries.
    private static HashSet<(AppCategory, string)> Signature(IReadOnlyCollection<CountedProcess> counted) =>
        counted.Where(c => c.Role != ProbeRole.Cursor)
            .Select(c => (c.Category, WindowsPath.Canonicalize(c.ExePath)?.ToUpperInvariant() ?? c.ExePath.ToUpperInvariant()))
            .ToHashSet();
}
