using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Sessions;

/// <summary>
/// Finds the Session Agent of a session. The service does not trust the name of a process or a claim of the agent:
/// a process is the agent only if it runs in that session from the agent exe that the service starts.
/// </summary>
public static class SessionAgentMatch
{
    /// <summary>
    /// The process name that Windows gives the agent exe (the file name without ".exe"),
    /// for <c>Process.GetProcessesByName</c>. Only the ".exe" part goes: "ParentalControl.Agent.exe" gives
    /// "ParentalControl.Agent", not "ParentalControl".
    /// </summary>
    public static string ProcessName(string exePath)
    {
        var name = WindowsPath.GetFileName(exePath);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// True if the process runs in <paramref name="sessionId"/> from <paramref name="agentExePath"/>.
    /// The paths are compared in canonical form and without letter case. A process without a readable path is not the agent.
    /// </summary>
    public static bool IsSessionAgent(int processSessionId, string? processImagePath, int sessionId, string agentExePath)
    {
        if (processSessionId != sessionId)
            return false;

        var actual = WindowsPath.Canonicalize(processImagePath);
        var expected = WindowsPath.Canonicalize(agentExePath);
        return actual is not null && expected is not null &&
               string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
