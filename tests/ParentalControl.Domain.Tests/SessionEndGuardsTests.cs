using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

/// <summary>Spec: category-limits "Session End Guard", "Limit the Session End Guard" and "Report a repeated Session End Guard" (issue #47).</summary>
public class SessionEndGuardsTests
{
    private const int Session = 1;
    private const int OtherSession = 4;
    private const int Kid = 2;
    private const int OtherKid = 3;
    private static readonly DateTime T0 = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start1 = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start2 = new(2026, 11, 27, 9, 5, 0, DateTimeKind.Utc);
    private static readonly (int, DateTime) Game = (10, Start1);
    private static readonly (int, DateTime) Pad = (11, Start2);
    private static readonly Func<int, bool> SessionExists = _ => true;
    private static readonly Func<int, bool> SessionGone = _ => false;

    private static StartedGuard Started(GuardStart start)
    {
        Assert.Equal(GuardStartOutcome.Started, start.Outcome);
        return start.Guard!;
    }

    [Fact]
    public void Guard_covers_the_processes_that_the_service_suspended()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);

        var guard = Started(guards.Start(Session, Kid, [Game, Pad], T0));

        Assert.Equal([Game, Pad], guard.Covered);
        Assert.Equal(T0.AddSeconds(60), guard.EndUtc);
        Assert.False(guard.Repeated);
        Assert.Equal(new HashSet<(int, DateTime)> { Game, Pad }, guards.Covered(T0.AddSeconds(30)));
        Assert.Equal(T0.AddSeconds(60), guards.NextEndUtc);
    }

    [Fact]
    public void Guard_does_not_cover_a_process_that_the_service_resumed_itself()
    {
        // After a granted hold the service resumed the game; a fake request must not keep it running.
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkResumed(10, Start1);

        Assert.Equal(GuardStartOutcome.NothingToCover, guards.Start(Session, Kid, [Game], T0).Outcome);
        Assert.Empty(guards.Covered(T0));
    }

    [Fact]
    public void Guard_does_not_cover_a_process_that_the_service_never_suspended()
    {
        var guards = new SessionEndGuards();
        Assert.Equal(GuardStartOutcome.NothingToCover, guards.Start(Session, Kid, [Game], T0).Outcome);
    }

    [Fact]
    public void Request_without_an_eligible_process_starts_no_guard()
    {
        // Review finding 1: an empty guard must not block a later real guard for 60 s.
        var guards = new SessionEndGuards();

        var empty = guards.Start(Session, Kid, [Game], T0);

        Assert.Equal(GuardStartOutcome.NothingToCover, empty.Outcome);
        Assert.Null(empty.Guard);
        Assert.Null(guards.NextEndUtc);
        Assert.Empty(guards.Expire(T0.AddSeconds(60), SessionExists));
    }

    [Fact]
    public void Empty_request_does_not_block_a_real_guard_soon_after()
    {
        // A refused request with nothing to cover, then the first suspension of the game, then a real sign-out
        // within 60 s: the real sign-out gets a guard (else the next tick freezes the game: #47).
        var guards = new SessionEndGuards();
        Assert.Equal(GuardStartOutcome.NothingToCover, guards.Start(Session, Kid, [Game], T0).Outcome);

        guards.MarkSuspended(10, Start1);
        var guard = Started(guards.Start(Session, Kid, [Game], T0.AddSeconds(30)));

        Assert.Equal([Game], guard.Covered);
        Assert.False(guard.Repeated);
        Assert.Contains(Game, guards.Covered(T0.AddSeconds(60)));
    }

    [Fact]
    public void Guard_covers_only_the_processes_that_the_caller_lists_for_its_session()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);

        Assert.Equal([Game], Started(guards.Start(Session, Kid, [Game], T0)).Covered);
        Assert.True(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Process_is_covered_at_most_once_for_each_suspension()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        guards.Expire(T0.AddSeconds(60), SessionExists);

        // The next request comes in the Pending Suspension after the guard: the game is not covered again.
        Assert.Equal(GuardStartOutcome.NothingToCover, guards.Start(Session, Kid, [Game], T0.AddSeconds(65)).Outcome);
        Assert.Empty(guards.Covered(T0.AddSeconds(66)));

        // The service suspends the game again: a later guard can cover it again.
        guards.MarkSuspended(10, Start1);
        Assert.Equal([Game], Started(guards.Start(Session, Kid, [Game], T0.AddSeconds(130))).Covered);
    }

    [Fact]
    public void Request_during_a_running_guard_does_not_extend_it()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        guards.MarkSuspended(11, Start2);

        var again = guards.Start(Session, Kid, [Game, Pad], T0.AddSeconds(30));

        Assert.Equal(GuardStartOutcome.AlreadyRunning, again.Outcome);
        Assert.Null(again.Guard);
        Assert.Equal(new HashSet<(int, DateTime)> { Game }, guards.Covered(T0.AddSeconds(30)));
        Assert.Empty(guards.Covered(T0.AddSeconds(60)));
        Assert.True(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Guard_of_another_session_runs_on_its_own()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);
        guards.Start(Session, Kid, [Game], T0);

        Assert.Equal([Pad], Started(guards.Start(OtherSession, OtherKid, [Pad], T0.AddSeconds(10))).Covered);
    }

    [Fact]
    public void Guard_ends_by_its_time()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);

        Assert.Empty(guards.Expire(T0.AddSeconds(59.9), SessionExists));
        Assert.NotEmpty(guards.Covered(T0.AddSeconds(59.9)));
        var expired = Assert.Single(guards.Expire(T0.AddSeconds(60), SessionExists));
        Assert.False(expired.Confirmed);
        Assert.Null(guards.NextEndUtc);
        Assert.Empty(guards.Covered(T0.AddSeconds(60)));
    }

    [Fact]
    public void Guard_ends_with_the_session()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);

        Assert.False(guards.SessionEnded(OtherSession));
        Assert.True(guards.SessionEnded(Session));
        Assert.Empty(guards.Covered(T0.AddSeconds(5)));
        Assert.Null(guards.NextEndUtc);
    }

    [Fact]
    public void Retain_forgets_a_process_without_a_record()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);

        guards.Retain(new HashSet<(int, DateTime)> { Pad });

        Assert.False(guards.IsEligible(10, Start1));
        Assert.True(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Observe_makes_a_suspended_record_eligible_outside_a_guard()
    {
        var guards = new SessionEndGuards();

        guards.Observe([Game, Pad], new HashSet<(int, DateTime)> { Game }, T0);

        Assert.True(guards.IsEligible(10, Start1));
        Assert.False(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Observe_ignores_a_suspended_process_without_a_record()
    {
        var guards = new SessionEndGuards();

        guards.Observe([Pad], new HashSet<(int, DateTime)> { Game, Pad }, T0);

        Assert.False(guards.IsEligible(10, Start1));
        Assert.True(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Observe_does_not_make_a_covered_process_eligible_during_its_guard()
    {
        // The agent could not resume a covered process. It stays suspended, but only after the guard can it be eligible again.
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        var suspended = new HashSet<(int, DateTime)> { Game };

        guards.Observe([Game], suspended, T0.AddSeconds(30));
        Assert.False(guards.IsEligible(10, Start1));

        guards.Observe([Game], suspended, T0.AddSeconds(60));
        Assert.True(guards.IsEligible(10, Start1));
    }

    [Fact]
    public void Guard_has_the_length_of_a_hold()
    {
        Assert.Equal(SessionEndHolds.HoldDuration, SessionEndGuards.Duration);
        Assert.Equal(SessionEndHolds.UnconfirmedWindow, SessionEndGuards.RepeatWindow);
    }

    // --- Report a repeated Session End Guard (review finding 3) ---

    [Fact]
    public void Guard_after_an_unconfirmed_guard_is_repeated()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        Assert.False(Started(guards.Start(Session, Kid, [Game], T0)).Repeated);
        guards.Expire(T0.AddSeconds(60), SessionExists);         // the child cancelled the sign-out

        guards.MarkSuspended(10, Start1);                         // the service suspends the game again
        var second = Started(guards.Start(Session, Kid, [Game], T0.AddMinutes(3)));

        Assert.True(second.Repeated);
        Assert.Equal([Game], second.Covered);                     // a repeated guard still covers the game (no #47)
        Assert.Contains(Game, guards.Covered(T0.AddMinutes(3).AddSeconds(59)));
    }

    [Fact]
    public void Guard_after_a_completed_sign_out_is_not_repeated()
    {
        // A guard that ends with its session (the sign-out completed) never counts.
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        Assert.True(guards.SessionEnded(Session));

        guards.MarkSuspended(11, Start2);
        Assert.False(Started(guards.Start(OtherSession, Kid, [Pad], T0.AddMinutes(1))).Repeated);
    }

    [Fact]
    public void Guard_whose_session_is_gone_at_its_end_is_confirmed_and_does_not_count()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        Assert.True(Assert.Single(guards.Expire(T0.AddSeconds(61), SessionGone)).Confirmed);

        guards.MarkSuspended(11, Start2);
        Assert.False(Started(guards.Start(OtherSession, Kid, [Pad], T0.AddMinutes(1))).Repeated);
    }

    [Fact]
    public void Request_with_nothing_to_cover_does_not_count()
    {
        var guards = new SessionEndGuards();
        guards.Start(Session, Kid, [Game], T0);                   // nothing eligible: no guard
        guards.Expire(T0.AddSeconds(61), SessionExists);

        guards.MarkSuspended(10, Start1);
        Assert.False(Started(guards.Start(Session, Kid, [Game], T0.AddMinutes(2))).Repeated);
    }

    [Fact]
    public void Repeat_window_is_15_minutes_after_the_unconfirmed_guard()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        guards.Expire(T0.AddSeconds(60), SessionExists);

        guards.MarkSuspended(10, Start1);
        Assert.False(Started(guards.Start(Session, Kid, [Game], T0.AddMinutes(15).AddSeconds(1))).Repeated);
    }

    [Fact]
    public void Repeat_counts_per_user()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, Kid, [Game], T0);
        guards.Expire(T0.AddSeconds(60), SessionExists);

        guards.MarkSuspended(11, Start2);
        Assert.False(Started(guards.Start(OtherSession, OtherKid, [Pad], T0.AddMinutes(2))).Repeated);
    }

    [Fact]
    public void Guard_while_another_guard_of_the_user_is_open_is_repeated()
    {
        // A guard that did not end yet can still end without the end of its session, so it counts.
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);
        guards.Start(Session, Kid, [Game], T0);

        Assert.True(Started(guards.Start(OtherSession, Kid, [Pad], T0.AddSeconds(10))).Repeated);
    }
}

