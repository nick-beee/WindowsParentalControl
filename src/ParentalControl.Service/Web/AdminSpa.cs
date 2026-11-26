using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using ParentalControl.Core;

namespace ParentalControl.Service.Web;

/// <summary>
/// Serves the web admin, a React single-page app (ADR 0009, web/admin). The build of the service embeds the
/// Vite output as resources with the prefix "AdminSpa/" (ParentalControl.Service.csproj). The service serves
/// only these files: the page at /admin, the child request page at /request (RequestSpa) and the hashed files at
/// /admin/assets/.
/// </summary>
public static class AdminSpa
{
    /// <summary>The views that the page can show. The service selects one per request (meta tag pc-admin-view).</summary>
    public const string ViewSetup = "setup";
    public const string ViewLogin = "login";
    public const string ViewRefused = "refused";
    public const string ViewApp = "app";

    /// <summary>The policy of issue #25: no inline script or style, no frames, no other origin.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    private const string ResourcePrefix = "AdminSpa/";
    private const string ViewPlaceholder = "__PC_ADMIN_VIEW__";
    private const string VersionPlaceholder = "__PC_VERSION__";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly Lazy<FrozenDictionary<string, byte[]>> Files = new(LoadFiles);

    /// <summary>The page for one of the views, with the CSP. 503 if the build did not embed the page.</summary>
    /// <param name="statusCode">For example 403 for the refused view of require-admin-sign-in (#21).</param>
    public static IResult Page(HttpContext ctx, string view, int statusCode = StatusCodes.Status200OK)
    {
        if (view is not (ViewSetup or ViewLogin or ViewRefused or ViewApp))
            throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown view of the admin page.");
        if (!Files.Value.TryGetValue("index.html", out var template))
            return Results.Text("The web admin is not part of this build (web/admin/dist is missing).", "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        SetSecurityHeaders(ctx.Response);
        // The page depends on the session: no cache, so a logout or a login always gets a fresh view.
        ctx.Response.Headers.CacheControl = "no-store";
        // The two values are fixed strings of this service, not request data.
        var html = Encoding.UTF8.GetString(template)
            .Replace(ViewPlaceholder, view, StringComparison.Ordinal)
            .Replace(VersionPlaceholder, System.Net.WebUtility.HtmlEncode(AppVersion.DisplayName), StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    /// <summary>A file of /admin/assets/. Only an exact name of the build matches; there is no file system path.</summary>
    public static IResult Asset(HttpContext ctx, string file)
    {
        if (!Files.Value.TryGetValue("assets/" + file, out var bytes))
            return Results.NotFound();

        SetSecurityHeaders(ctx.Response);
        // Vite puts a content hash into each asset name, so a file never changes under its name.
        ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.Bytes(bytes, ContentTypeOf(file), entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(ETagOf(bytes)));
    }

    public static string ContentTypeOf(string file) =>
        !ContentTypes.TryGetContentType(file, out var type) ? "application/octet-stream"
        : type.StartsWith("text/", StringComparison.Ordinal) ? type + "; charset=utf-8"
        : type;

    /// <summary>The names of the embedded files (for example "index.html", "assets/index-abc.js").</summary>
    public static IReadOnlyCollection<string> FileNames => Files.Value.Keys;

    /// <summary>An embedded file of the build, for example "request.html" (RequestSpa).</summary>
    internal static bool TryGetFile(string name, out byte[] bytes) => Files.Value.TryGetValue(name, out bytes!);

    internal static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static string ETagOf(byte[] bytes) => "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";

    private static FrozenDictionary<string, byte[]> LoadFiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // On Windows, %(RecursiveDir) in the resource name has a backslash.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[normalized[ResourcePrefix.Length..]] = copy.ToArray();
        }
        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
