using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class LaunchEnvironmentTests
{
    private static readonly string[] Entries =
    [
        "=C:=C:\\Windows", "PATH=C:\\Windows", "DOTNET_STARTUP_HOOKS=C:\\Users\\Kid\\hook.dll", "dotnet_root=C:\\x",
        "COMPlus_EnableDiagnostics=1", "CORECLR_PROFILER={1}", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222",
        "USERNAME=Kid", "DOTNETX=keep",
    ];

    [Fact]
    public void Agent_environment_has_no_runtime_variables()
    {
        var result = LaunchEnvironment.Build(Entries, forProbe: false);
        Assert.Equal(["=C:=C:\\Windows", "PATH=C:\\Windows", "USERNAME=Kid", "DOTNETX=keep"], result);
    }

    [Fact]
    public void Probe_environment_adds_the_two_settings()
    {
        var result = LaunchEnvironment.Build(Entries, forProbe: true);
        Assert.Contains("DOTNET_EnableDiagnostics=0", result);
        Assert.Contains("DOTNET_DISABLE_GUI_ERRORS=1", result);
        Assert.DoesNotContain(result, e => e.StartsWith("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, e => e.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Block_ends_with_two_nul_characters()
    {
        Assert.Equal("A=1\0B=2\0\0", LaunchEnvironment.ToBlock(["A=1", "B=2"]));
        Assert.Equal("\0\0", LaunchEnvironment.ToBlock([]));
    }

    // Review nit: the Session Probe falls back to the environment of the service if CreateEnvironmentBlock fails.
    [Fact]
    public void Probe_uses_the_token_block_if_it_exists()
    {
        var service = new System.Collections.Hashtable { ["PATH"] = @"C:\Service" };
        var entries = LaunchEnvironment.BuildForProbe([@"Path=C:\Token", "DOTNET_STARTUP_HOOKS=x"], service);
        Assert.Equal([@"Path=C:\Token", .. LaunchEnvironment.ProbeSettings], entries);
    }

    [Fact]
    public void Probe_without_a_token_block_gets_the_sorted_service_environment_without_removed_variables()
    {
        var service = new System.Collections.Hashtable
        {
            ["windir"] = @"C:\Windows",
            ["ComSpec"] = @"C:\Windows\system32\cmd.exe",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\evil.dll",
            ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--x",
            ["Path"] = @"C:\Windows\system32",
        };
        var entries = LaunchEnvironment.BuildForProbe(null, service);
        Assert.Equal(
            [@"ComSpec=C:\Windows\system32\cmd.exe", @"Path=C:\Windows\system32", @"windir=C:\Windows", .. LaunchEnvironment.ProbeSettings],
            entries);
    }
}
