namespace ParentalControl.Domain.AdminAccess;

public enum AdminRouteClass
{
    /// <summary>A route that works without an Admin Session (the child-facing routes and the <c>/admin</c> page).</summary>
    Open,
    Login,
    Logout,
    /// <summary>A route below <c>/api/admin/</c> that requires an Admin Session.</summary>
    Admin,
}

public enum AdminGateResult
{
    Pass,
    Unauthorized,
    Forbidden,
}

/// <summary>
/// Decides which routes need an Admin Session. Deny by default: each path or route template below
/// <c>/api/admin/</c> is <see cref="AdminRouteClass.Admin"/>, except the login and the logout.
/// The match uses whole path segments, ignores letter case, and ignores empty segments and a trailing <c>/</c>.
/// </summary>
public static class AdminRoutePolicy
{
    public static AdminRouteClass Classify(string? pathOrTemplate)
    {
        if (string.IsNullOrEmpty(pathOrTemplate))
            return AdminRouteClass.Open;

        var segments = pathOrTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return AdminRouteClass.Open;
        }

        if (segments.Length == 3)
        {
            if (segments[2].Equals("login", StringComparison.OrdinalIgnoreCase))
                return AdminRouteClass.Login;
            if (segments[2].Equals("logout", StringComparison.OrdinalIgnoreCase))
                return AdminRouteClass.Logout;
        }
        return AdminRouteClass.Admin;
    }

    /// <summary>
    /// Classifies a request by its raw path and by the template of the endpoint that routing selected (null if none).
    /// If one of the two is <see cref="AdminRouteClass.Admin"/>, the request is an admin request.
    /// </summary>
    public static AdminRouteClass Classify(string? requestPath, string? endpointTemplate)
    {
        var byPath = Classify(requestPath);
        var byTemplate = Classify(endpointTemplate);
        if (byPath == AdminRouteClass.Admin || byTemplate == AdminRouteClass.Admin)
            return AdminRouteClass.Admin;
        if (byTemplate != AdminRouteClass.Open)
            return byTemplate;
        return byPath;
    }

    /// <summary>
    /// The gate decision for one request.
    /// A state-changing request (each method except GET, HEAD, OPTIONS and TRACE) to the login, the logout or an
    /// admin route with an <c>Origin</c> header that is not <c>http://</c> plus the <c>Host</c> header gets
    /// <see cref="AdminGateResult.Forbidden"/>. An admin route without a valid Admin Session gets
    /// <see cref="AdminGateResult.Unauthorized"/>.
    /// </summary>
    /// <param name="sessionValid">True only if the sign-in is set up and the request has a valid Admin Session.</param>
    public static AdminGateResult Decide(AdminRouteClass routeClass, string method, string? origin, string? host, bool sessionValid)
    {
        if (routeClass == AdminRouteClass.Open)
            return AdminGateResult.Pass;

        if (ChangesState(method) && !string.IsNullOrEmpty(origin) && !IsSameOrigin(origin, host))
            return AdminGateResult.Forbidden;

        if (routeClass == AdminRouteClass.Admin && !sessionValid)
            return AdminGateResult.Unauthorized;

        return AdminGateResult.Pass;
    }

    public static bool ChangesState(string method) =>
        !(HttpMethodIs(method, "GET") || HttpMethodIs(method, "HEAD") || HttpMethodIs(method, "OPTIONS") || HttpMethodIs(method, "TRACE"));

    /// <summary>The web server uses plain HTTP (#7), so its origin is <c>http://</c> plus the <c>Host</c> header.</summary>
    public static bool IsSameOrigin(string origin, string? host) =>
        !string.IsNullOrEmpty(host) && origin.Equals("http://" + host, StringComparison.OrdinalIgnoreCase);

    private static bool HttpMethodIs(string method, string expected) =>
        method.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
