using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

/// <summary>Spec: category-limits "Session End Guard" and "Limit the Session End Guard" (issue #47).</summary>
public class SessionEndGuardsTests
{
    private const int Session = 1;
    private const int OtherSession = 4;
    private static readonly DateTime T0 = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start1 = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start2 = new(2026, 11, 27, 9, 5, 0, DateTimeKind.Utc);
    private static readonly (int, DateTime) Game = (10, Start1);
    private static readonly (int, DateTime) Pad = (11, Start2);

    [Fact]
    public void Guard_covers_the_processes_that_the_service_suspended()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);

        var guard = guards.Start(Session, [Game, Pad], T0);

        Assert.NotNull(guard);
        Assert.Equal([Game, Pad], guard!.Covered);
        Assert.Equal(T0.AddSeconds(60), guard.EndUtc);
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

        var guard = guards.Start(Session, [Game], T0);

        Assert.Empty(guard!.Covered);
        Assert.Empty(guards.Covered(T0));
    }

    [Fact]
    public void Guard_does_not_cover_a_process_that_the_service_never_suspended()
    {
        var guards = new SessionEndGuards();
        Assert.Empty(guards.Start(Session, [Game], T0)!.Covered);
    }

    [Fact]
    public void Guard_covers_only_the_processes_that_the_caller_lists_for_its_session()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.MarkSuspended(11, Start2);

        Assert.Equal([Game], guards.Start(Session, [Game], T0)!.Covered);
        Assert.True(guards.IsEligible(11, Start2));
    }

    [Fact]
    public void Process_is_covered_at_most_once_for_each_suspension()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, [Game], T0);
        guards.Expire(T0.AddSeconds(60));

        // The next request comes in the Pending Suspension after the guard: the game is not covered again.
        var second = guards.Start(Session, [Game], T0.AddSeconds(65));
        Assert.Empty(second!.Covered);
        Assert.Empty(guards.Covered(T0.AddSeconds(66)));

        // The service suspends the game again: a later guard can cover it again.
        guards.Expire(T0.AddSeconds(125));
        guards.MarkSuspended(10, Start1);
        Assert.Equal([Game], guards.Start(Session, [Game], T0.AddSeconds(130))!.Covered);
    }

    [Fact]
    public void Request_during_a_running_guard_does_not_extend_it()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, [Game], T0);
        guards.MarkSuspended(11, Start2);

        Assert.Null(guards.Start(Session, [Game, Pad], T0.AddSeconds(30)));
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
        guards.Start(Session, [Game], T0);

        Assert.Equal([Pad], guards.Start(OtherSession, [Pad], T0.AddSeconds(10))!.Covered);
    }

    [Fact]
    public void Guard_ends_by_its_time()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, [Game], T0);

        Assert.Empty(guards.Expire(T0.AddSeconds(59.9)));
        Assert.NotEmpty(guards.Covered(T0.AddSeconds(59.9)));
        Assert.Single(guards.Expire(T0.AddSeconds(60)));
        Assert.Null(guards.NextEndUtc);
        Assert.Empty(guards.Covered(T0.AddSeconds(60)));
    }

    [Fact]
    public void Guard_ends_with_the_session()
    {
        var guards = new SessionEndGuards();
        guards.MarkSuspended(10, Start1);
        guards.Start(Session, [Game], T0);

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
    public void Guard_has_the_length_of_a_hold()
    {
        Assert.Equal(SessionEndHolds.HoldDuration, SessionEndGuards.Duration);
    }
}

