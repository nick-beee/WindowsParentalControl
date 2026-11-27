using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests;

public class LockDeferralsTests
{
    private const int Kid = 2;
    private const int OtherKid = 3;
    private const int Session = 1;
    private const int OtherSession = 4;
    private static readonly DateTime T0 = new(2026, 11, 27, 14, 0, 0, DateTimeKind.Utc);

    private static Func<int, bool> Exists(params int[] sessions) => sessions.Contains;

    /// <summary>A deferral of the Kid session that owes a lock and times out while the session still exists.</summary>
    private static EndedDeferral OwedAndUnconfirmed(LockDeferrals deferrals, DateTime start)
    {
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, start, trusted: false));
        Assert.True(deferrals.TryDefer(Session, start.AddSeconds(1), out _));
        return Assert.Single(deferrals.Expire(start + LockDeferrals.Duration, Exists(Session)));
    }

    [Fact]
    public void Duration_is_75_seconds_and_the_window_15_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(75), LockDeferrals.Duration);
        Assert.Equal(TimeSpan.FromMinutes(15), LockDeferrals.UnconfirmedWindow);
    }

    [Fact]
    public void No_deferral_without_a_request()
    {
        var deferrals = new LockDeferrals();
        Assert.False(deferrals.TryDefer(Session, T0, out _));
        Assert.Null(deferrals.ActiveEnd(Session, T0));
        Assert.Null(deferrals.NextEnd);
    }

    [Fact]
    public void Granted_request_defers_a_lock_for_75_seconds()
    {
        var deferrals = new LockDeferrals();
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, T0, trusted: false));

        Assert.True(deferrals.TryDefer(Session, T0.AddSeconds(0.3), out var ends));
        Assert.Equal(T0.AddSeconds(75), ends);
        Assert.True(deferrals.TryDefer(Session, T0.AddSeconds(74.9), out _));
        Assert.False(deferrals.TryDefer(Session, T0.AddSeconds(75), out _));
        Assert.Equal(T0.AddSeconds(75), deferrals.NextEnd);
    }

    [Fact]
    public void Works_with_a_monotonic_time_base_that_starts_at_zero()
    {
        // The service gives Environment.TickCount64 as a DateTime: the time since the start of Windows, from year 1.
        var boot = DateTime.MinValue.AddMinutes(3);
        var deferrals = new LockDeferrals();
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, boot, trusted: false));
        Assert.True(deferrals.TryDefer(Session, boot.AddSeconds(1), out var ends));
        Assert.Equal(boot.AddSeconds(75), ends);

        var ended = Assert.Single(deferrals.Expire(boot.AddSeconds(75), Exists(Session)));
        Assert.True(ended.Owed);
        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(Session, Kid, boot.AddMinutes(5), trusted: false));
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, boot.AddMinutes(15), trusted: false));
    }

    [Fact]
    public void Deferral_applies_only_to_its_session()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        Assert.False(deferrals.TryDefer(OtherSession, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Request_during_a_deferral_does_not_extend_it()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        for (var s = 10; s < 75; s += 10)
            Assert.Equal(DeferralRequestResult.AlreadyDeferred, deferrals.Request(Session, Kid, T0.AddSeconds(s), trusted: false));

        Assert.Equal(T0.AddSeconds(75), deferrals.ActiveEnd(Session, T0.AddSeconds(70)));
        Assert.False(deferrals.TryDefer(Session, T0.AddSeconds(75), out _));
    }

    [Fact]
    public void ActiveEnd_does_not_mark_the_deferral_as_owed()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        Assert.NotNull(deferrals.ActiveEnd(Session, T0.AddSeconds(1)));

        var ended = Assert.Single(deferrals.Expire(T0.AddSeconds(75), Exists(Session)));
        Assert.False(ended.Owed);
    }

    [Fact]
    public void Owed_deferral_that_times_out_with_the_session_still_there_is_unconfirmed()
    {
        var deferrals = new LockDeferrals();
        var ended = OwedAndUnconfirmed(deferrals, T0);
        Assert.True(ended.Owed);
        Assert.False(ended.Confirmed);
        Assert.Null(deferrals.NextEnd);
    }

    [Fact]
    public void Expire_before_the_end_keeps_the_deferral()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        Assert.Empty(deferrals.Expire(T0.AddSeconds(74.9), Exists(Session)));
        Assert.True(deferrals.TryDefer(Session, T0.AddSeconds(74.9), out _));
    }

    [Fact]
    public void Unconfirmed_owed_deferral_refuses_the_next_request_for_15_minutes_from_its_start()
    {
        var deferrals = new LockDeferrals();
        OwedAndUnconfirmed(deferrals, T0);

        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(Session, Kid, T0.AddMinutes(5), trusted: false));
        Assert.False(deferrals.TryDefer(Session, T0.AddMinutes(5).AddSeconds(1), out _));
        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(Session, Kid, T0.AddMinutes(15).AddSeconds(-1), trusted: false));
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, T0.AddMinutes(15), trusted: false));
    }

    [Fact]
    public void Refusal_applies_to_another_session_of_the_same_user_but_not_to_another_user()
    {
        var deferrals = new LockDeferrals();
        OwedAndUnconfirmed(deferrals, T0);

        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(OtherSession, Kid, T0.AddMinutes(2), trusted: false));
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(OtherSession, OtherKid, T0.AddMinutes(2), trusted: false));
    }

    [Fact]
    public void Deferral_that_owed_no_lock_does_not_count()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        var ended = Assert.Single(deferrals.Expire(T0.AddSeconds(75), Exists(Session)));
        Assert.False(ended.Owed);
        Assert.False(ended.Confirmed);

        // A real sign-out and Cancel while no lock is due: the next sign-out at the limit is still protected.
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, T0.AddMinutes(2), trusted: false));
        Assert.True(deferrals.TryDefer(Session, T0.AddMinutes(2).AddSeconds(0.3), out _));
    }

    [Fact]
    public void Deferral_that_ended_with_the_session_does_not_count()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        Assert.True(deferrals.TryDefer(Session, T0.AddSeconds(1), out _));

        var ended = Assert.Single(deferrals.SessionEnded(Session));
        Assert.True(ended.Owed);
        Assert.True(ended.Confirmed);
        Assert.False(deferrals.TryDefer(Session, T0.AddSeconds(2), out _));
        Assert.Null(deferrals.NextEnd);

        // Sign-in again and sign out again 5 minutes later, with a due lock.
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(OtherSession, Kid, T0.AddMinutes(5), trusted: false));
    }

    [Fact]
    public void Owed_deferral_that_expires_after_the_session_ended_is_confirmed()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        deferrals.TryDefer(Session, T0.AddSeconds(1), out _);

        var ended = Assert.Single(deferrals.Expire(T0.AddSeconds(75), Exists()));
        Assert.True(ended.Confirmed);
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(OtherSession, Kid, T0.AddMinutes(1), trusted: false));
    }

    [Fact]
    public void Owed_deferral_that_did_not_end_yet_blocks_a_request_of_another_session()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0, trusted: false);
        deferrals.TryDefer(Session, T0.AddSeconds(1), out _);

        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(OtherSession, Kid, T0.AddSeconds(10), trusted: false));
        // Over but not processed by Expire yet: it still counts.
        Assert.Equal(DeferralRequestResult.Refused, deferrals.Request(Session, Kid, T0.AddSeconds(80), trusted: false));
    }

    [Fact]
    public void Trusted_request_is_never_refused()
    {
        var deferrals = new LockDeferrals();
        OwedAndUnconfirmed(deferrals, T0);

        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, T0.AddMinutes(3), trusted: true));
        Assert.True(deferrals.TryDefer(Session, T0.AddMinutes(3).AddSeconds(1), out _));
    }

    [Fact]
    public void NextEnd_is_the_earliest_end()
    {
        var deferrals = new LockDeferrals();
        deferrals.Request(Session, Kid, T0.AddSeconds(10), trusted: false);
        deferrals.Request(OtherSession, OtherKid, T0, trusted: false);
        Assert.Equal(T0.AddSeconds(75), deferrals.NextEnd);

        deferrals.Expire(T0.AddSeconds(75), Exists(Session, OtherSession));
        Assert.Equal(T0.AddSeconds(85), deferrals.NextEnd);
    }

    /// <summary>
    /// The bound of design D3: the child sends a fake request every 10 s for two hours and never signs out, and a lock is
    /// due all the time. Each deferral ends 75 s after its start, and at most one deferral starts in each 15 minutes.
    /// </summary>
    [Fact]
    public void Fake_requests_while_a_lock_is_always_due_give_at_most_one_deferral_in_15_minutes()
    {
        var deferrals = new LockDeferrals();
        var starts = new List<DateTime>();
        var deferredSeconds = 0;

        for (var second = 0; second < 2 * 3600; second++)
        {
            var now = T0.AddSeconds(second);
            deferrals.Expire(now, Exists(Session));
            if (second % 10 == 0 && deferrals.Request(Session, Kid, now, trusted: false) == DeferralRequestResult.Granted)
                starts.Add(now);
            // The tick of the service: a lock is due each second.
            if (deferrals.TryDefer(Session, now, out var ends))
            {
                deferredSeconds++;
                Assert.Equal(starts[^1] + LockDeferrals.Duration, ends);
            }
        }

        Assert.Equal(8, starts.Count);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] - starts[i - 1] >= LockDeferrals.UnconfirmedWindow);
        Assert.Equal(8 * 75, deferredSeconds);
    }

    /// <summary>Requests while no lock is due cost nothing: a deferral at the limit later in the day is still granted.</summary>
    [Fact]
    public void Requests_while_no_lock_is_due_do_not_use_the_limit()
    {
        var deferrals = new LockDeferrals();
        for (var minute = 0; minute < 120; minute++)
        {
            var now = T0.AddMinutes(minute);
            deferrals.Expire(now, Exists(Session));
            deferrals.Request(Session, Kid, now, trusted: false);
        }

        var atLimit = T0.AddMinutes(121);
        deferrals.Expire(atLimit, Exists(Session));
        Assert.Equal(DeferralRequestResult.Granted, deferrals.Request(Session, Kid, atLimit, trusted: false));
        Assert.True(deferrals.TryDefer(Session, atLimit.AddSeconds(0.3), out _));
    }
}
