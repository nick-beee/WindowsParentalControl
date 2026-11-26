using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class LoginThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    private static List<LoginFailure> Failures(string source, int count, DateTimeOffset start, TimeSpan? gap = null) =>
        Enumerable.Range(0, count).Select(i => new LoginFailure(source, start + (gap ?? TimeSpan.FromMinutes(1)) * i)).ToList();

    [Fact]
    public void Nine_failures_do_not_block()
    {
        var failures = Failures(LoginSource.Local, 9, T0);
        Assert.False(LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(1)).Blocked);
    }

    [Fact]
    public void Ten_failures_block_the_source()
    {
        var failures = Failures(LoginSource.Local, 10, T0);
        var status = LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(1));
        Assert.True(status.Blocked);
        Assert.Equal(LoginLimitScope.Source, status.Scope);
        Assert.Equal(T0.AddHours(24), status.BlockedUntilUtc);
    }

    [Fact]
    public void Block_ends_when_the_oldest_failure_is_24_hours_old()
    {
        var failures = Failures(LoginSource.Local, 10, T0);
        Assert.True(LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(24).AddSeconds(-1)).Blocked);
        Assert.False(LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(24)).Blocked);
    }

    [Fact]
    public void Second_source_is_not_blocked()
    {
        var failures = Failures(LoginSource.Local, 10, T0);
        Assert.False(LoginThrottle.Check(failures, "192.168.1.50", T0.AddHours(1)).Blocked);
    }

    [Fact]
    public void Thirty_failures_from_three_sources_block_all_sources()
    {
        var failures = Failures("a", 10, T0).Concat(Failures("b", 10, T0.AddHours(1))).Concat(Failures("c", 10, T0.AddHours(2))).ToList();
        var status = LoginThrottle.Check(failures, "192.168.1.50", T0.AddHours(3));
        Assert.True(status.Blocked);
        Assert.Equal(LoginLimitScope.AllSources, status.Scope);
        Assert.Equal(T0.AddHours(24), status.BlockedUntilUtc);
        Assert.False(LoginThrottle.Check(failures, "192.168.1.50", T0.AddHours(24)).Blocked);
    }

    [Fact]
    public void Twenty_nine_failures_from_all_sources_do_not_block_a_new_source()
    {
        var failures = Failures("a", 9, T0).Concat(Failures("b", 9, T0)).Concat(Failures("c", 9, T0)).Concat(Failures("d", 2, T0)).ToList();
        Assert.False(LoginThrottle.Check(failures, "e", T0.AddHours(1)).Blocked);
    }

    [Fact]
    public void Old_failures_outside_the_window_do_not_count()
    {
        var failures = Failures(LoginSource.Local, 10, T0.AddHours(-30));
        Assert.False(LoginThrottle.Check(failures, LoginSource.Local, T0).Blocked);
    }

    [Fact]
    public void Failure_that_reaches_the_source_limit_reports_it_one_time()
    {
        var before = Failures(LoginSource.Local, 9, T0);
        var now = T0.AddHours(1);
        var reached = LoginThrottle.AfterFailure(before, LoginSource.Local, now);
        Assert.True(reached.Blocked);
        Assert.Equal(LoginLimitScope.Source, reached.Scope);
        Assert.Equal(T0.AddHours(24), reached.BlockedUntilUtc);

        var eight = Failures(LoginSource.Local, 8, T0);
        Assert.False(LoginThrottle.AfterFailure(eight, LoginSource.Local, now).Blocked);

        var ten = Failures(LoginSource.Local, 10, T0);
        Assert.False(LoginThrottle.AfterFailure(ten, LoginSource.Local, now).Blocked);
    }

    [Fact]
    public void Failure_that_reaches_both_limits_reports_one_alert_for_all_sources()
    {
        var before = Failures("a", 10, T0).Concat(Failures("b", 10, T0)).Concat(Failures("c", 9, T0)).ToList();
        var reached = LoginThrottle.AfterFailure(before, "c", T0.AddHours(1));
        Assert.True(reached.Blocked);
        Assert.Equal(LoginLimitScope.AllSources, reached.Scope);
    }

    [Fact]
    public void Refused_logins_during_a_block_do_not_move_the_end()
    {
        // The caller records no failure while a limit applies, so the end time stays the same.
        var failures = Failures(LoginSource.Local, 10, T0);
        var first = LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(1)).BlockedUntilUtc;
        var later = LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(20)).BlockedUntilUtc;
        Assert.Equal(first, later);
    }

    [Fact]
    public void Valid_login_that_clears_its_source_unblocks_only_that_source()
    {
        var failures = Failures("phone", 2, T0).Concat(Failures(LoginSource.Local, 10, T0)).ToList();
        // The service deletes the failures of the source of a valid login.
        failures.RemoveAll(f => f.Source == "phone");
        Assert.DoesNotContain(failures, f => f.Source == "phone");
        Assert.False(LoginThrottle.Check(failures, "phone", T0.AddHours(1)).Blocked);
        Assert.True(LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(1)).Blocked);
    }

    [Fact]
    public void More_than_ten_failures_block_until_enough_leave_the_window()
    {
        // 12 failures (for example after a race): the block ends when 3 have left the window.
        var failures = Failures(LoginSource.Local, 12, T0, TimeSpan.FromHours(1));
        var status = LoginThrottle.Check(failures, LoginSource.Local, T0.AddHours(12));
        Assert.Equal(T0.AddHours(2 + 24), status.BlockedUntilUtc);
    }
}
