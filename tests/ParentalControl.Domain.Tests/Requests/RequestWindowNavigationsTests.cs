using ParentalControl.Domain.Requests;

namespace ParentalControl.Domain.Tests.Requests;

public class RequestWindowNavigationsTests
{
    [Fact]
    public void FirstPageLoads()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        Assert.Equal(NavigationOutcome.KeepPage, navigations.Completed(1, isSuccess: true));
    }

    [Fact]
    public void FailedFirstNavigationShowsTheFallback()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        Assert.Equal(NavigationOutcome.ShowFallback, navigations.Completed(1, isSuccess: false));
    }

    [Fact]
    public void FirstNavigationWithACancelledRedirectShowsTheFallback()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        // The redirect to /admin keeps the NavigationId of the first event.
        navigations.Started(1, cancelledByAgent: true);
        Assert.Equal(NavigationOutcome.ShowFallback, navigations.Completed(1, isSuccess: false));
    }

    [Fact]
    public void LaterCancelledNavigationKeepsThePage()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        navigations.Completed(1, isSuccess: true);
        navigations.Started(2, cancelledByAgent: true);
        Assert.Equal(NavigationOutcome.KeepPage, navigations.Completed(2, isSuccess: false));
    }

    [Fact]
    public void LaterCancelledRedirectKeepsThePage()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        navigations.Completed(1, isSuccess: true);
        navigations.Started(2, cancelledByAgent: false);
        navigations.Started(2, cancelledByAgent: true);
        Assert.Equal(NavigationOutcome.KeepPage, navigations.Completed(2, isSuccess: false));
    }

    [Fact]
    public void LaterFailedNavigationThatTheAgentDidNotCancelShowsTheFallback()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        navigations.Completed(1, isSuccess: true);
        navigations.Started(2, cancelledByAgent: false);
        Assert.Equal(NavigationOutcome.ShowFallback, navigations.Completed(2, isSuccess: false));
    }

    [Fact]
    public void ACancelledIdDoesNotProtectALaterNavigation()
    {
        var navigations = new RequestWindowNavigations();
        navigations.Started(1, cancelledByAgent: false);
        navigations.Completed(1, isSuccess: true);
        navigations.Started(2, cancelledByAgent: true);
        navigations.Completed(2, isSuccess: false);
        Assert.Equal(NavigationOutcome.ShowFallback, navigations.Completed(2, isSuccess: false));
    }

    [Theory]
    [InlineData(true, false, false, NavigationOutcome.ShowFallback)]
    [InlineData(true, true, false, NavigationOutcome.ShowFallback)]
    [InlineData(true, false, true, NavigationOutcome.KeepPage)]
    [InlineData(false, true, false, NavigationOutcome.KeepPage)]
    [InlineData(false, false, false, NavigationOutcome.ShowFallback)]
    [InlineData(false, false, true, NavigationOutcome.KeepPage)]
    public void Decide(bool isFirst, bool cancelled, bool isSuccess, NavigationOutcome expected) =>
        Assert.Equal(expected, RequestWindowNavigations.Decide(isFirst, cancelled, isSuccess));
}
