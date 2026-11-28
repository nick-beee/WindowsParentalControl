using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// A static scan of the Session Probe and of <c>SessionProbeHost</c> (session-probe-and-agent-channel, design D14, D15;
/// code review of task group 4):
/// <list type="bullet">
/// <item>Each test switch of the probe and each test hook of the service is inside <c>#if VM_TEST</c>, and only the
/// VM scripts build with <c>-p:VmTest=true</c>. So the release has none of them.</item>
/// <item>In the count of a sample, the probe locks come before the writes (D14).</item>
/// </list>
/// </summary>
public class ProbeSourceTests
{
    private static readonly string[] VmOnlyFiles =
    [
        "src/ParentalControl.Probe/Program.cs",
        "src/ParentalControl.Probe/Sampler.cs",
        "src/ParentalControl.Service/SessionProbeHost.cs",
    ];

    // The names of the test switches and hooks. "NoHung" is also the name of the property that is false in a release.
    private static readonly Regex TestName = new(
        @"PARENTAL_CONTROL_TEST_PROBE|TestSwitches|_switches\b|_testArgs|_testNoStart|_trace\b|TestProbe\w*Env|""(crash|hang|gap|stall|nohung)""|test switch");

    [Fact]
    public void EachTestSwitchAndHookIsInsideIfVmTest()
    {
        var findings = new List<string>();
        foreach (var file in VmOnlyFiles)
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), file));
            // A stack of the open #if blocks: true while the current branch is the VM_TEST branch.
            var stack = new Stack<bool>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#if ", StringComparison.Ordinal))
                {
                    stack.Push(line == "#if VM_TEST");
                    continue;
                }
                if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                {
                    // The #else branch of "#if VM_TEST" is the release branch.
                    stack.Pop();
                    stack.Push(false);
                    continue;
                }
                if (line.StartsWith("#endif", StringComparison.Ordinal))
                {
                    stack.Pop();
                    continue;
                }
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal)) continue;
                if (TestName.IsMatch(line) && !stack.Contains(true))
                    findings.Add($"{file}:{i + 1}: {line}");
            }
            Assert.True(stack.Count == 0, $"{file}: an #if without #endif");
        }
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void OnlyABuildWithVmTestDefinesVmTest()
    {
        foreach (var project in new[] { "src/ParentalControl.Probe/ParentalControl.Probe.csproj", "src/ParentalControl.Service/ParentalControl.Service.csproj" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), project));
            var define = Regex.Matches(text, @"<PropertyGroup(?<attrs>[^>]*)>(?<body>(?:(?!</PropertyGroup>).)*VM_TEST(?:(?!</PropertyGroup>).)*)</PropertyGroup>",
                RegexOptions.Singleline);
            Assert.Single(define);
            Assert.Contains("'$(VmTest)' == 'true'", define[0].Groups["attrs"].Value);
        }
    }

    [Fact]
    public void TheReleaseBuildsWithoutVmTest()
    {
        var build = File.ReadAllText(Path.Combine(RepoRoot(), "build.ps1"));
        Assert.DoesNotContain("VmTest=true", build);
        Assert.Equal(2, Regex.Matches(build, @"^\s+-p:VmTest=false `\r?$", RegexOptions.Multiline).Count);
        var release = File.ReadAllText(Path.Combine(RepoRoot(), ".github/workflows/release.yml"));
        Assert.DoesNotContain("VmTest", release);
    }

    [Fact]
    public void TheVmScriptsBuildWithVmTest()
    {
        Assert.Contains("-p:VmTest=true", File.ReadAllText(Path.Combine(RepoRoot(), "scripts/vm/guest/deploy.ps1")));
        Assert.Contains("-p:VmTest=true", File.ReadAllText(Path.Combine(RepoRoot(), "scripts/vm-test.sh")));
    }

    [Fact]
    public void TheProbeLocksComeBeforeTheWritesOfASample()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src/ParentalControl.Service/SessionProbeHost.cs"));
        var start = text.IndexOf("    private void Count(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        var body = text[start..end];

        int At(string what)
        {
            var index = body.IndexOf(what, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Count has no {what}");
            return index;
        }

        var lastLock = Math.Max(At("ApplyBudgetDecision("), Math.Max(
            body.IndexOf("ProbeLockKind.NeverSuspended", StringComparison.Ordinal),
            body.IndexOf("ProbeLockKind.FullSet", StringComparison.Ordinal)));
        Assert.True(At("_monitor.Sample(") < At("ApplyBudgetDecision("));
        foreach (var write in new[] { "ReportGap(", "CollectMinutes(", "_ledger.AddCategory(", "AppUsageRepository.AddSeconds(", "LiveActivities.Shared", "_logger.Information(\"Session Probe of session {SessionId}: {Kind}" })
            Assert.True(lastLock < At(write), $"{write} comes before a probe lock");
        // The usage for the locks never reads the ledger without a fallback.
        Assert.DoesNotContain("_ledger.CategoryUsage(", body);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build.ps1")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Cannot find the repository above {AppContext.BaseDirectory}.");
    }
}
