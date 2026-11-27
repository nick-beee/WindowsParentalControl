namespace ParentalControl.Domain.Sessions;

/// <summary>Who or what ended a Session Probe.</summary>
public enum ProbeEndCause
{
    /// <summary>The service ended it at <c>WTS_SESSION_LOGOFF</c> or at the service stop: expected, no restart.</summary>
    Service,
    /// <summary>The watchdog of the service ended a silent probe. It gets the end wait like any other end that the
    /// service did not ask for.</summary>
    Watchdog,
    /// <summary>Any other end (a crash, an exit, an end by Windows).</summary>
    Other,
}

/// <summary>What the service does after the end of a probe.</summary>
/// <param name="Restart">Start a new probe after <paramref name="Delay"/>, if the session still exists then.</param>
/// <param name="Benign">A benign end (within the resume window of 15 s after a resume): no event and no item.</param>
public sealed record ProbeEndOutcome(bool Restart, TimeSpan Delay, bool Benign);

/// <summary>An unexpected end of a probe after its classification.</summary>
/// <param name="Alert">The 3rd unexpected end within one hour: one alert (at most one per user and hour).</param>
/// <param name="BudgetItem">The 3rd or a further unexpected end within 10 minutes of unlocked time: one budget item.</param>
/// <param name="Lock">The lock decision of that budget item.</param>
public sealed record UnexpectedProbeEnd(int UserId, int SessionId, bool Alert, bool BudgetItem, ProbeLockDecision Lock);

/// <summary>A probe outage: 5 minutes of unlocked, connected time without a sample.</summary>
/// <param name="Alert">One alert, at most one per user and hour.</param>
public sealed record ProbeOutage(int UserId, bool Alert);

/// <summary>
/// The faults and the hidden states of the Session Probe of each Restricted User (session-probe-and-agent-channel,
/// design D2, D6, D6a, D12 and S20):
/// <list type="bullet">
/// <item><b>Blind samples and budget items</b> (<see cref="Sample"/>): one item per sample at most. An item of a
/// session that is not active or is locked at its query is ignored (N23). The 6th item within 10 minutes of unlocked
/// time, and each blind sample after the first lock of the day (the strike), give a probe lock.</item>
/// <item><b>Shadow mode</b> (D12): while the probe locks are off, a probe lock is a "would lock", and the budget and the
/// strike advance as if it had locked. The switch to on clears the budget and the strike.</item>
/// <item><b>The end of a probe</b> that the service did not ask for (also a watchdog kill): the service restarts the
/// probe without a wait, after 1, 2, then 5 seconds for each further end of the session and logon in 10 minutes. The
/// end wait is 90 seconds of unbiased time: if <c>WTS_SESSION_LOGOFF</c>, a gone session or a service stop comes in it,
/// the end is a Session-End End (no event, no item, no alert); else, at the end of the end wait, it is unexpected
/// (<c>PROBE_RESTART</c>; from the 3rd in 10 minutes of unlocked time a budget item, with the N23 state queried at the
/// end itself; the 3rd in one hour alerts). An end within the resume window of 15 seconds after a resume is a Benign
/// End: a restart at once, no event, no item, no delay count.</item>
/// <item><b>Sign-out</b>: the state of a signed-out session is keyed by the session ID and the logon, so a new sign-in
/// in the same session ID clears it. The service starts no probe in a signed-out session.</item>
/// <item><b>Outage</b> (D6): 5 minutes of unlocked, connected time without a sample give one event, and one alert at
/// most once per user and hour.</item>
/// <item><b>Sleep</b> (S20): all times are the unbiased interrupt time, which has no sleep. A resume starts the outage
/// time again.</item>
/// </list>
/// The input is service facts only (WTS notifications and queries, service controls, probe samples), never data of
/// the Session Agent. Not thread-safe: the caller holds one lock.
/// </summary>
public sealed class ProbeOutageMonitor
{
    public static readonly TimeSpan OutageAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AlertInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan EndWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EndWait = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(15);
    public const int RestartAlertAt = 3;
    public const int RestartItemFrom = 3;

