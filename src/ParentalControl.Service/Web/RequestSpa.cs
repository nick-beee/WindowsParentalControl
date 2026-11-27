using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using ParentalControl.Domain.Web;

namespace ParentalControl.Service.Web;

/// <summary>
/// Serves the child request page (/request), the second entry of the React app in web/admin (request.html, ADR 0009).
/// The page gets its data and all of its texts from the service: the JSON of <see cref="RequestPageModel"/> goes into
/// the meta tag pc-request-data. The page loads its files from /admin/assets/ (AdminSpa.Asset), with the same CSP.
/// </summary>
public static class RequestSpa
{
    private const string PageFile = "request.html";
    private const string LangPlaceholder = "__PC_LANG__";
    private const string TitlePlaceholder = "__PC_TITLE__";
    private const string DataPlaceholder = "__PC_REQUEST_DATA__";

    /// <summary>The page with the data of one request, with the CSP. 503 if the build did not embed the page.</summary>
    public static IResult Page(HttpContext ctx, RequestPageModel model)
    {
        if (!AdminSpa.TryGetFile(PageFile, out var template))
            return Results.Text("The request page is not part of this build (web/admin/dist is missing).", "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        AdminSpa.SetSecurityHeaders(ctx.Response);
        // The page shows the state at the time of the request (remaining time, last request).
        ctx.Response.Headers.CacheControl = "no-store";
        // The data is the last value to go in, so a text in the data (for example a decline reason with
        // "__PC_LANG__") is not replaced again. HtmlEncode makes each value safe inside the attribute.
        var html = Encoding.UTF8.GetString(template)
            .Replace(LangPlaceholder, WebUtility.HtmlEncode(model.Lang), StringComparison.Ordinal)
            .Replace(TitlePlaceholder, WebUtility.HtmlEncode(model.Texts.GetValueOrDefault("page_title", "")), StringComparison.Ordinal)
            .Replace(DataPlaceholder, WebUtility.HtmlEncode(model.ToJson()), StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
