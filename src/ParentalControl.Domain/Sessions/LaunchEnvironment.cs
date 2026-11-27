namespace ParentalControl.Domain.Sessions;

/// <summary>
/// The environment of a process that the service starts in a session (session-probe-and-agent-channel, design D2 step
/// 4, D5 and D7). The child can set user variables in <c>HKCU\Environment</c>, for example
/// <c>DOTNET_STARTUP_HOOKS</c> or <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c>. The service removes each variable
/// that starts with <c>DOTNET_</c>, <c>COMPlus_</c>, <c>CORECLR_</c> or <c>WEBVIEW2_</c>. For the Session Probe it then
/// adds <c>DOTNET_EnableDiagnostics=0</c> and <c>DOTNET_DISABLE_GUI_ERRORS=1</c>.
/// </summary>
public static class LaunchEnvironment
{
    public static IReadOnlyList<string> RemovedPrefixes { get; } = ["DOTNET_", "COMPlus_", "CORECLR_", "WEBVIEW2_"];

    public static IReadOnlyList<string> ProbeSettings { get; } = ["DOTNET_EnableDiagnostics=0", "DOTNET_DISABLE_GUI_ERRORS=1"];

    /// <summary>True for an entry <c>NAME=value</c> that the service removes. The test ignores letter case, as Windows
    /// does for variable names.</summary>
    public static bool IsRemoved(string entry) =>
        RemovedPrefixes.Any(prefix => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries of the new environment: <paramref name="entries"/> without the removed variables, and for the
    /// Session Probe with <see cref="ProbeSettings"/>. Windows keeps entries that start with "=" (the current folder of
    /// a drive); they stay.
    /// </summary>
    public static IReadOnlyList<string> Build(IEnumerable<string> entries, bool forProbe)
    {
        var result = entries.Where(e => !string.IsNullOrEmpty(e) && !IsRemoved(e)).ToList();
        if (forProbe) result.AddRange(ProbeSettings);
        return result;
    }

    /// <summary>The Unicode environment block for <c>CreateProcessAsUserW</c>: each entry ends with a NUL, and one
    /// more NUL ends the block.</summary>
    public static string ToBlock(IReadOnlyList<string> entries) =>
        entries.Count == 0 ? "\0\0" : string.Join("\0", entries) + "\0\0";
}
