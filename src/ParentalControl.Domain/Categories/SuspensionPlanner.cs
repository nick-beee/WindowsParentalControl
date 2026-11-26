namespace ParentalControl.Domain.Categories;

/// <summary>A process in a session, as the service sees it in one tick.</summary>
/// <param name="ExePath">The full exe path, or null if the service cannot read it.</param>
/// <param name="IsSuspended">True if all threads of the process are suspended.</param>
public sealed record SessionProcess(int Pid, DateTime StartTimeUtc, string? ExePath, int SessionId, bool IsSuspended);

/// <summary>Why the service suspended a process.</summary>
public enum SuspensionReason
{
    /// <summary>The exe has no time left under its Category Limit or App Limit.</summary>
    Limit,

    /// <summary>The exe is a Blocked Program, and the process did not start after the block took effect.</summary>
    Blocked,
}

/// <summary>A process that the service suspended. The pid and the start time identify the process.</summary>
public sealed record SuspensionRecord(int Pid, DateTime StartTimeUtc, int SessionId, int UserId, AppCategory Category, string ExePath,
    SuspensionReason Reason = SuspensionReason.Limit);

/// <summary>
/// The category of an exe for a user, whether the exe has no time left today, and whether it is a Blocked Program
/// (the caller uses <see cref="BlockedStartDecider.IsBlockedProgram"/>).
/// </summary>
public sealed record ExeVerdict(AppCategory Category, bool IsExhausted, bool IsBlocked = false);

/// <summary>A process to suspend. <see cref="IsRecorded"/> is true if a record exists (the process runs again).</summary>
public sealed record SuspendAction(SessionProcess Process, int UserId, AppCategory Category, string ExePath, bool IsRecorded,
    SuspensionReason Reason = SuspensionReason.Limit);

/// <param name="ToRelabel">Records that stay, with a new reason. The caller stores the new reason.</param>
public sealed record SuspensionPlan(
    IReadOnlyList<SuspendAction> ToSuspend,
    IReadOnlyList<SuspensionRecord> ToResume,
    IReadOnlyList<SuspensionRecord> ToDrop,
    IReadOnlyList<SuspensionRecord> ToRelabel);

/// <summary>The input of one planning step.</summary>
/// <param name="Processes">The processes of all sessions that the service lists.</param>
/// <param name="RestrictedSessions">Session id to user id, for the sessions of Restricted Users.</param>
/// <param name="Verdict">The verdict for (user id, exe path).</param>
/// <param name="WasForeground">True if the exe path was a Foreground Window of the user today.</param>
/// <param name="Records">The current suspension records.</param>
/// <param name="AgentExePath">The full path of the Session Agent exe. It is never suspended.</param>
/// <param name="WindowsFolder">The Windows folder. An exe below it is never suspended, also if no App Rule puts it in System.</param>
/// <param name="HeldSessions">The sessions with a Session End Hold. No process of such a session is suspended; a resume still happens.</param>
/// <param name="PendingClose">True for a process that the block enforcer closes now (a Blocked Start). The planner skips it.</param>
/// <param name="ProtectedPrograms">The programs that a block never suspends. The block verdict of an exe below one of them is ignored.</param>
public sealed record PlannerInput(
    IReadOnlyList<SessionProcess> Processes,
    IReadOnlyDictionary<int, int> RestrictedSessions,
    Func<int, string, ExeVerdict> Verdict,
    Func<int, string, bool> WasForeground,
    IReadOnlyList<SuspensionRecord> Records,
    string? AgentExePath,
    string? WindowsFolder,
    IReadOnlySet<int>? HeldSessions = null,
    Func<SessionProcess, bool>? PendingClose = null,
    ProtectedPrograms? ProtectedPrograms = null);

