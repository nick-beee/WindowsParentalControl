using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// A static scan of the source text of the web pages in ParentalControl.Service (issue #5, stored XSS).
/// The pages are in a Windows project, so this test reads them as text and does not run them.
/// The scan finds raw interpolation of untrusted values. It does not execute a payload in a browser.
/// Since ADR 0009 the web admin and the child request page are a React app (web/admin, see AdminSpaSourceTests).
/// The pages that stay in C# are the login page that the login route returns after a failure and the setup text.
/// </summary>
public class WebPageEscapeTests
{
    private const string PagesDir = "src/ParentalControl.Service/Web/Pages";

    // Names of values that come from the child, the Session Agent, an exe, Telegram, GitHub or the database.
    private static readonly Regex UntrustedName = new(
        @"\b(reason|username|user|currentApp|processName|windowTitle|pattern|url|releaseTitle|releaseNotes|error|message|" +
        @"timestamp|createdAt|status|dayName|scheduleStart|scheduleEnd|date|curfew|currentVersion|latestVersion|" +
        @"triggerType|baseStart|baseEnd|line)\b");

    // Last segment of a bare path (for example "s.sessionId") that is a number, a fixed value or HTML that the page builds itself.
    private static readonly HashSet<string> SafeBareNames = new(StringComparer.Ordinal)
    {
        "id", "sessionId", "userId", "requestedMinutes", "remainingMinutes", "totalAllowed", "percentage", "minutes",
        "width", "height", "customCount", "baseMinutes", "dailyMinutes", "category", "i", "heightPct", "color",
        "barColor", "tooltip", "isActive", "isChecked", "disabledAttr", "opacityStyle", "dis", "used", "attrs",
        "liveIndicator", "currentAppHtml", "actionHtml", "lockBadge", "customBadge", "formattedNotes",
    };

    // A line with an HTML tag or an HTML attribute. Only these lines can write HTML.
    private static readonly Regex HtmlLine = new(@"<[a-zA-Z/!]|[\w-]+=""");

    // The innermost ${...} of a JS template literal.
    private static readonly Regex JsHole = new(@"\$\{([^{}]*)\}");

    private static readonly Regex BarePath = new(@"^[A-Za-z_$][\w$]*(\.[A-Za-z_$][\w$]*)*$");

    [Fact]
    public void JsTemplatesPutUntrustedValuesIntoHtmlOnlyThroughEsc()
    {
        var findings = new List<string>();
        foreach (var (file, lineNo, line) in PageLines())
        {
            if (!HtmlLine.IsMatch(line)) continue;
            foreach (Match m in JsHole.Matches(line))
            {
                var expr = m.Groups[1].Value.Trim();
                if (expr.StartsWith("esc(", StringComparison.Ordinal)) continue;

                if (UntrustedName.IsMatch(expr))
                    findings.Add($"{file}:{lineNo}: untrusted value without esc(): ${{{expr}}}");
                else if (BarePath.IsMatch(expr) && !SafeBareNames.Contains(expr.Split('.')[^1]))
                    findings.Add($"{file}:{lineNo}: unknown value without esc(): ${{{expr}}}");
            }
        }
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void InlineEventHandlersDoNotPutInterpolatedValuesIntoJsStrings()
    {
        // esc() is not enough here: the HTML parser decodes &#39; back to ' before the handler runs.
        // Put the value in a data-* attribute and read it with this.dataset.
        var handlerWithString = new Regex(@"\bon\w+=""[^""]*'\$\{");
        var findings = PageLines()
            .Where(l => handlerWithString.IsMatch(l.Line))
            .Select(l => $"{l.File}:{l.LineNo}: {l.Line.Trim()}")
            .ToList();
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void ServerSideInterpolationEncodesUntrustedValues()
    {
        // C# raw strings: {name} in $"""...""", {{name}} in $$"""...""". A raw hole for an untrusted
        // name is a finding. Encoded values use another local (usernameHtml) or a call (WebUtility.HtmlEncode(...)).
        var rawHole = new Regex(@"(?<![\$\{])\{\{?(username|error|latestDeclineReason|statusLabel|latestStatus|u|curfew|defText|reason)\}\}?");
        var findings = PageLines()
            .Where(l => rawHole.IsMatch(l.Line))
            .Select(l => $"{l.File}:{l.LineNo}: {l.Line.Trim()}")
            .ToList();
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void ServerResponsesAreNotWrittenWithInnerHtml()
    {
        // A field of a fetch response (data.x, err.x, res.x) goes into innerHTML only inside esc(...).
        var innerHtmlFromResponse = new Regex(@"innerHTML\s*=.*(?<!esc\()\b(data|err|res)\.");
        var findings = PageLines()
            .Where(l => innerHtmlFromResponse.IsMatch(l.Line))
            .Select(l => $"{l.File}:{l.LineNo}: {l.Line.Trim()}")
            .ToList();
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void TheScanFindsThePages()
    {
        var files = PageLines().Select(l => l.File).Distinct().ToList();
        Assert.Contains("AdminPage.cs", files);
    }

    private static IEnumerable<(string File, int LineNo, string Line)> PageLines()
    {
        var dir = Path.Combine(RepoRoot(), PagesDir);
        foreach (var path in Directory.GetFiles(dir, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
                yield return (Path.GetFileName(path), i + 1, lines[i]);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, PagesDir)))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Cannot find {PagesDir} above {AppContext.BaseDirectory}.");
    }
}
