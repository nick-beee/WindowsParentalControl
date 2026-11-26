namespace ParentalControl.Domain.Categories;

/// <summary>What the service does with a process because of a block.</summary>
public enum BlockDecision
{
    /// <summary>The block does not apply to the process.</summary>
    Ignore,

    /// <summary>A Blocked Start: the service closes the process. This is the only close of a program of the child.</summary>
    Close,

    /// <summary>A Blocked Program that is not at its start: the service suspends it and never closes it.</summary>
    Suspend,
}

/// <summary>The parent process of a process, as the service reads it. Null fields are not readable.</summary>
/// <param name="Pid">The pid of the parent.</param>
/// <param name="StartTimeUtc">The creation time of the live process with this pid.</param>
/// <param name="IsBlockedProgram">True if the exe of the live process with this pid is a Blocked Program.</param>
public sealed record BlockParent(int Pid, DateTime StartTimeUtc, bool IsBlockedProgram);

/// <summary>The input of one block decision.</summary>
/// <param name="Resolution">The resolution of the exe path with the current App Rules.</param>
/// <param name="IsRestrictedSession">True if the process runs in a session of a Restricted User.</param>
/// <param name="CreatedUtc">The creation time of the process, from Windows.</param>
/// <param name="DetectedUtc">The time of the decision. A late decision must give Suspend, never Close.</param>
/// <param name="BlockEpochUtc">The later of the start of the block enforcer and the last change of the App Rules.</param>
/// <param name="Parent">The live parent process, or null if it ended or is not readable.</param>
public sealed record BlockedStartInput(
    CategoryResolution Resolution,
    bool IsRestrictedSession,
    DateTime CreatedUtc,
    DateTime DetectedUtc,
    DateTime BlockEpochUtc,
    BlockParent? Parent);

/// <summary>
/// Decides whether a process is a Blocked Start (Close), a Blocked Program that already runs (Suspend), or not
/// blocked (Ignore). The input has no command line: data of the process can never change Close to another result.
/// </summary>
public static class BlockedStartDecider
{
    /// <summary>A process counts as "at its start" only if the service detects it within this time after its creation.</summary>
    public static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True if the exe is a Blocked Program: its App Rule has the block, the rule is not for System,
    /// the path is readable and absolute, and <paramref name="protectedPrograms"/> does not protect it.
    /// </summary>
    public static bool IsBlockedProgram(CategoryResolution resolution, ProtectedPrograms protectedPrograms) =>
        resolution.Rule is { IsBlocked: true }
        && resolution.Category != AppCategory.System
        && resolution.CanonicalPath is not null
        && !protectedPrograms.IsProtected(resolution.CanonicalPath);

    public static BlockDecision Decide(BlockedStartInput input, ProtectedPrograms protectedPrograms)
    {
        if (!input.IsRestrictedSession || !IsBlockedProgram(input.Resolution, protectedPrograms))
            return BlockDecision.Ignore;

        var startedAfterBlock = input.CreatedUtc > input.BlockEpochUtc;
        var age = input.DetectedUtc - input.CreatedUtc;
        if (!startedAfterBlock || age > StartWindow)
            return BlockDecision.Suspend;

        // A new tab of a browser that ran before the block is a new process of the same exe. Its parent is the
        // old browser. Close it, and the child can lose the text of the tab. The parent start time must be before
        // the start of the new process, else the pid belongs to another process now.
        if (input.Parent is { IsBlockedProgram: true } parent
            && parent.StartTimeUtc <= input.BlockEpochUtc
            && parent.StartTimeUtc < input.CreatedUtc)
            return BlockDecision.Suspend;

        return BlockDecision.Close;
    }
}

/// <summary>What the command line says about a process of a Blocked Program.</summary>
/// <param name="IsSilent">A background start or a sub-process: no child message and no Telegram notice.</param>
/// <param name="IsSubProcess">A sub-process of a browser. It does not count as a blocked start in the report.</param>
public readonly record struct StartKind(bool IsSilent, bool IsSubProcess);

/// <summary>
/// Reads the command line of a Blocked Program process. The command line is untrusted text. It only selects the
/// silent path and the count of the report; it never changes the block decision.
/// </summary>
public static class SilentStartClassifier
{
    public static StartKind Classify(string? commandLine)
    {
        var silent = false;
        var sub = false;
        foreach (var token in Tokens(commandLine))
        {
            if (token.Equals("--no-startup-window", StringComparison.OrdinalIgnoreCase))
                silent = true;
            else if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
                silent = sub = true;
            else if (token.Equals("-contentproc", StringComparison.OrdinalIgnoreCase))
                sub = true; // a Firefox content process: not silent, but not a new start of the browser
        }
        return new StartKind(silent, sub);
    }

    /// <summary>The arguments of a Windows command line: split at white space outside double quotes, quotes removed.</summary>
    internal static IEnumerable<string> Tokens(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            yield break;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                    yield return current.ToString();
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken)
            yield return current.ToString();
    }
}
