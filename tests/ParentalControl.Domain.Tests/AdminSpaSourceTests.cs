using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// A static scan of the web admin (web/admin, ADR 0009). React escapes text, so the XSS bug class of #5
/// comes back only through code that writes raw HTML or runs a string as code. The ESLint rules of
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

    [Fact]
    public void ThePageHasNoInlineScriptOrStyle()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), SpaDir, "index.html"));
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>"), html);
        Assert.DoesNotMatch(new Regex(@"<style\b|\sstyle=""|\son\w+="""), html);
        Assert.Contains("__PC_ADMIN_VIEW__", html);
        Assert.Contains("__PC_VERSION__", html);
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
