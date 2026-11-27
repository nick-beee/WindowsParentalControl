using System.Text.RegularExpressions;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// A static scan of the web admin and the child request page (web/admin, ADR 0009). React escapes text, so the XSS
/// bug class of #5 comes back only through code that writes raw HTML or runs a string as code. The ESLint rules of
/// web/admin/eslint.config.js refuse the same patterns; this test also runs without Node.
/// </summary>
public class AdminSpaSourceTests
{
    private const string SpaDir = "web/admin";

    private static readonly (string Name, Regex Pattern)[] RawHtml =
    {
        ("dangerouslySetInnerHTML", new Regex(@"\bdangerouslySetInnerHTML\b")),
        ("innerHTML", new Regex(@"\.innerHTML\b")),
        ("outerHTML", new Regex(@"\.outerHTML\b")),
        ("insertAdjacentHTML", new Regex(@"\binsertAdjacentHTML\b")),
        ("document.write", new Regex(@"\bdocument\.write(ln)?\s*\(")),
        ("eval", new Regex(@"(?<![\w.])eval\s*\(")),
        ("new Function", new Regex(@"\bnew\s+Function\s*\(")),
        ("javascript: URL", new Regex(@"(?i)(href|src)\s*=\s*\{?\s*['""`]javascript:")),
    };

    [Fact]
    public void SourcesDoNotWriteRawHtml()
    {
        var findings = new List<string>();
        foreach (var (file, lineNo, line) in SourceLines(includeTests: false))
        {
            foreach (var (name, pattern) in RawHtml)
            {
                if (pattern.IsMatch(line))
                    findings.Add($"{file}:{lineNo}: {name}: {line.Trim()}");
            }
        }
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void TheScanFindsTheSources()
    {
        var files = SourceLines(includeTests: false).Select(l => l.File).Distinct().ToList();
        Assert.Contains("src/api/client.ts", files);
        Assert.Contains("src/views/AdminApp.tsx", files);
        Assert.Contains("src/request/RequestApp.tsx", files);
    }

    /// <summary>
    /// The child request page has no texts of its own: the service sends the texts of the Language Preset
    /// (RequestPageModelTests). So the page must not use the German texts or the code of the web admin.
    /// </summary>
    [Fact]
    public void TheRequestPageDoesNotUseTheTextsOfTheAdmin()
    {
        var findings = SourceLines(includeTests: false)
            .Where(l => l.File.StartsWith("src/request/", StringComparison.Ordinal))
            .Where(l => Regex.IsMatch(l.Line, @"from\s+'(\.\./)+(i18n|views|dialogs|api)\b|styles\.css"))
            .Select(l => $"{l.File}:{l.LineNo}: {l.Line.Trim()}")
            .ToList();
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void LintRefusesRawHtml()
    {
        var config = File.ReadAllText(Path.Combine(RepoRoot(), SpaDir, "eslint.config.js"));
        Assert.Contains("dangerouslySetInnerHTML", config);
        Assert.Contains("innerHTML|outerHTML|insertAdjacentHTML", config);
        Assert.Contains("'no-eval': 'error'", config);
    }

    [Fact]
    public void ThePreviewUsesTheCspOfTheService()
    {
        // The CSP of AdminSpa.cs and the one of `vite preview` must be the same, so a check with the preview is valid.
        var root = RepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "src/ParentalControl.Service/Web/AdminSpa.cs"));
        var vite = File.ReadAllText(Path.Combine(root, SpaDir, "vite.config.ts"));
        var serviceCsp = JoinStrings(Regex.Match(service, @"ContentSecurityPolicy\s*=\s*(?<parts>(""[^""]*""\s*\+?\s*)+);").Groups["parts"].Value, '"');
        var viteCsp = JoinStrings(Regex.Match(vite, @"contentSecurityPolicy\s*=\s*(?<parts>(""[^""]*""\s*\+?\s*)+)\n").Groups["parts"].Value, '"');

        Assert.Equal("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
                     "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'", serviceCsp);
        Assert.Equal(serviceCsp, viteCsp);
    }

    [Theory]
    [InlineData("index.html", new[] { "__PC_ADMIN_VIEW__", "__PC_VERSION__" })]
    [InlineData("request.html", new[] { "__PC_LANG__", "__PC_TITLE__", "__PC_REQUEST_DATA__" })]
    public void ThePageHasNoInlineScriptOrStyle(string page, string[] placeholders)
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), SpaDir, page));
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>"), html);
        Assert.DoesNotMatch(new Regex(@"<style\b|\sstyle=""|\son\w+="""), html);
        // Each placeholder once: the service replaces it, and a copy in a comment would get the value too.
        foreach (var placeholder in placeholders)
            Assert.Single(Regex.Matches(html, Regex.Escape(placeholder)));
    }

    /// <summary>
    /// The setup and refused views show their texts before any request, so de.ts has copies of the sign-in texts of
    /// AdminAccessTexts. Each copy must equal its constant. The other sign-in texts come from the service at run time.
    /// </summary>
    [Theory]
    [InlineData("setupTitle", AdminAccessTexts.SetupTitle)]
    [InlineData("setupText", AdminAccessTexts.SetupText)]
    [InlineData("setupHint", AdminAccessTexts.SetupHint)]
    [InlineData("loginInvalid", AdminAccessTexts.InvalidCode)]
    [InlineData("loginRefused", AdminAccessTexts.LocalClientRefused)]
    public void TheSignInTextsAreCopiesOfAdminAccessTexts(string key, string expected)
    {
        var de = File.ReadAllText(Path.Combine(RepoRoot(), SpaDir, "src/i18n/de.ts"));
        var match = Regex.Match(de, $@"\b{key}:\s*'(?<text>[^']*)'");
        Assert.True(match.Success, $"de.ts has no text {key}");
        Assert.Equal(expected, match.Groups["text"].Value);
    }

    private static string JoinStrings(string parts, char quote) =>
        string.Concat(Regex.Matches(parts, $"{quote}([^{quote}]*){quote}").Select(m => m.Groups[1].Value));

    private static IEnumerable<(string File, int LineNo, string Line)> SourceLines(bool includeTests)
    {
        var root = Path.Combine(RepoRoot(), SpaDir);
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".ts", StringComparison.Ordinal) || p.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(p => includeTests || !Regex.IsMatch(p, @"\.test\.tsx?$|[\\/]test[\\/]"))
            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
                yield return (relative, i + 1, lines[i]);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SpaDir, "package.json")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Cannot find {SpaDir} above {AppContext.BaseDirectory}.");
    }
}