    private sealed class UserState
    {
        public readonly BlindSampleBudget Budget = new();
        public TimeSpan Unlocked;
        public TimeSpan LastObserved;
        public bool WasUnlocked;
        public bool Observed;
        public TimeSpan LastSampleUnlocked;
        public bool InOutage;
        public TimeSpan? LastOutageAlert;
        public readonly List<TimeSpan> EndsUnlocked = [];
        public readonly List<TimeSpan> EndsMonotonic = [];
        public TimeSpan? LastRestartAlert;
    }

    private sealed record PendingEnd(int UserId, int SessionId, long LogonKey, TimeSpan Due, bool ActiveAndUnlockedAtEnd);

    private readonly Dictionary<int, UserState> _users = new();
    private readonly List<PendingEnd> _pending = [];
    private readonly Dictionary<int, long> _signedOut = new();
    private readonly Dictionary<(int SessionId, long LogonKey), List<TimeSpan>> _restartsAt = new();
    private TimeSpan? _lastResume;

    public ProbeOutageMonitor(bool probeLocksEnforced) => ProbeLocksEnforced = probeLocksEnforced;

    public bool ProbeLocksEnforced { get; private set; }

    /// <summary>The switch of the setting <c>probe_locks_enforced</c>. The switch to on clears the budget and the
    /// strike of each user, so the switch itself never locks.</summary>
    public void SetProbeLocksEnforced(bool on)
    {
        if (on && !ProbeLocksEnforced)
            foreach (var user in _users.Values) user.Budget.Clear();
        ProbeLocksEnforced = on;
    }

    /// <summary>The state of the session of the user changed (lock, unlock, connect, disconnect), or the service
    /// queried it. The unlocked clock moves only while the session is active and unlocked.</summary>
    public void Observe(int userId, TimeSpan unbiasedNow, bool activeAndUnlocked)
    {
        var user = User(userId);
        if (user.Observed && user.WasUnlocked && unbiasedNow > user.LastObserved)
            user.Unlocked += unbiasedNow - user.LastObserved;
        if (!user.Observed || activeAndUnlocked != user.WasUnlocked)
        {
            // The 5 minutes of an outage start again at each unlock and connect (D6).
            if (activeAndUnlocked) user.LastSampleUnlocked = user.Unlocked;
        }
        user.Observed = true;
        user.LastObserved = unbiasedNow;
        user.WasUnlocked = activeAndUnlocked;
    }

    /// <summary>The unlocked clock of the user.</summary>
    public TimeSpan UnlockedTime(int userId, TimeSpan unbiasedNow)
    {
        var user = User(userId);
        return user.Observed && user.WasUnlocked ? user.Unlocked + (unbiasedNow - user.LastObserved) : user.Unlocked;
    }

    /// <summary>A resume from sleep or hibernation: the outage time starts again, and an end of a probe in the next 15
    /// seconds is benign.</summary>
    public void Resumed(TimeSpan unbiasedNow)
    {
        _lastResume = unbiasedNow;
        foreach (var (id, user) in _users)
        {
            Observe(id, unbiasedNow, user.WasUnlocked);
            user.LastSampleUnlocked = user.Unlocked;
        }
    }

    /// <summary>
    /// An accepted sample. <paramref name="blind"/> and <paramref name="budgetItem"/> together give at most one item.
    /// </summary>
    /// <param name="activeAndUnlocked">The connect state of the session (<c>WTSActive</c>) and the lock state of
    /// <c>SessionTracker</c>, queried at this sample (N23).</param>
    public ProbeLockDecision Sample(int userId, TimeSpan unbiasedNow, bool activeAndUnlocked, DateOnly today, bool blind, bool budgetItem)
    {
        Observe(userId, unbiasedNow, activeAndUnlocked);
        var user = User(userId);
        user.LastSampleUnlocked = user.Unlocked;
        user.InOutage = false;
        if (!activeAndUnlocked || (!blind && !budgetItem)) return ProbeLockDecision.None;
        return Decide(user, user.Budget.Add(user.Unlocked, today, blind), today);
    }

