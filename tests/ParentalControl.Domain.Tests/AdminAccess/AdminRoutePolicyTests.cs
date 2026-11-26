using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class AdminRoutePolicyTests
{
    [Theory]
    [InlineData("/api/admin/grant")]
    [InlineData("/API/Admin/Grant")]
    [InlineData("/api/admin")]
    [InlineData("/api/admin/")]
    [InlineData("//api/admin/grant")]
    [InlineData("/api//admin/grant")]
    [InlineData("/api/admin/grant/")]
    [InlineData("/api/admin/app-rules/{id:long}")]
    [InlineData("/api/admin/captures/{id:int}")]
    [InlineData("/api/admin/login/extra")]
    [InlineData("/api/admin/settings")]
    [InlineData("/api/admin/blocked-starts")]
    [InlineData("/api/admin/app-rules")]
    [InlineData("api/admin/grant")]
    [InlineData("/api/admin/breaks")]
    [InlineData("/api/admin/breaks/{userId:int}")]
    [InlineData("/api/admin/breaks/{userId:int}/end")]
    [InlineData("/api/admin/breaks/3")]
    [InlineData("/api/admin/breaks/3/end")]
    [InlineData("/API/Admin/Breaks/3/End/")]
    public void Paths_below_api_admin_are_admin(string path)
    {
        Assert.Equal(AdminRouteClass.Admin, AdminRoutePolicy.Classify(path));
    }

    [Theory]
    [InlineData("/api/admin/login")]
    [InlineData("/api/admin/login/")]
    [InlineData("/API/ADMIN/LOGIN")]
    public void Login_paths_are_login(string path)
    {
        Assert.Equal(AdminRouteClass.Login, AdminRoutePolicy.Classify(path));
    }

    [Fact]
    public void Logout_path_is_logout()
    {
        Assert.Equal(AdminRouteClass.Logout, AdminRoutePolicy.Classify("/api/admin/logout"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/api/administrator")]
    [InlineData("/api/adminx/grant")]
    [InlineData("/request")]
    [InlineData("/api/request")]
    [InlineData("/api/agent/status")]
    [InlineData("/api/agent/capture")]
    [InlineData("/admin/api/grant")]
    public void Other_paths_are_open(string? path)
    {
        Assert.Equal(AdminRouteClass.Open, AdminRoutePolicy.Classify(path));
    }

    [Fact]
    public void Admin_endpoint_template_makes_an_open_looking_path_admin()
    {
        // A path variant that routing maps to an admin endpoint goes through the gate.
        Assert.Equal(AdminRouteClass.Admin, AdminRoutePolicy.Classify("/api/admin%2Fgrant", "/api/admin/grant"));
    }

    [Fact]
    public void Admin_path_without_an_endpoint_is_admin()
    {
        Assert.Equal(AdminRouteClass.Admin, AdminRoutePolicy.Classify("/api/admin/unknown", null));
    }

    [Fact]
    public void Login_endpoint_with_its_path_is_login()
    {
        Assert.Equal(AdminRouteClass.Login, AdminRoutePolicy.Classify("/api/admin/login", "/api/admin/login"));
        Assert.Equal(AdminRouteClass.Open, AdminRoutePolicy.Classify("/request", "/request"));
    }

    [Fact]
    public void Open_route_always_passes()
    {
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(AdminRouteClass.Open, "POST", "http://evil:8000", "127.0.0.1:5050", false));
    }

    [Fact]
    public void Admin_route_without_a_session_is_unauthorized()
    {
        Assert.Equal(AdminGateResult.Unauthorized, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "POST", null, "127.0.0.1:5050", false));
        Assert.Equal(AdminGateResult.Unauthorized, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "GET", null, "127.0.0.1:5050", false));
    }

    [Fact]
    public void Admin_route_with_a_session_passes()
    {
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "POST", null, "127.0.0.1:5050", true));
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "POST", "http://127.0.0.1:5050", "127.0.0.1:5050", true));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void State_change_from_another_origin_is_forbidden(string method)
    {
        Assert.Equal(AdminGateResult.Forbidden, AdminRoutePolicy.Decide(AdminRouteClass.Admin, method, "http://localhost:8000", "localhost:5050", true));
    }

    [Theory]
    [InlineData(AdminRouteClass.Login)]
    [InlineData(AdminRouteClass.Logout)]
    public void Login_and_logout_from_another_origin_are_forbidden(AdminRouteClass routeClass)
    {
        Assert.Equal(AdminGateResult.Forbidden, AdminRoutePolicy.Decide(routeClass, "POST", "http://localhost:8000", "localhost:5050", false));
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(routeClass, "POST", "http://localhost:5050", "localhost:5050", false));
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(routeClass, "POST", null, "localhost:5050", false));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("https://localhost:5050")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:5050.evil")]
    public void Origin_that_is_not_exactly_http_plus_host_is_forbidden(string origin)
    {
        Assert.Equal(AdminGateResult.Forbidden, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "POST", origin, "localhost:5050", true));
    }

    [Fact]
    public void Origin_is_not_checked_for_a_read()
    {
        Assert.Equal(AdminGateResult.Pass, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "GET", "http://localhost:8000", "localhost:5050", true));
    }

    [Fact]
    public void Missing_host_header_does_not_match_an_origin()
    {
        Assert.Equal(AdminGateResult.Forbidden, AdminRoutePolicy.Decide(AdminRouteClass.Admin, "POST", "http://", null, true));
    }
}