/// <summary>
/// The hold, the guard, the planner and the Pending Suspension together, in the order of <c>CategoryEnforcer</c>.
/// Spec: "Limit the Session End Guard" (a chain of fake requests) and "Session End Guard" (the second sign-out).
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
        public readonly SuspensionRecord Record = new(10, GameStart, Session, Kid, AppCategory.Games, Game);

        public Model() => Guards.MarkSuspended(Record.Pid, Record.StartTimeUtc);

        /// <summary>The session-end request. The agent resumed the game before (<paramref name="agentResumed"/>).</summary>
        public HoldRequestResult Request(DateTime now, bool agentResumed)
        {
            if (agentResumed)
                GameSuspended = false;
            var result = Holds.Request(Session, Kid, now, trusted: false);
            if (result == HoldRequestResult.Refused)
            {
                Guards.Start(Session, [(Record.Pid, Record.StartTimeUtc)], now);
            }
            else
            {
                GameSuspended = false;
                Guards.MarkResumed(Record.Pid, Record.StartTimeUtc);
            }
            return result;
        }

        public void Tick(DateTime now)
        {
            Holds.Expire(now, Today, _ => true);
            Guards.Expire(now);
            var guarded = Guards.Covered(now);
            if (GameSuspended && !guarded.Contains((Record.Pid, Record.StartTimeUtc)))
                Guards.MarkSuspended(Record.Pid, Record.StartTimeUtc);

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
    }

    [Fact]
    public void Second_sign_out_within_15_minutes_keeps_the_resumed_game_running_during_the_guard()
    {
        var m = new Model();
        Assert.Equal(HoldRequestResult.Granted, m.Request(T0, agentResumed: true));
        m.Tick(T0.AddSeconds(60.5));            // the hold ends without the end of the session
        m.Tick(T0.AddSeconds(71));              // Pending Suspension ready
        Assert.True(m.GameSuspended);

        var second = T0.AddMinutes(7);
        Assert.Equal(HoldRequestResult.Refused, m.Request(second, agentResumed: true));
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
    public void Request_with_curl_alone_after_a_refused_hold_gains_nothing()
    {
        var m = new Model();
        m.Request(T0, agentResumed: true);
        m.Tick(T0.AddSeconds(60.5));
        m.Tick(T0.AddSeconds(71));

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

        // Fake request 2 in the Pending Suspension: refused, and the guard does not cover the game.
        Assert.Equal(HoldRequestResult.Refused, m.Request(T0.AddSeconds(65), agentResumed: false));
        Assert.Empty(m.Guards.Covered(T0.AddSeconds(66)));
        m.Tick(T0.AddSeconds(71));
        Assert.True(m.GameSuspended);

        // A resume with a tool and fake request 3: one guard covers the game, and its time is bounded.
        var t3 = T0.AddMinutes(3);             // after the guard of request 2 ended
        Assert.Equal(HoldRequestResult.Refused, m.Request(t3, agentResumed: true));
        m.Tick(t3.AddSeconds(30));
        Assert.False(m.GameSuspended);
        m.Tick(t3.AddSeconds(60.5));            // the guard ends: Pending Suspension starts

        // Fake request 4 in that Pending Suspension: no new cover, the game is suspended.
        Assert.Equal(HoldRequestResult.Refused, m.Request(t3.AddSeconds(65), agentResumed: false));
        Assert.Empty(m.Guards.Covered(t3.AddSeconds(66)));
        m.Tick(t3.AddSeconds(71));
        Assert.True(m.GameSuspended);
    }

    [Fact]
    public void Process_that_stayed_suspended_under_a_guard_is_covered_again_at_a_later_sign_out()
    {
        // The agent could not resume the game, so it stayed suspended. After the guard the service sees it suspended.
        var m = new Model();
        m.Request(T0, agentResumed: true);
        m.Tick(T0.AddSeconds(60.5));
        m.Tick(T0.AddSeconds(71));

        m.Request(T0.AddMinutes(3), agentResumed: false);
        m.Tick(T0.AddMinutes(3).AddSeconds(61));
        Assert.True(m.Guards.IsEligible(10, GameStart));

        Assert.Equal(HoldRequestResult.Refused, m.Request(T0.AddMinutes(6), agentResumed: true));
        m.Tick(T0.AddMinutes(6).AddSeconds(20));
        Assert.False(m.GameSuspended);
    }
}
