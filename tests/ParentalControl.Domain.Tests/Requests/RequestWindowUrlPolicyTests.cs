using ParentalControl.Domain.Requests;

namespace ParentalControl.Domain.Tests.Requests;

public class RequestWindowUrlPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5050/request")]
    [InlineData("http://127.0.0.1:5050/request?user=Kid")]
    [InlineData("http://127.0.0.1:5050/REQUEST")]
    [InlineData("http://127.0.0.1:5050/request?user=Kid2#top")]
    public void AllowsTheRequestPage(string url) => Assert.True(RequestWindowUrlPolicy.IsAllowedDocument(url));

    [Theory]
    [InlineData("http://127.0.0.1:5050/admin")]
    [InlineData("http://127.0.0.1:5050/request/x")]
    [InlineData("http://127.0.0.1:5050/requestx")]
    [InlineData("http://127.0.0.1:5050/")]
    [InlineData("https://127.0.0.1:5050/request")]
    [InlineData("http://localhost:5050/request")]
    [InlineData("http://127.0.0.1:5051/request")]
    [InlineData("http://127.0.0.1/request")]
    [InlineData("http://127.0.0.1.example.com:5050/request")]
    [InlineData("http://x@127.0.0.1:5050/request")]
    [InlineData("http://[::1]:5050/request")]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:blank")]
    [InlineData("edge://settings")]
    [InlineData("view-source:http://127.0.0.1:5050/request")]
    [InlineData("blob:http://127.0.0.1:5050/0f8e7a4c-6d1b-4a35-9d2e-111111111111")]
    [InlineData("/request")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesEachOtherDocument(string? url) => Assert.False(RequestWindowUrlPolicy.IsAllowedDocument(url));

    [Theory]
    [InlineData("http://127.0.0.1:5050/admin/assets/x.js")]
    [InlineData("http://127.0.0.1:5050/api/request")]
    [InlineData("http://127.0.0.1:5050/request")]
    public void AllowsResourcesOfTheOrigin(string url) => Assert.True(RequestWindowUrlPolicy.IsAllowedResource(url));

    [Theory]
    [InlineData("https://example.com/x.png")]
    [InlineData("http://localhost:5050/api/request")]
    [InlineData("https://127.0.0.1:5050/api/request")]
    [InlineData("http://127.0.0.1:5051/x.png")]
    [InlineData("http://x@127.0.0.1:5050/x.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("not a url")]
    public void RefusesResourcesOfOtherOrigins(string url) => Assert.False(RequestWindowUrlPolicy.IsAllowedResource(url));

    [Fact]
    public void PageUrlEscapesTheUserAndPassesTheFilter()
    {
        var url = RequestWindowUrlPolicy.PageUrl("Kid & Co");
        Assert.Equal("http://127.0.0.1:5050/request?user=Kid%20%26%20Co", url);
        Assert.True(RequestWindowUrlPolicy.IsAllowedDocument(url));
    }
}
