using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class ProbeOutageMonitorTests
{
    private const int User = 7;
    private const int Session = 2;
    private const long Logon = 1000;
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static ProbeOutageMonitor New(bool enforced = true)
    {
        var monitor = new ProbeOutageMonitor(enforced);
        monitor.Observe(User, S(0), true);
        return monitor;
    }

    private static ProbeLockDecision Blind(ProbeOutageMonitor m, double at, bool active = true) =>
        m.Sample(User, S(at), active, Today, blind: true, budgetItem: false);

    private static ProbeLockDecision Item(ProbeOutageMonitor m, double at) =>
        m.Sample(User, S(at), true, Today, blind: false, budgetItem: true);

    private static ProbeLockDecision Readable(ProbeOutageMonitor m, double at) =>
        m.Sample(User, S(at), true, Today, blind: false, budgetItem: false);

    private static readonly Func<int, bool> Active = _ => true;
    private static readonly Func<int, bool> NotGone = _ => false;

    // ---------- the budget and the strike ----------

    [Fact]
    public void Five_blind_and_one_readable_sample_every_30_s_lock_at_the_6th_blind_sample()
    {
        var m = New();
        var t = 0.0;
        var locks = new List<double>();
        for (var round = 0; round < 3 && locks.Count == 0; round++)
        {
            for (var i = 0; i < 5; i++)
            {
                t += 5;
                if (Blind(m, t).Action == ProbeLockAction.Lock) locks.Add(t);
            }
            t += 5;
            Assert.Equal(ProbeLockAction.None, Readable(m, t).Action);
        }
        Assert.Equal(35.0, locks[0]);
    }

    [Fact]
    public void Five_blind_samples_spread_over_11_minutes_do_not_lock_and_neither_does_a_6th_later()
    {
        var m = New();
        for (var i = 0; i < 5; i++) Assert.Equal(ProbeLockAction.None, Blind(m, i * 132).Action);
        Assert.Equal(ProbeLockAction.None, Blind(m, 5 * 132).Action);
    }

    [Fact]
    public void Six_ended_entries_in_10_minutes_lock_and_set_no_strike_by_themselves()
    {
        var m = New();
        for (var i = 1; i <= 5; i++) Assert.Equal(ProbeLockAction.None, Item(m, i * 60).Action);
        var decision = Item(m, 360);
        Assert.Equal(new ProbeLockDecision(ProbeLockAction.Lock, ProbeLockReason.Budget), decision);
    }

    [Fact]
    public void One_sample_with_three_ended_entries_is_one_item()
    {
        // CountedProcesses gives one EndedItem flag per sample; the monitor adds one item for it.
        var m = New();
        for (var i = 1; i <= 5; i++) m.Sample(User, S(i * 10), true, Today, blind: false, budgetItem: true);
        Assert.False(m.IsLockDue(User, S(51), Today));
    }

    [Fact]
    public void Blind_sample_and_ended_entry_in_one_sample_are_one_item()
    {
        var m = New();
        for (var i = 1; i <= 5; i++) m.Sample(User, S(i * 10), true, Today, blind: true, budgetItem: true);
        Assert.False(m.IsLockDue(User, S(51), Today));
    }

    [Fact]
    public void After_the_strike_a_blind_sample_locks_and_an_ended_entry_does_not()
    {
        var m = New();
        for (var i = 1; i <= 6; i++) Blind(m, i * 5);
        m.LockApplied(User, Today);
        Assert.Equal(ProbeLockAction.None, Item(m, 100).Action);
        Assert.Equal(new ProbeLockDecision(ProbeLockAction.Lock, ProbeLockReason.Strike), Blind(m, 105));
    }

    [Fact]
    public void Six_gap_items_in_10_minutes_lock_and_a_gap_item_is_never_the_strike()
    {
        var m = New();
        for (var i = 1; i <= 6; i++) Blind(m, i * 5);
        m.LockApplied(User, Today);
        for (var i = 1; i <= 5; i++) Assert.Equal(ProbeLockAction.None, Item(m, 100 + i * 30).Action);
        Assert.Equal(new ProbeLockDecision(ProbeLockAction.Lock, ProbeLockReason.Budget), Item(m, 300));
    }

    [Fact]
    public void New_day_clears_the_strike()
    {
        var m = New();
        for (var i = 1; i <= 6; i++) Blind(m, i * 5);
        m.LockApplied(User, Today);
        Assert.Equal(ProbeLockAction.None, m.Sample(User, S(100), true, Today.AddDays(1), blind: true, budgetItem: false).Action);
    }

    [Fact]
    public void Blind_sample_of_a_disconnected_or_locked_session_is_ignored()
    {
        var m = New();
        for (var i = 1; i <= 10; i++) Assert.Equal(ProbeLockAction.None, Blind(m, i * 5, active: false).Action);
        Assert.False(m.IsLockDue(User, S(60), Today));
    }

    [Fact]
    public void Locked_time_is_not_in_the_window()
    {
        var m = New();
        for (var i = 1; i <= 5; i++) Blind(m, i * 5);
        m.Observe(User, S(30), false);
        m.Observe(User, S(3600), true);
        Assert.Equal(ProbeLockAction.Lock, Blind(m, 3605).Action);
    }

    [Fact]
    public void Sleep_of_10_minutes_between_the_5th_and_the_6th_blind_sample_locks_at_the_6th()
    {
        // The unbiased interrupt time does not move during the sleep.
        var m = New();
        for (var i = 1; i <= 5; i++) Blind(m, i * 5);
        m.Resumed(S(26));
        Assert.Equal(ProbeLockAction.Lock, Blind(m, 30).Action);
    }

    [Fact]
    public void Owed_lock_is_due_while_the_window_holds_6_items_or_the_strike_is_set()
    {
        var m = New();
        for (var i = 1; i <= 6; i++) Blind(m, i * 5);
        Assert.True(m.IsLockDue(User, S(40), Today));
        Assert.False(m.IsLockDue(User, S(700), Today));
        m.LockApplied(User, Today);
        Assert.True(m.IsLockDue(User, S(800), Today));
    }

    // ---------- shadow mode ----------

    [Fact]
    public void Sixth_blind_sample_with_the_probe_locks_off_is_a_would_lock_and_sets_the_strike()
    {
        var m = New(enforced: false);
        for (var i = 1; i <= 5; i++) Blind(m, i * 5);
        Assert.Equal(new ProbeLockDecision(ProbeLockAction.WouldLock, ProbeLockReason.Budget), Blind(m, 30));
        Assert.Equal(new ProbeLockDecision(ProbeLockAction.WouldLock, ProbeLockReason.Strike), Blind(m, 35));
    }

    [Fact]
    public void Switch_to_on_after_a_would_lock_does_not_lock_and_clears_the_strike()
    {
        var m = New(enforced: false);
        for (var i = 1; i <= 6; i++) Blind(m, i * 5);
        m.SetProbeLocksEnforced(true);
        Assert.False(m.IsLockDue(User, S(40), Today));
        Assert.Equal(ProbeLockAction.None, Blind(m, 40).Action);
        for (var i = 1; i <= 4; i++) Assert.Equal(ProbeLockAction.None, Blind(m, 40 + i * 5).Action);
        Assert.Equal(ProbeLockAction.Lock, Blind(m, 65).Action);
    }

    // ---------- outage ----------

    [Fact]
    public void Five_minutes_without_a_sample_give_one_outage_with_one_alert_per_hour()
    {
        var m = New();
        Readable(m, 10);
        Assert.Empty(m.CheckOutages(S(300)));
        Assert.Equal([new ProbeOutage(User, true)], m.CheckOutages(S(310)));
        Assert.Empty(m.CheckOutages(S(400)));
        Readable(m, 500);
        Assert.Equal([new ProbeOutage(User, false)], m.CheckOutages(S(800)));
        Readable(m, 4000);
        Assert.Equal([new ProbeOutage(User, true)], m.CheckOutages(S(4300)));
    }

    [Fact]
    public void Locked_time_does_not_count_for_an_outage()
    {
        var m = New();
        Readable(m, 10);
        m.Observe(User, S(20), false);
        Assert.Empty(m.CheckOutages(S(3600)));
        m.Observe(User, S(3600), true);
        Assert.Empty(m.CheckOutages(S(3800)));
        Assert.Single(m.CheckOutages(S(3900)));
    }

    // ---------- the end of a probe ----------

    private static (ProbeEndOutcome Outcome, UnexpectedProbeEnd? Unexpected) End(ProbeOutageMonitor m, double at,
        ProbeEndCause cause = ProbeEndCause.Other, long logon = Logon) =>
        m.ProbeEnded(User, Session, logon, S(at), cause, Active, Today);

    [Fact]
    public void Restart_delays_are_1_2_and_5_s_within_10_minutes()
    {
        var m = New();
        Assert.Equal([1.0, 2, 5, 5], new[] { 10.0, 70, 130, 190 }.Select(t => End(m, t).Outcome.Delay.TotalSeconds));
        Assert.Equal(1, End(m, 900).Outcome.Delay.TotalSeconds);
    }

    [Fact]
    public void End_in_a_session_that_stays_is_unexpected_after_90_s()
    {
        var m = New();
        var (outcome, unexpected) = End(m, 10);
        Assert.True(outcome.Restart);
        Assert.Null(unexpected);
        Assert.Empty(m.Classify(S(99), NotGone, Active, Today));
        var classified = Assert.Single(m.Classify(S(100), NotGone, Active, Today));
        Assert.Equal((User, Session, false, false), (classified.UserId, classified.SessionId, classified.Alert, classified.BudgetItem));
    }

    [Fact]
    public void Two_sign_outs_within_one_hour_give_no_event_no_item_and_no_alert()
    {
        var m = New();
        foreach (var (start, logon) in new[] { (100.0, 1L), (1900.0, 2L) })
        {
            m.SignedIn(Session, logon);
            Assert.True(m.MayStart(Session, logon));
            Assert.True(End(m, start, logon: logon).Outcome.Restart);          // Windows ends the probe, 6 s before the notification
            Assert.True(End(m, start + 5.7, logon: logon).Outcome.Restart);    // the restarted probe, 0.3 s before it
            m.SignedOut(Session, logon);
            Assert.False(m.MayStart(Session, logon));
            Assert.False(End(m, start + 7, logon: logon).Outcome.Restart);
            Assert.Empty(m.Classify(S(start + 200), NotGone, Active, Today));
        }
        Assert.Equal(0, m.PendingEnds);
    }

    [Fact]
    public void New_sign_in_in_the_same_session_ID_clears_the_sign_out()
    {
        var m = New();
        m.SignedOut(Session, 1);
        Assert.False(m.MayStart(Session, 1));
        m.SignedIn(Session, 2);
        Assert.True(m.MayStart(Session, 2));
    }

    [Fact]
    public void End_and_a_gone_session_7_s_later_is_a_session_end_end()
    {
        var m = New();
        End(m, 10);
        Assert.Empty(m.Classify(S(17), id => id == Session, Active, Today));
        Assert.Empty(m.Classify(S(200), NotGone, Active, Today));
    }

    [Fact]
    public void Gone_is_never_sticky()
    {
        var m = New();
        End(m, 10);
        m.SessionGone(Session);
        Assert.True(m.MayStart(Session, Logon));
    }

    [Fact]
    public void End_and_a_service_stop_3_s_later_is_a_session_end_end()
    {
        var m = New();
        End(m, 10);
        m.ServiceStopping();
        Assert.Empty(m.Classify(S(200), NotGone, Active, Today));
    }

    [Fact]
    public void Service_end_is_expected_and_gives_no_restart()
    {
        var m = New();
        Assert.False(End(m, 10, ProbeEndCause.Service).Outcome.Restart);
        Assert.Equal(0, m.PendingEnds);
    }

    [Fact]
    public void Watchdog_kill_is_unexpected_at_once()
    {
        var m = New();
        var (outcome, unexpected) = End(m, 10, ProbeEndCause.Watchdog);
        Assert.True(outcome.Restart);
        Assert.NotNull(unexpected);
        Assert.Equal(0, m.PendingEnds);
    }

    [Fact]
    public void Two_unexpected_ends_give_no_item_and_the_3rd_alerts_and_is_an_item()
    {
        var m = New();
        var ends = new List<UnexpectedProbeEnd>();
        foreach (var t in new[] { 10.0, 86, 162 })
        {
            End(m, t);
            ends.AddRange(m.Classify(S(t + 90), NotGone, Active, Today));
        }
        Assert.Equal([false, false, true], ends.Select(e => e.BudgetItem));
        Assert.Equal([false, false, true], ends.Select(e => e.Alert));
    }

    [Fact]
    public void Eight_unexpected_ends_in_10_minutes_lock()
    {
        var m = New();
        var decisions = new List<ProbeLockDecision>();
        for (var i = 0; i < 8; i++)
        {
            var t = 10 + i * 70;
            End(m, t);
            decisions.AddRange(m.Classify(S(t + 90), NotGone, Active, Today).Select(e => e.Lock));
        }
        Assert.Equal(8, decisions.Count);
        Assert.Equal(ProbeLockAction.Lock, decisions[7].Action);
        Assert.All(decisions.Take(7), d => Assert.Equal(ProbeLockAction.None, d.Action));
    }

    [Fact]
    public void Alert_for_unexpected_ends_at_most_once_per_hour()
    {
        var m = New();
        var alerts = 0;
        for (var i = 0; i < 20; i++)
        {
            var t = 10 + i * 120;
            End(m, t);
            alerts += m.Classify(S(t + 90), NotGone, Active, Today).Count(e => e.Alert);
        }
        Assert.Equal(1, alerts);
    }

    [Fact]
    public void Item_of_an_unexpected_end_in_a_locked_session_is_ignored()
    {
        var m = New();
        var items = 0;
        foreach (var t in new[] { 10.0, 86, 162, 238 })
        {
            End(m, t);
            items += m.Classify(S(t + 90), NotGone, _ => false, Today).Count(e => e.BudgetItem);
        }
        Assert.Equal(0, items);
    }

    [Fact]
    public void End_within_15_s_after_a_resume_is_benign()
    {
        var m = New();
        m.Resumed(S(100));
        var (outcome, unexpected) = End(m, 110);
        Assert.True(outcome.Restart);
        Assert.True(outcome.Benign);
        Assert.Equal(TimeSpan.Zero, outcome.Delay);
        Assert.Null(unexpected);
        Assert.Equal(0, m.PendingEnds);
    }

    [Fact]
    public void Three_sleeps_of_5_minutes_within_10_minutes_with_a_probe_end_at_each_resume_give_nothing()
    {
        // The unbiased time does not move in the sleep; each resume is 5 s of awake time after the previous one.
        var m = New();
        Readable(m, 10);
        var t = 10.0;
        for (var i = 0; i < 3; i++)
        {
            t += 5;
            m.Resumed(S(t));
            var (outcome, unexpected) = End(m, t + 1);
            Assert.True(outcome.Benign);
            Assert.Null(unexpected);
            Readable(m, t + 3);
        }
        Assert.Empty(m.CheckOutages(S(t + 60)));
        Assert.Empty(m.Classify(S(t + 500), NotGone, Active, Today));
        Assert.False(m.IsLockDue(User, S(t + 60), Today));
    }
}
