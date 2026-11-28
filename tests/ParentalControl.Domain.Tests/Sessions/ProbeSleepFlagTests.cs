using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class ProbeSleepFlagTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromHours(3);

    [Fact]
    public void Suspend_sets_the_flag_and_resume_clears_it()
    {
        var flag = new ProbeSleepFlag();
        Assert.False(flag.IsSet);
        flag.Suspend(T0);
        Assert.True(flag.IsSet);
        Assert.True(flag.Resume());
        Assert.False(flag.IsSet);
        Assert.False(flag.Resume());
    }

    [Fact]
    public void Real_sleep_keeps_the_flag_because_the_unbiased_time_does_not_move()
    {
        var flag = new ProbeSleepFlag();
        flag.Suspend(T0);
        // The tick right after the suspend event and the first tick after the wake: a few unbiased seconds only.
        Assert.False(flag.ClearIfStale(T0 + TimeSpan.FromSeconds(2)));
        Assert.False(flag.ClearIfStale(T0 + ProbeSleepFlag.StaleAfter));
        Assert.True(flag.IsSet);
    }

    [Fact]
    public void Missing_resume_event_clears_the_flag_once_after_30_seconds_of_unbiased_time()
    {
        var flag = new ProbeSleepFlag();
        flag.Suspend(T0);
        Assert.True(flag.ClearIfStale(T0 + ProbeSleepFlag.StaleAfter + TimeSpan.FromSeconds(1)));
        Assert.False(flag.IsSet);
        Assert.False(flag.ClearIfStale(T0 + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void New_suspend_event_starts_the_30_seconds_again()
    {
        var flag = new ProbeSleepFlag();
        flag.Suspend(T0);
        flag.Suspend(T0 + TimeSpan.FromSeconds(25));
        Assert.False(flag.ClearIfStale(T0 + TimeSpan.FromSeconds(40)));
        Assert.True(flag.ClearIfStale(T0 + TimeSpan.FromSeconds(56)));
    }
}
