using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class SessionEndHoldsTests
{
    private const int Kid = 2;
    private const int Session = 1;
    private const int OtherSession = 4;
    private static readonly DateTime T0 = new(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 26);
    private static readonly DateOnly Tomorrow = new(2026, 11, 27);

    private static Func<int, bool> Exists(params int[] sessions) => sessions.Contains;

    /// <summary>A hold of the Kid session that times out while the session still exists.</summary>
    private static ExpiredHold Unconfirmed(SessionEndHolds holds, DateTime start, DateOnly date, bool trusted = false)
    {
        Assert.Equal(HoldRequestResult.Granted, holds.Request(Session, Kid, start, trusted));
        return Assert.Single(holds.Expire(start + SessionEndHolds.HoldDuration, date, Exists(Session)));
    }

    [Fact]
    public void First_request_is_granted_and_holds_the_session()
    {
        var holds = new SessionEndHolds();
        Assert.Equal(HoldRequestResult.Granted, holds.Request(Session, Kid, T0, trusted: false));
        Assert.Equal([Session], holds.HeldSessions(T0.AddSeconds(5)));
        Assert.Equal(T0.AddSeconds(60), holds.NextEndUtc);
    }

    [Fact]
    public void Hold_ends_after_60_seconds()
    {
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: false);
        Assert.Contains(Session, holds.HeldSessions(T0.AddSeconds(59.9)));
        Assert.Empty(holds.HeldSessions(T0.AddSeconds(60)));
        Assert.Empty(holds.Expire(T0.AddSeconds(59.9), Today, Exists(Session)));
        Assert.Single(holds.Expire(T0.AddSeconds(60), Today, Exists(Session)));
        Assert.Null(holds.NextEndUtc);
    }

    [Fact]
    public void Request_for_a_held_session_does_not_extend_the_hold()
    {
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: false);
        Assert.Equal(HoldRequestResult.AlreadyHeld, holds.Request(Session, Kid, T0.AddSeconds(30), trusted: false));
        Assert.Equal(HoldRequestResult.AlreadyHeld, holds.Request(Session, Kid, T0.AddSeconds(30), trusted: true));
        Assert.Empty(holds.HeldSessions(T0.AddSeconds(60)));
    }

    [Fact]
    public void Second_untrusted_request_within_15_minutes_after_an_unconfirmed_hold_is_refused()
    {
        var holds = new SessionEndHolds();
        Assert.False(Unconfirmed(holds, T0, Today).Confirmed);
        Assert.Equal(HoldRequestResult.Refused, holds.Request(Session, Kid, T0.AddMinutes(5), trusted: false));
        Assert.Empty(holds.HeldSessions(T0.AddMinutes(5)));
    }

    [Fact]
    public void Untrusted_request_during_an_active_hold_of_another_session_is_refused()
    {
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: false);
        Assert.Equal(HoldRequestResult.Refused, holds.Request(OtherSession, Kid, T0.AddSeconds(10), trusted: false));
    }

    [Fact]
    public void Hold_that_timed_out_but_was_not_expired_yet_counts()
    {
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: false);
        // 61 s: the time of the hold is over, but no tick ran Expire yet.
        Assert.Equal(HoldRequestResult.Refused, holds.Request(Session, Kid, T0.AddSeconds(61), trusted: false));
    }

    [Fact]
    public void Request_after_a_confirmed_hold_is_granted()
    {
        // Spec scenario "Sign-out, sign-in and sign-out again".
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: false);
        Assert.True(holds.SessionEnded(Session));
        Assert.Empty(holds.HeldSessions(T0.AddSeconds(1)));
        Assert.Equal(HoldRequestResult.Granted, holds.Request(OtherSession, Kid, T0.AddMinutes(5), trusted: false));
    }

    [Fact]
    public void Request_15_minutes_after_an_unconfirmed_hold_is_granted()
    {
        var holds = new SessionEndHolds();
        Unconfirmed(holds, T0, Today);
        Assert.Equal(HoldRequestResult.Refused, holds.Request(Session, Kid, T0.AddMinutes(14.9), trusted: false));
        Assert.Equal(HoldRequestResult.Granted, holds.Request(Session, Kid, T0.AddMinutes(15), trusted: false));
    }

    [Fact]
    public void Trusted_request_is_always_granted()
    {
        var holds = new SessionEndHolds();
        Unconfirmed(holds, T0, Today);
        Assert.Equal(HoldRequestResult.Granted, holds.Request(Session, Kid, T0.AddMinutes(2), trusted: true));
    }

    [Fact]
    public void Hold_whose_session_is_gone_at_the_end_is_confirmed()
    {
        // Forced Logoff removes the session from the tracker at once, so no SessionEnded event may come.
        var holds = new SessionEndHolds();
        holds.Request(Session, Kid, T0, trusted: true);
        var expired = Assert.Single(holds.Expire(T0.AddSeconds(60), Today, Exists()));
        Assert.True(expired.Confirmed);
        Assert.False(expired.Alert);
        Assert.Equal(HoldRequestResult.Granted, holds.Request(Session, Kid, T0.AddMinutes(1), trusted: false));
    }

    [Fact]
    public void Unconfirmed_trusted_hold_counts_against_the_limit()
    {
        var holds = new SessionEndHolds();
        var expired = Unconfirmed(holds, T0, Today, trusted: true);
        Assert.True(expired.Trusted);
        Assert.False(expired.Confirmed);
        Assert.Equal(HoldRequestResult.Refused, holds.Request(Session, Kid, T0.AddMinutes(2), trusted: false));
    }

    [Fact]
    public void First_unconfirmed_hold_of_a_day_asks_for_an_alert_and_the_second_does_not()
    {
        var holds = new SessionEndHolds();
        Assert.True(Unconfirmed(holds, T0, Today).Alert);
        Assert.False(Unconfirmed(holds, T0.AddMinutes(20), Today).Alert);
    }

    [Fact]
    public void Unconfirmed_hold_on_the_next_day_asks_for_an_alert_again()
    {
        var holds = new SessionEndHolds();
        Assert.True(Unconfirmed(holds, T0, Today).Alert);
        Assert.True(Unconfirmed(holds, T0.AddHours(12), Tomorrow).Alert);
    }

    [Fact]
    public void Alert_is_per_user()
    {
        var holds = new SessionEndHolds();
        Assert.True(Unconfirmed(holds, T0, Today).Alert);
        holds.Request(OtherSession, 3, T0.AddMinutes(1), trusted: false);
        var other = Assert.Single(holds.Expire(T0.AddMinutes(2), Today, Exists(OtherSession)));
        Assert.Equal(3, other.UserId);
        Assert.True(other.Alert);
    }
}
