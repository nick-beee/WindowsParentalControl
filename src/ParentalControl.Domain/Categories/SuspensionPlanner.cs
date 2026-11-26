namespace ParentalControl.Domain.Categories;

/// <summary>A process in a session, as the service sees it in one tick.</summary>
/// <param name="ExePath">The full exe path, or null if the service cannot read it.</param>
/// <param name="IsSuspended">True if all threads of the process are suspended.</param>
public sealed record SessionProcess(int Pid, DateTime StartTimeUtc, string? ExePath, int SessionId, bool IsSuspended);

/// <summary>A process that the service suspended. The pid and the start time identify the process.</summary>
public sealed record SuspensionRecord(int Pid, DateTime StartTimeUtc, int SessionId, int UserId, AppCategory Category, string ExePath);

/// <summary>The category of an exe for a user, and whether the exe has no time left today.</summary>
public sealed record ExeVerdict(AppCategory Category, bool IsExhausted);

/// <summary>A process to suspend. <see cref="IsRecorded"/> is true if a record exists (the process runs again).</summary>
public sealed record SuspendAction(SessionProcess Process, int UserId, AppCategory Category, string ExePath, bool IsRecorded);

public sealed record SuspensionPlan(
    IReadOnlyList<SuspendAction> ToSuspend,
    IReadOnlyList<SuspensionRecord> ToResume,
    IReadOnlyList<SuspensionRecord> ToDrop);

/// <summary>The input of one planning step.</summary>
/// <param name="Processes">The processes of all sessions that the service lists.</param>
/// <param name="RestrictedSessions">Session id to user id, for the sessions of Restricted Users.</param>
/// <param name="Verdict">The verdict for (user id, exe path).</param>
/// <param name="WasForeground">True if the exe path was a Foreground Window of the user today.</param>
/// <param name="Records">The current suspension records.</param>
/// <param name="AgentExePath">The full path of the Session Agent exe. It is never suspended.</param>
/// <param name="WindowsFolder">The Windows folder. An exe below it is never suspended, also if no App Rule puts it in System.</param>
public sealed record PlannerInput(
    IReadOnlyList<SessionProcess> Processes,
    IReadOnlyDictionary<int, int> RestrictedSessions,
    Func<int, string, ExeVerdict> Verdict,
    Func<int, string, bool> WasForeground,
    IReadOnlyList<SuspensionRecord> Records,
    string? AgentExePath,
    string? WindowsFolder);

/// <summary>
/// Decides which processes to suspend, which recorded processes to resume and which records to drop.
/// It never suspends System, the Session Agent, msedgewebview2.exe, an exe below the Windows folder, a process without a path,
/// a process outside the sessions of Restricted Users, or an exe that was not a Foreground Window today.
/// A recorded process stays suspended while its exe has no time left, also if the exe was not a
/// Foreground Window since the last service start.
/// </summary>
public static class SuspensionPlanner
{
    public const string WebView2FileName = "msedgewebview2.exe";

    public static SuspensionPlan Plan(PlannerInput input)
    {
        var agentPath = WindowsPath.Canonicalize(input.AgentExePath);
        var windowsFolder = WindowsPath.CanonicalizeFolder(input.WindowsFolder);
        var toSuspend = new List<SuspendAction>();
        var toResume = new List<SuspensionRecord>();
        var toDrop = new List<SuspensionRecord>();

        var byPid = new Dictionary<int, SessionProcess>();
        foreach (var p in input.Processes)
            byPid.TryAdd(p.Pid, p);

        var recordedPids = new HashSet<int>();
        foreach (var record in input.Records)
        {
            // The pid alone does not identify a process: Windows reuses pids.
            if (!byPid.TryGetValue(record.Pid, out var process) || process.StartTimeUtc != record.StartTimeUtc)
            {
                toDrop.Add(record);
                continue;
            }

            recordedPids.Add(record.Pid);
            var path = WindowsPath.Canonicalize(process.ExePath) ?? WindowsPath.Canonicalize(record.ExePath);

            ExeVerdict? verdict = null;
            if (path is not null
                && input.RestrictedSessions.TryGetValue(process.SessionId, out var userId)
                && userId == record.UserId
                && !IsExcluded(path, agentPath, windowsFolder))
            {
                verdict = input.Verdict(userId, path);
            }

            if (verdict is not { IsExhausted: true } || verdict.Category == AppCategory.System)
                toResume.Add(record);
            else if (!process.IsSuspended)
                toSuspend.Add(new SuspendAction(process, record.UserId, verdict.Category, path!, IsRecorded: true));
        }

        foreach (var process in input.Processes)
        {
            if (recordedPids.Contains(process.Pid))
                continue;
            if (!input.RestrictedSessions.TryGetValue(process.SessionId, out var userId))
                continue;
            var path = WindowsPath.Canonicalize(process.ExePath);
            if (path is null || IsExcluded(path, agentPath, windowsFolder))
                continue;

            var verdict = input.Verdict(userId, path);
            if (verdict.Category == AppCategory.System || !verdict.IsExhausted)
                continue;
            if (!input.WasForeground(userId, path))
                continue;

            toSuspend.Add(new SuspendAction(process, userId, verdict.Category, path, IsRecorded: false));
        }

        return new SuspensionPlan(toSuspend, toResume, toDrop);
    }

    // The Windows folder is a hard exclusion: if the parent deletes its default System rule,
    // explorer.exe and the shell must still keep running.
    private static bool IsExcluded(string canonicalPath, string? canonicalAgentPath, string? canonicalWindowsFolder) =>
        string.Equals(WindowsPath.GetFileName(canonicalPath), WebView2FileName, StringComparison.OrdinalIgnoreCase)
        || (canonicalAgentPath is not null && string.Equals(canonicalPath, canonicalAgentPath, StringComparison.OrdinalIgnoreCase))
        || (canonicalWindowsFolder is not null && WindowsPath.IsBelow(canonicalPath, canonicalWindowsFolder));
}
