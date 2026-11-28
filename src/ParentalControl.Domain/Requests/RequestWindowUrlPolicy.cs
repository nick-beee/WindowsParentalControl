namespace ParentalControl.Domain.Requests;

/// <summary>
/// The URL filter of the Request Window (request-window, design D2). A document (the main frame or any frame) is
/// allowed only at <c>http://127.0.0.1:5050/request</c>, with or without a query. Each other resource is allowed only
/// from the origin <c>http://127.0.0.1:5050</c>. Each other URL, scheme or URL that does not parse is refused.
/// </summary>
public static class RequestWindowUrlPolicy
{
    public const string Host = "127.0.0.1";
    public const int Port = 5050;
    public const string RequestPath = "/request";

    /// <summary>
    /// The first page of the Request Window. It names no user: the service takes the user from the Request Token
    /// (request-identity-from-launch).
    /// </summary>
    public static string PageUrl => $"http://{Host}:{Port}{RequestPath}";

    /// <summary>True only for the request page of the local service.</summary>
    public static bool IsAllowedDocument(string? url) => TryParse(url, out var uri) && IsAllowedDocument(uri);

    /// <inheritdoc cref="IsAllowedDocument(string?)"/>
    public static bool IsAllowedDocument(Uri? uri) =>
        IsAllowedResource(uri) && string.Equals(uri!.AbsolutePath, RequestPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for a URL of the origin <c>http://127.0.0.1:5050</c> without user info.</summary>
    public static bool IsAllowedResource(string? url) => TryParse(url, out var uri) && IsAllowedResource(uri);

    /// <inheritdoc cref="IsAllowedResource(string?)"/>
    public static bool IsAllowedResource(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        && uri.UserInfo.Length == 0
        && string.Equals(uri.Host, Host, StringComparison.Ordinal)
        && uri.Port == Port;

    private static bool TryParse(string? url, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out uri);
    }
}
