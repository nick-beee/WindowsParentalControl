using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class WarningLimitTests
{
    private static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void Same_warning_logs_once_per_10_minutes_with_the_count_of_the_left_out_warnings()
    {
        var limit = new WarningLimit();
        Assert.True(limit.Take("2:CreateEnvironmentBlock (5)", M(0), out var suppressed));
        Assert.Equal(0, suppressed);
        for (var i = 1; i < 20; i++) Assert.False(limit.Take("2:CreateEnvironmentBlock (5)", M(i * 0.5), out _));
        Assert.True(limit.Take("2:CreateEnvironmentBlock (5)", M(10), out suppressed));
        Assert.Equal(19, suppressed);
    }

    [Fact]
    public void Other_key_logs_at_once()
    {
        var limit = new WarningLimit();
        Assert.True(limit.Take("2:a", M(0), out _));
        Assert.True(limit.Take("3:a", M(1), out _));
        Assert.True(limit.Take("2:b", M(1), out _));
    }
}
