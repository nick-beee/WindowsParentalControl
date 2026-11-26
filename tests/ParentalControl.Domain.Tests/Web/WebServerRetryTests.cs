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
}