    /// <summary>A probe lock was applied through the Lock Deferral: the strike of the day is set and the window
    /// starts empty. Call it only for <see cref="ProbeLockAction.Lock"/>; a "would lock" applies it itself.</summary>
    public void LockApplied(int userId, DateOnly today) => User(userId).Budget.LockApplied(today);

    /// <summary>True if a probe lock that a Lock Deferral owed is still due at the end of the deferral.</summary>
    public bool IsLockDue(int userId, TimeSpan unbiasedNow, DateOnly today) =>
        User(userId).Budget.IsLockDue(UnlockedTime(userId, unbiasedNow), today);

    /// <summary>Checks the outage of each user. Returns each new outage once.</summary>
    public IReadOnlyList<ProbeOutage> CheckOutages(TimeSpan unbiasedNow)
    {
        var outages = new List<ProbeOutage>();
        foreach (var (id, user) in _users)
        {
            if (!user.WasUnlocked || user.InOutage) continue;
            if (UnlockedTime(id, unbiasedNow) - user.LastSampleUnlocked < OutageAfter) continue;
            user.InOutage = true;
            var alert = user.LastOutageAlert is not { } last || unbiasedNow - last >= AlertInterval;
            if (alert) user.LastOutageAlert = unbiasedNow;
            outages.Add(new ProbeOutage(id, alert));
        }
        return outages;
    }

    /// <summary>A probe of the session ended. Gives the restart; the end wait classifies the end in
    /// <see cref="Classify"/>.</summary>
    /// <param name="logonKey">The logon of the session (for example its logon time), so that a new sign-in in the same
    /// session ID is a new session.</param>
    /// <param name="activeAndUnlocked">The connect and lock state of the session, queried at the end itself (N23).</param>
    public ProbeEndOutcome ProbeEnded(int userId, int sessionId, long logonKey, TimeSpan unbiasedNow, ProbeEndCause cause,
        bool activeAndUnlocked)
    {
        if (cause == ProbeEndCause.Service || IsSignedOut(sessionId, logonKey))
            return new ProbeEndOutcome(false, TimeSpan.Zero, false);
        if (_lastResume is { } resume && unbiasedNow >= resume && unbiasedNow - resume <= ResumeWindow)
            return new ProbeEndOutcome(true, TimeSpan.Zero, true);

        if (!_restartsAt.TryGetValue((sessionId, logonKey), out var restarts))
            _restartsAt[(sessionId, logonKey)] = restarts = [];
        restarts.RemoveAll(t => unbiasedNow - t >= EndWindow);
        var delay = restarts.Count switch
        {
            0 => TimeSpan.FromSeconds(1),
            1 => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(5),
        };
        restarts.Add(unbiasedNow);
        _pending.Add(new PendingEnd(userId, sessionId, logonKey, unbiasedNow + EndWait, activeAndUnlocked));
        return new ProbeEndOutcome(true, delay, false);
    }

    /// <summary><c>WTS_SESSION_LOGOFF</c> of the session: each pending end of it is a session-end end, and no probe
    /// starts in it until a new sign-in. The unlocked clock of the user stops (S5), so the time of the sign-out never
    /// counts as unlocked time and gives no <c>PROBE_OUTAGE</c>.</summary>
    /// <param name="userId">The Restricted User of the session, or null if the service does not know one.</param>
    public void SignedOut(int? userId, int sessionId, long logonKey, TimeSpan unbiasedNow)
    {
        if (userId is { } id) Observe(id, unbiasedNow, false);
        _signedOut[sessionId] = logonKey;
        _pending.RemoveAll(p => p.SessionId == sessionId);
        _restartsAt.Remove((sessionId, logonKey));
    }

    /// <summary><c>WTS_SESSION_LOGON</c> in the session: a new logon clears the signed-out state.</summary>
    public void SignedIn(int sessionId, long logonKey)
    {
        if (_signedOut.TryGetValue(sessionId, out var key) && key != logonKey) _signedOut.Remove(sessionId);
    }

    /// <summary>The service stops: each pending end is a session-end end.</summary>
    public void ServiceStopping() => _pending.Clear();