/// <summary>
/// The hold, the guard, the planner and the Pending Suspension together, in the order of <c>CategoryEnforcer</c>.
/// The model keeps only the state of the one game and of the session. Every decision comes from the Domain classes,
/// also the observe step (<see cref="SessionEndGuards.Observe"/>).
/// Spec: "Session End Guard", "Limit the Session End Guard" and "Report a repeated Session End Guard".
/// </summary>
public class SessionEndGuardChainTests
{
    private const int Kid = 2;
    private const int Session = 1;
    private const string Game = @"C:\Steam\steamapps\common\Portal\portal.exe";
    private static readonly DateTime T0 = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GameStart = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 27);

    /// <summary>A minimal model of the enforcer for one recorded, exhausted game.</summary>
    private sealed class Model
    {
        public readonly SessionEndHolds Holds = new();
        public readonly SessionEndGuards Guards = new();
        public readonly PendingSuspensions Pending = new();
        public bool GameSuspended = true;
        public GuardStart? LastGuard;
        public readonly SuspensionRecord Record = new(10, GameStart, Session, Kid, AppCategory.Games, Game);
        private (int, DateTime) Key => (Record.Pid, Record.StartTimeUtc);

        public Model() => Guards.MarkSuspended(Record.Pid, Record.StartTimeUtc);

        /// <summary>The session-end request. The agent resumed the game before (<paramref name="agentResumed"/>).</summary>
        public HoldRequestResult Request(DateTime now, bool agentResumed)
        {
            if (agentResumed)
                GameSuspended = false;
            var result = Holds.Request(Session, Kid, now, trusted: false);
            switch (result)
            {
                case HoldRequestResult.Refused:
                    LastGuard = Guards.Start(Session, Kid, [Key], now);
                    break;
                default:
                    GameSuspended = false;
                    Guards.MarkResumed(Record.Pid, Record.StartTimeUtc);
                    break;
            }
            return result;
        }

        public void Tick(DateTime now)
        {
            Holds.Expire(now, Today, _ => true);
            Guards.Expire(now, _ => true);
            Guards.Observe([Key], GameSuspended ? new HashSet<(int, DateTime)> { Key } : new HashSet<(int, DateTime)>(), now);
            var guarded = Guards.Covered(now);

            var plan = SuspensionPlanner.Plan(new PlannerInput(
                [new SessionProcess(Record.Pid, Record.StartTimeUtc, Game, Session, GameSuspended)],
                new Dictionary<int, int> { [Session] = Kid },
                (_, _) => new ExeVerdict(AppCategory.Games, IsExhausted: true),
                (_, _) => true,
                [Record], null, @"C:\Windows",
                Holds.HeldSessions(now),
                GuardedProcesses: guarded));
            foreach (var _ in Pending.Step(plan.ToSuspend, now).Ready)
            {
                GameSuspended = true;
                Guards.MarkSuspended(Record.Pid, Record.StartTimeUtc);
            }
        }

        /// <summary>Ticks as the follow-up timer does: at each end of a hold, a guard or a Pending Suspension, plus the 60 s tick.</summary>
        public void TickUntil(DateTime from, DateTime to)
        {
            for (var t = from; t <= to; t = t.AddSeconds(0.5))
                Tick(t);
        }
    }

    /// <summary>The first sign-out in the window: a granted hold that did not end with the session, and the re-suspension.</summary>
    private static Model AfterUnconfirmedHold()
    {
        var m = new Model();
        Assert.Equal(HoldRequestResult.Granted, m.Request(T0, agentResumed: true));
        m.Tick(T0.AddSeconds(60.5));            // the hold ends without the end of the session
        m.Tick(T0.AddSeconds(71));              // Pending Suspension ready
        Assert.True(m.GameSuspended);
        return m;
    }

    [Fact]
    public void Second_sign_out_within_15_minutes_keeps_the_resumed_game_running_during_the_guard()
    {
        var m = AfterUnconfirmedHold();

        var second = T0.AddMinutes(7);
        Assert.Equal(HoldRequestResult.Refused, m.Request(second, agentResumed: true));
        Assert.False(m.LastGuard!.Guard!.Repeated);
        for (var s = 1; s < 60; s += 5)
        {
            m.Tick(second.AddSeconds(s));
            Assert.False(m.GameSuspended);
        }

        // The child cancelled: the guard ends and the game is suspended again within 20 seconds.
        m.Tick(second.AddSeconds(60.5));
        m.Tick(second.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Third_sign_out_after_the_re_suspension_is_covered_again_and_repeated()
    {
        var m = AfterUnconfirmedHold();
        var second = T0.AddMinutes(3);
        m.Request(second, agentResumed: true);
        Assert.False(m.LastGuard!.Guard!.Repeated);
        m.TickUntil(second.AddSeconds(1), second.AddSeconds(75));
        Assert.True(m.GameSuspended);           // re-suspended after the guard: eligible again

        // Third real sign-out: the game is covered again (no freeze during the sign-out), and the guard is repeated.
        var third = T0.AddMinutes(5);
        Assert.Equal(HoldRequestResult.Refused, m.Request(third, agentResumed: true));
        Assert.Equal(GuardStartOutcome.Started, m.LastGuard!.Outcome);
        Assert.True(m.LastGuard.Guard!.Repeated);
        m.TickUntil(third.AddSeconds(0.5), third.AddSeconds(59.5));
        Assert.False(m.GameSuspended);
        m.TickUntil(third.AddSeconds(60), third.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Sign_out_and_cancel_cycle_gives_at_most_about_70_seconds_per_cycle_and_is_reported()
    {
        // Review finding 3: each real sign-out with Cancel resumes the game locally. The gain of one cycle is bounded
        // (the guard and the Pending Suspension), but the number of cycles is not: the parent gets an alert instead.
        var m = AfterUnconfirmedHold();
        var cycles = 0;
        var repeated = 0;
        var maxRunSeconds = 0.0;
        var run = 0.0;
        var t = T0.AddMinutes(2);
        while (t < T0.AddMinutes(15))
        {
            if (m.GameSuspended)
            {
                m.Request(t, agentResumed: true);   // the child signs out and cancels at once
                cycles++;
                if (m.LastGuard?.Guard?.Repeated == true)
                    repeated++;
            }
            m.Tick(t);
            run = m.GameSuspended ? 0 : run + 0.5;
            maxRunSeconds = Math.Max(maxRunSeconds, run);
            t = t.AddSeconds(0.5);
        }

        Assert.InRange(maxRunSeconds, 60, 75);
        Assert.True(cycles >= 5);
        Assert.Equal(cycles - 1, repeated);     // every guard after the first in the window is reported
    }

    [Fact]
    public void Request_with_curl_alone_after_a_refused_hold_gains_nothing()
    {
        var m = AfterUnconfirmedHold();

        Assert.Equal(HoldRequestResult.Refused, m.Request(T0.AddMinutes(5), agentResumed: false));
        m.Tick(T0.AddMinutes(5).AddSeconds(10));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Chain_of_fake_requests_cannot_keep_the_game_running()
    {
        var m = new Model();
        // Fake request 1 is granted: the service resumes the game.
        m.Request(T0, agentResumed: false);
        m.Tick(T0.AddSeconds(60.5));            // the hold ends: Pending Suspension starts
        Assert.False(m.GameSuspended);

        // Fake request 2 in the Pending Suspension: refused, and no guard starts.
        Assert.Equal(HoldRequestResult.Refused, m.Request(T0.AddSeconds(65), agentResumed: false));
        Assert.Equal(GuardStartOutcome.NothingToCover, m.LastGuard!.Outcome);
        Assert.Empty(m.Guards.Covered(T0.AddSeconds(66)));
        m.Tick(T0.AddSeconds(71));
        Assert.True(m.GameSuspended);

        // A resume with a tool and fake request 3: one guard covers the game, and its time is bounded.
        var t3 = T0.AddMinutes(3);
        Assert.Equal(HoldRequestResult.Refused, m.Request(t3, agentResumed: true));
        m.Tick(t3.AddSeconds(30));
        Assert.False(m.GameSuspended);
        m.Tick(t3.AddSeconds(60.5));            // the guard ends: Pending Suspension starts

        // Fake request 4 in that Pending Suspension: no new guard, the game is suspended.
        Assert.Equal(HoldRequestResult.Refused, m.Request(t3.AddSeconds(65), agentResumed: false));
        Assert.Equal(GuardStartOutcome.NothingToCover, m.LastGuard!.Outcome);
        Assert.Empty(m.Guards.Covered(t3.AddSeconds(66)));
        m.Tick(t3.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Process_that_stayed_suspended_under_a_guard_is_covered_again_at_a_later_sign_out()
    {
        // The agent could not resume the game, so it stayed suspended. After the guard the service sees it suspended.
        var m = AfterUnconfirmedHold();

        m.Request(T0.AddMinutes(3), agentResumed: false);
        m.Tick(T0.AddMinutes(3).AddSeconds(30));
        Assert.False(m.Guards.IsEligible(10, GameStart));        // covered: not eligible during its guard
        m.Tick(T0.AddMinutes(3).AddSeconds(61));
        Assert.True(m.Guards.IsEligible(10, GameStart));

        Assert.Equal(HoldRequestResult.Refused, m.Request(T0.AddMinutes(6), agentResumed: true));
        m.Tick(T0.AddMinutes(6).AddSeconds(20));
        Assert.False(m.GameSuspended);
    }

    // --- Known gaps (review finding 2). These tests pin the accepted behaviour; design.md "Risks" explains it. ---

    [Fact]
    public void Gap_sign_out_during_a_running_guard_is_not_extended()
    {
        // Second sign-out at t0 (guard), Cancel, third sign-out at +20 s. The guard is not extended, so the game is
        // suspended again at about +71 s, while the third blocking screen of Windows can last until about +85 s.
        var m = AfterUnconfirmedHold();
        var second = T0.AddMinutes(3);
        m.Request(second, agentResumed: true);

        Assert.Equal(HoldRequestResult.Refused, m.Request(second.AddSeconds(20), agentResumed: true));
        Assert.Equal(GuardStartOutcome.AlreadyRunning, m.LastGuard!.Outcome);
        m.TickUntil(second.AddSeconds(20.5), second.AddSeconds(59.5));
        Assert.False(m.GameSuspended);
        m.TickUntil(second.AddSeconds(60), second.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Gap_sign_out_in_the_re_suspension_window_gets_no_guard()
    {
        // After the guard ends (+60 s) and before the re-suspension (about +71 s), the record is not eligible,
        // so a refused request covers nothing and the Pending Suspension goes on.
        var m = AfterUnconfirmedHold();
        var second = T0.AddMinutes(3);
        m.Request(second, agentResumed: true);
        m.TickUntil(second.AddSeconds(0.5), second.AddSeconds(62));
        Assert.False(m.GameSuspended);

        Assert.Equal(HoldRequestResult.Refused, m.Request(second.AddSeconds(63), agentResumed: true));
        Assert.Equal(GuardStartOutcome.NothingToCover, m.LastGuard!.Outcome);
        m.TickUntil(second.AddSeconds(63.5), second.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }
}
