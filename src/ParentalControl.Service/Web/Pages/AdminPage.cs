using System.Net;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Service.Web.Pages;

/// <summary>
/// The pages that POST /api/admin/login returns to a form post: the login page after a failed login and the setup
/// text before the setup. The web admin itself is the single-page app of web/admin (AdminSpa, ADR 0009); it signs in
/// with a JSON request.
/// </summary>
public static class AdminPage
{
    public static string RenderLogin(string? error = null)
    {
        var errorHtml = !string.IsNullOrEmpty(error)
            ? $"""<div class="banner banner-error">❌ {WebUtility.HtmlEncode(error)}</div>"""
            : "";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Parental Control Admin — Login</title>
            <style>
                * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
                body { background: #090d16; color: #f8fafc; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 1rem; }
                .card { background: #131c2e; border: 1px solid #1e293b; border-radius: 1rem; padding: 2rem; max-width: 400px; width: 100%; box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.6); }
                h1 { font-size: 1.25rem; margin-bottom: 0.5rem; color: #38bdf8; display: flex; align-items: center; gap: 0.5rem; }
                p { font-size: 0.875rem; color: #94a3b8; margin-bottom: 1.5rem; }
                .banner { padding: 0.75rem 1rem; border-radius: 0.5rem; margin-bottom: 1.5rem; font-size: 0.875rem; }
                .banner-error { background: #7f1d1d; color: #fecaca; border: 1px solid #b91c1c; }
                label { display: block; font-size: 0.875rem; color: #cbd5e1; margin-bottom: 0.5rem; font-weight: 500; }
                input[type="text"] { width: 100%; background: #090d16; border: 2px solid #334155; border-radius: 0.5rem; padding: 0.875rem; color: #f8fafc; font-size: 1.5rem; text-align: center; letter-spacing: 0.5rem; margin-bottom: 1.5rem; }
                input[type="text"]:focus { outline: none; border-color: #38bdf8; }
                button { width: 100%; background: #0284c7; color: white; border: none; padding: 0.875rem; border-radius: 0.5rem; font-size: 1rem; font-weight: 600; cursor: pointer; transition: background 0.2s; }
                button:hover { background: #0369a1; }
            </style>
        </head>
        <body>
            <div class="card">
                <h1>🛡️ Admin Authentication</h1>
                <p>Enter the 6-digit TOTP code from your Authenticator app.</p>
                {{errorHtml}}
                <form method="POST" action="/api/admin/login">
                    <label for="code">Authenticator Code:</label>
                    <input type="text" id="code" name="code" maxlength="6" pattern="[0-9]{6}" inputmode="numeric" autocomplete="one-time-code" autofocus required placeholder="000000">
                    <button type="submit">Verify & Login</button>
                </form>
            </div>
        </body>
        </html>
        """;
    }

    /// <summary>
    /// The page before the setup of the admin sign-in: a German text with no form, no data and no admin function.
    /// </summary>
    public static string RenderSetupNeeded()
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="de">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Parental Control Admin — {{WebUtility.HtmlEncode(AdminAccessTexts.SetupTitle)}}</title>
            <style>
                * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
                body { background: #090d16; color: #f8fafc; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 1rem; }
                .card { background: #131c2e; border: 1px solid #1e293b; border-radius: 1rem; padding: 2rem; max-width: 440px; width: 100%; box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.6); }
                h1 { font-size: 1.25rem; margin-bottom: 1rem; color: #38bdf8; }
                p { font-size: 0.95rem; color: #cbd5e1; line-height: 1.5; margin-bottom: 1rem; }
                p.hint { font-size: 0.85rem; color: #94a3b8; margin-bottom: 0; }
            </style>
        </head>
        <body>
            <div class="card">
                <h1>🔒 {{WebUtility.HtmlEncode(AdminAccessTexts.SetupTitle)}}</h1>
                <p>{{WebUtility.HtmlEncode(AdminAccessTexts.SetupText)}}</p>
                <p class="hint">{{WebUtility.HtmlEncode(AdminAccessTexts.SetupHint)}}</p>
            </div>
        </body>
        </html>
        """;
    }
}