    /// <summary>The end of the session (it is gone): its pending ends are session-end ends, and the unlocked clock of
    /// the user stops (S5).</summary>
    /// <param name="userId">The Restricted User of the session, or null if the service does not know one.</param>
    public void SessionGone(int? userId, int sessionId, TimeSpan unbiasedNow)
    {
        if (userId is { } id) Observe(id, unbiasedNow, false);
        _pending.RemoveAll(p => p.SessionId == sessionId);
    }

    /// <summary>True if the service may start a probe in the session: it did not sign out in this logon. The caller
    /// also checks that the session exists.</summary>
    public bool MayStart(int sessionId, long logonKey) => !IsSignedOut(sessionId, logonKey);

    /// <summary>
    /// Classifies each end whose end wait is over. <paramref name="isGone"/> is the live test of the service
    /// (<see cref="SessionPresence.IsGone"/>), checked again at each call: a session that is gone within the end wait
    /// makes a Session-End End. The test counts only while the end wait runs: at or after <c>Due</c> the end is
    /// unexpected also if the session is gone now, because the gone state then can come from after the end wait.
    /// </summary>
    public IReadOnlyList<UnexpectedProbeEnd> Classify(TimeSpan unbiasedNow, Func<int, bool> isGone, DateOnly today)
    {
        var result = new List<UnexpectedProbeEnd>();
        foreach (var pending in _pending.ToList())
        {
            if (unbiasedNow < pending.Due)
            {
                if (isGone(pending.SessionId)) _pending.Remove(pending);
                continue;
            }
            _pending.Remove(pending);
            result.Add(Unexpected(pending.UserId, pending.SessionId, unbiasedNow, pending.ActiveAndUnlockedAtEnd, today));
        }
        return result;
    }

    /// <summary>The number of ends that wait for their classification.</summary>
    public int PendingEnds => _pending.Count;

    private UnexpectedProbeEnd Unexpected(int userId, int sessionId, TimeSpan unbiasedNow, bool activeAndUnlocked, DateOnly today)
    {
        var user = User(userId);
        Observe(userId, unbiasedNow, user.WasUnlocked);
        user.EndsMonotonic.RemoveAll(t => unbiasedNow - t >= AlertInterval);
        user.EndsMonotonic.Add(unbiasedNow);
        var alert = user.EndsMonotonic.Count >= RestartAlertAt
                    && (user.LastRestartAlert is not { } last || unbiasedNow - last >= AlertInterval);
        if (alert) user.LastRestartAlert = unbiasedNow;

        user.EndsUnlocked.RemoveAll(t => user.Unlocked - t >= EndWindow);
        user.EndsUnlocked.Add(user.Unlocked);
        var item = user.EndsUnlocked.Count >= RestartItemFrom && activeAndUnlocked;
        var decision = item ? Decide(user, user.Budget.Add(user.Unlocked, today, blind: false), today) : ProbeLockDecision.None;
        return new UnexpectedProbeEnd(userId, sessionId, alert, item, decision);
    }

    private ProbeLockDecision Decide(UserState user, ProbeLockReason reason, DateOnly today)
    {
        if (reason == ProbeLockReason.None) return ProbeLockDecision.None;
        if (ProbeLocksEnforced) return new ProbeLockDecision(ProbeLockAction.Lock, reason);
        user.Budget.LockApplied(today);
        return new ProbeLockDecision(ProbeLockAction.WouldLock, reason);
    }

    private bool IsSignedOut(int sessionId, long logonKey) => _signedOut.TryGetValue(sessionId, out var key) && key == logonKey;

    private UserState User(int userId)
    {
        if (!_users.TryGetValue(userId, out var user))
            _users[userId] = user = new UserState();
        return user;
    }
}

/// <summary>The gone test of a session (session-probe-and-agent-channel, design D2): never sticky, checked at each tick.</summary>
public static class SessionPresence
{
    /// <summary>A session is gone only if <c>WTSQuerySessionInformationW</c> fails and <c>WTSEnumerateSessionsW</c>
    /// does not list it.</summary>
    public static bool IsGone(bool queryFailed, bool listedByEnumeration) => queryFailed && !listedByEnumeration;
}
