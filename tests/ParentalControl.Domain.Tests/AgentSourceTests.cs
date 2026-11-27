using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// A static scan of the Session Agent (request-window). The agent must never start a browser: a <c>Process.Start</c>
/// or a <c>ProcessStartInfo</c> with an http URL or with <c>UseShellExecute</c> for a URL would open the default
/// browser, which the block list closes (issue #52). The Request Window must give its web view no URL before the
/// lockdown (design D6a), so its XAML has no <c>Source</c>.
/// </summary>
public class AgentSourceTests
{
    private const string AgentDir = "src/ParentalControl.Agent";

    private static readonly Regex ProcessStart = new(@"\bProcess\.Start\s*\(|\bnew\s+ProcessStartInfo\b|\bProcessStartInfo\s*\{");
    private static readonly Regex Url = new(@"(?i)https?:|localhost|127\.0\.0\.1|\bUseShellExecute\s*=\s*true\b");

    [Fact]
    public void TheAgentStartsNoBrowser()
    {
        var findings = new List<string>();
        foreach (var path in AgentFiles("*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (Match match in ProcessStart.Matches(text))
            {
                // The statement of the start: up to the next ';' after the match.
                var end = text.IndexOf(';', match.Index);
                var statement = end < 0 ? text[match.Index..] : text[match.Index..end];
                if (Url.IsMatch(statement))
                    findings.Add($"{Relative(path)}:{LineOf(text, match.Index)}: {statement.Trim()}");
            }
            foreach (Match match in Regex.Matches(text, @"\bUseShellExecute\s*=\s*true\b"))
                findings.Add($"{Relative(path)}:{LineOf(text, match.Index)}: UseShellExecute = true");
        }
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings.Distinct()));
    }

    [Fact]
    public void TheRequestWindowXamlGivesTheWebViewNoSource()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), AgentDir, "RequestWindow.xaml"));
        var elements = Regex.Matches(xaml, @"<\s*wv2:WebView2\b[^>]*>", RegexOptions.Singleline);
        Assert.Single(elements);
        Assert.DoesNotMatch(new Regex(@"\bSource\s*="), elements[0].Value);
        Assert.DoesNotMatch(new Regex(@"WebView2\.Source\b|<\s*wv2:WebView2\.CreationProperties"), xaml);
    }

    private static IEnumerable<string> AgentFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), AgentDir), pattern, SearchOption.AllDirectories)
            .Where(p => !Regex.IsMatch(p, @"[\\/](obj|bin)[\\/]"))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, AgentDir)))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Cannot find {AgentDir} above {AppContext.BaseDirectory}.");
    }
}
