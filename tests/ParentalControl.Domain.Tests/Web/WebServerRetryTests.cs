using ParentalControl.Domain.Web;

namespace ParentalControl.Domain.Tests.Web;

public class WebServerRetryTests
{
    [Fact]
    public void Delay_doubles_up_to_the_maximum()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), WebServerRetry.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), WebServerRetry.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(32), WebServerRetry.Delay(5));
        Assert.Equal(WebServerRetry.MaxDelay, WebServerRetry.Delay(6));
        Assert.Equal(WebServerRetry.MaxDelay, WebServerRetry.Delay(1000));
    }

    [Fact]
    public void No_failure_gives_no_delay()
    {
        Assert.Equal(TimeSpan.Zero, WebServerRetry.Delay(0));
    }

    [Fact]
    public void Only_another_process_on_the_port_is_a_squat()
    {
        Assert.False(WebServerRetry.IsForeignOwner(Array.Empty<int>(), 100));
        Assert.False(WebServerRetry.IsForeignOwner(new[] { 100 }, 100));
        Assert.True(WebServerRetry.IsForeignOwner(new[] { 100, 4242 }, 100));
    }

    [Fact]
    public void Squat_alert_waits_for_the_gate_and_goes_out_one_time()
    {
        var alert = new PortSquatAlert();
        Assert.False(alert.OnBindFailed(foreignOwner: true, gateOpen: false)); // no Telegram settings before the gate
        Assert.True(alert.OnBindFailed(foreignOwner: true, gateOpen: true));
        Assert.False(alert.OnBindFailed(foreignOwner: true, gateOpen: true)); // not one per retry
        Assert.True(alert.OnBound());
        Assert.False(alert.OnBound());
        Assert.True(alert.OnBindFailed(foreignOwner: true, gateOpen: true)); // a new squat gives a new alert
    }

    [Fact]
    public void Failed_bind_without_a_foreign_owner_gives_no_alert()
    {
        var alert = new PortSquatAlert();
        Assert.False(alert.OnBindFailed(foreignOwner: false, gateOpen: true));
        Assert.False(alert.OnBound());
    }
}