/// <summary>
/// Decides which processes to suspend, which recorded processes to resume and which records to drop.
/// It never suspends System, the Session Agent, msedgewebview2.exe, an exe below the Windows folder, a process without a path,
/// a process outside the sessions of Restricted Users, or an exe that was not a Foreground Window today.
/// A recorded process stays suspended while its exe has no time left, also if the exe was not a
/// Foreground Window since the last service start.
/// During a Session End Hold of a session, it suspends no process of that session, and the records of the session stay.
/// A process of a Blocked Program is suspended with the reason Blocked, also without a Foreground Window, and
/// stays suspended while the block applies, also if its exe has time left. When the block ends, its record
/// changes to Limit if the exe has no time left and was a Foreground Window today; else the process resumes.
/// </summary>
public static class SuspensionPlanner
{
    public const string WebView2FileName = ProtectedPrograms.WebView2FileName;

    public static SuspensionPlan Plan(PlannerInput input)
    {
        var agentPath = WindowsPath.Canonicalize(input.AgentExePath);
        var windowsFolder = WindowsPath.CanonicalizeFolder(input.WindowsFolder);
        var toSuspend = new List<SuspendAction>();
        var toResume = new List<SuspensionRecord>();
        var toDrop = new List<SuspensionRecord>();
        var held = input.HeldSessions ?? new HashSet<int>();
        var toRelabel = new List<SuspensionRecord>();

        bool BlockApplies(ExeVerdict verdict, string canonicalPath) =>
            verdict.IsBlocked && verdict.Category != AppCategory.System
            && !(input.ProtectedPrograms?.IsProtected(canonicalPath) ?? false);

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

            var reason = verdict is null ? (SuspensionReason?)null
                : BlockApplies(verdict, path!) ? SuspensionReason.Blocked
                : verdict.IsExhausted && verdict.Category != AppCategory.System
                  && (record.Reason == SuspensionReason.Limit || input.WasForeground(record.UserId, path!)) ? SuspensionReason.Limit
                : null;

            if (reason is not { } keep)
            {
                toResume.Add(record);
                continue;
            }

            var current = record with { Category = verdict!.Category };
            if (record.Reason != keep)
            {
                current = current with { Reason = keep };
                toRelabel.Add(current);
            }
            if (!process.IsSuspended && !held.Contains(process.SessionId))
                toSuspend.Add(new SuspendAction(process, record.UserId, verdict.Category, path!, IsRecorded: true, keep));
        }

        foreach (var process in input.Processes)
        {
            if (recordedPids.Contains(process.Pid))
                continue;
            if (!input.RestrictedSessions.TryGetValue(process.SessionId, out var userId) || held.Contains(process.SessionId))
                continue;
            var path = WindowsPath.Canonicalize(process.ExePath);
            if (path is null || IsExcluded(path, agentPath, windowsFolder))
                continue;

            var verdict = input.Verdict(userId, path);
            if (BlockApplies(verdict, path))
            {
                // A Blocked Start that the block enforcer closes now: do not suspend it in the same second.
                if (input.PendingClose?.Invoke(process) == true)
                    continue;
                toSuspend.Add(new SuspendAction(process, userId, verdict.Category, path, IsRecorded: false, SuspensionReason.Blocked));
                continue;
            }

            if (verdict.Category == AppCategory.System || !verdict.IsExhausted)
                continue;
            if (!input.WasForeground(userId, path))
                continue;

            toSuspend.Add(new SuspendAction(process, userId, verdict.Category, path, IsRecorded: false));
        }

        return new SuspensionPlan(toSuspend, toResume, toDrop, toRelabel);
    }

    // The Windows folder is a hard exclusion: if the parent deletes its default System rule,
    // explorer.exe and the shell must still keep running.
    private static bool IsExcluded(string canonicalPath, string? canonicalAgentPath, string? canonicalWindowsFolder) =>
        string.Equals(WindowsPath.GetFileName(canonicalPath), WebView2FileName, StringComparison.OrdinalIgnoreCase)
        || (canonicalAgentPath is not null && string.Equals(canonicalPath, canonicalAgentPath, StringComparison.OrdinalIgnoreCase))
        || (canonicalWindowsFolder is not null && WindowsPath.IsBelow(canonicalPath, canonicalWindowsFolder));
}
