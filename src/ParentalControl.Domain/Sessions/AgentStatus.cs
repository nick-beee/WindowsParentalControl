namespace ParentalControl.Domain.Sessions;

// The wire names of the status answer are the JSON names that the Session Agent reads (AgentStatusResponse in the
// agent), so the parameters are in lower case.
#pragma warning disable IDE1006

/// <summary>A process in <see cref="AgentStatus.suspendedApps"/> or <see cref="AgentStatus.pendingApps"/>.</summary>
public sealed record AgentStatusApp(int pid, DateTime startTimeUtc);

/// <summary>The active Interactive Session Prompt in the status answer.</summary>
public sealed record AgentStatusPrompt(int id, string message, string urgency, int targetDisplay, int timeoutSeconds,
    IReadOnlyList<string> options);

/// <summary>
/// The status answer of the service on the Agent Channel (session-probe-and-agent-channel, design D8). Build it only
/// through <see cref="AgentStatusLimits"/>, so that the answer frame never has more than
/// <see cref="AgentChannelCodec.MaxJsonFrameBytes"/> bytes. <see cref="suspendedAppsTotal"/> and
/// <see cref="pendingAppsTotal"/> give the number before the cap.
/// </summary>
public sealed record AgentStatus(
    string? username,
    bool isRestricted,
    int remainingSeconds,
    string? curfew,
    bool isLocked,
    bool isCurfewClamped,
    int? lastDeclinedId,
    string? lastDeclinedReason,
    DateTime? lastDeclinedTime,
    bool captureRequested,
    int watchIntervalSeconds,
    string? language,
    AgentStatusPrompt? activePrompt,
    IReadOnlyList<AgentStatusApp>? suspendedApps,
    int suspendedAppsTotal,
    IReadOnlyList<AgentStatusApp>? pendingApps,
    int pendingAppsTotal);

#pragma warning restore IDE1006

/// <summary>
/// The caps of the status answer (review of task groups 5 and 6, item 1). With about 1000 processes in a list, the
/// answer was over the 64 KB of a JSON frame, the service could not send it, and the agent got no status. Each list
/// keeps at most <see cref="MaxApps"/> entries, and each text has a cap. A test builds the largest answer and checks
/// that its frame fits.
/// </summary>
public static class AgentStatusLimits
{
    public const int MaxApps = 200;
    public const int MaxPromptChars = 2000;
    public const int MaxOptions = 10;
    public const int MaxOptionChars = 100;
    public const int MaxReasonChars = 1000;
    /// <summary>The user name, the curfew text, the urgency and the language.</summary>
    public const int MaxShortChars = 256;

    /// <summary>The first <see cref="MaxApps"/> processes, and the number of all of them.</summary>
    public static (IReadOnlyList<AgentStatusApp> Apps, int Total) Apps(IEnumerable<(int Pid, DateTime StartUtc)> processes)
    {
        var apps = new List<AgentStatusApp>();
        var total = 0;
        foreach (var (pid, start) in processes)
        {
            if (total++ < MaxApps)
                apps.Add(new AgentStatusApp(pid, DateTime.SpecifyKind(start, DateTimeKind.Utc)));
        }
        return (apps, total);
    }

    public static AgentStatusPrompt Prompt(int id, string? message, string? urgency, int targetDisplay, int timeoutSeconds,
        IEnumerable<string> options) =>
        new(id, Text(message, MaxPromptChars) ?? "", Text(urgency, MaxShortChars) ?? "", targetDisplay, timeoutSeconds,
            options.Take(MaxOptions).Select(o => Text(o, MaxOptionChars) ?? "").ToList());

    /// <summary>The text cut to <paramref name="maxChars"/> characters, without a split surrogate pair.</summary>
    public static string? Text(string? value, int maxChars)
    {
        if (value is null || value.Length <= maxChars) return value;
        var length = maxChars;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
