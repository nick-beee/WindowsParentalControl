using ParentalControl.Domain.Captures;

namespace ParentalControl.Domain.Tests.Captures;

public class CaptureQuotaTests
{
    [Fact]
    public void First_capture_of_the_day_is_allowed_with_the_size_limit_of_one_capture()
    {
        var decision = CaptureQuota.Check(0, 0);
        Assert.True(decision.Allowed);
        Assert.Equal(CaptureQuota.MaxBytesPerCapture, decision.MaxBytes);
    }

    [Fact]
    public void File_limit_refuses_the_next_capture()
    {
        Assert.True(CaptureQuota.Check(CaptureQuota.MaxFilesPerDay - 1, 0).Allowed);
        var decision = CaptureQuota.Check(CaptureQuota.MaxFilesPerDay, 0);
        Assert.Equal(CaptureQuotaOutcome.RefuseFiles, decision.Outcome);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Byte_limit_refuses_the_next_capture_and_limits_the_last_one()
    {
        Assert.Equal(CaptureQuotaOutcome.RefuseBytes, CaptureQuota.Check(1, CaptureQuota.MaxBytesPerDay).Outcome);
        var last = CaptureQuota.Check(1, CaptureQuota.MaxBytesPerDay - 1000);
        Assert.True(last.Allowed);
        Assert.Equal(1000, last.MaxBytes);
    }

    [Fact]
    public void Quota_values_are_safe()
    {
        Assert.Equal(200, CaptureQuota.MaxFilesPerDay);
        Assert.Equal(50L * 1024 * 1024, CaptureQuota.MaxBytesPerDay);
        Assert.True(CaptureQuota.MaxBytesPerCapture <= CaptureQuota.MaxBytesPerDay);
    }

    [Fact]
    public void Refusal_is_logged_one_time_per_user_and_day()
    {
        var log = new CaptureRefusalLog();
        var today = new DateOnly(2026, 11, 26);
        Assert.True(log.Count(1, today));
        Assert.False(log.Count(1, today));
        Assert.False(log.Count(1, today));
        Assert.True(log.Count(2, today));
        Assert.Equal(3, log.RefusalsToday(1, today));
        Assert.True(log.Count(1, today.AddDays(1)));
        Assert.Equal(1, log.RefusalsToday(1, today.AddDays(1)));
    }
}
