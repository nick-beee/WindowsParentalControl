using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Sessions;

/// <summary>The role of an entry of a Session Probe sample (session-probe-and-agent-channel, design D3).</summary>
public enum ProbeRole
{
    Foreground,
    Focus,
    Child,
    Cursor,
}

/// <summary>The outcome of the read of an entry by the service (design D3b).</summary>
public enum EntryOutcome
{
    /// <summary>The service has the exe path and the start time.</summary>
    Read,
    /// <summary>The thread or the process ended, or the thread belongs to another process now.</summary>
    Ended,
    /// <summary>Access denied, or a process of another session: the sample is a blind sample.</summary>
    Unreadable,
}

/// <summary>The outcome of the parent chain of a trusted WebView2 process (design D5, S16, S21).</summary>
public enum CreatorOutcome
{
    Read,
    /// <summary>A parent ended, failed the start-time test, is in another session, or the chain is too long.</summary>
    NotAttributed,
}

/// <summary>The creator of a trusted WebView2 process: the first process of its parent chain that is not one.</summary>
public sealed record WebView2Creator(CreatorOutcome Outcome, ProcessIdentity Process = default, string? ExePath = null)
{
    public static WebView2Creator NotAttributed { get; } = new(CreatorOutcome.NotAttributed);
}

/// <summary>
/// One entry of a sample after the read of the service. <see cref="Creator"/> is set only for a trusted WebView2
/// process (<see cref="TrustedWebView2"/>).
/// </summary>
public sealed record SampleEntry(
    ProbeRole Role,
    EntryOutcome Outcome,
    ProcessIdentity Process = default,
    string? ExePath = null,
    WebView2Creator? Creator = null);

/// <summary>The process of a Foreground Window that was not a ghost window, for the ghost rule of design D4.</summary>
public sealed record ForegroundProcess(ProcessIdentity Process, string ExePath);

/// <summary>
/// The read entries of one sample. <see cref="Entries"/> has the <c>fg</c>, <c>focus</c>, <c>children</c> and
/// <c>cursor</c> entries with their roles. <see cref="Hung"/> is the read <c>hung</c> entry, if the sample has one.
/// <see cref="ProbeBlind"/> is true for a blind sample of the probe (<c>"blind"</c>).
/// </summary>
public sealed record SampleReading(
    IReadOnlyList<SampleEntry> Entries,
    bool CursorNoActivate = false,
    SampleEntry? Hung = null,
    bool ProbeBlind = false);

/// <summary>The facts of the service that the selection needs. None of them comes from the Session Agent.</summary>
/// <param name="Categorize">The App Category of an exe path (<see cref="CategoryResolver"/>).</param>
/// <param name="WindowsFolder">The Windows folder, for the path of <c>dwm.exe</c>.</param>
/// <param name="IsAgent">True for the Session Agent of the launch record of the session.</param>
/// <param name="PromptActive">The service has an active Interactive Session Prompt for the user of the session.</param>
/// <param name="IsSuspended">True for a process that the service suspended for this user.</param>
/// <param name="IsRunning">True if the process still runs (the same pid and start time).</param>
/// <param name="LastForeground">The Foreground Window process of the last sample of the session that was not a ghost.</param>
public sealed record SelectionContext(
    Func<string, AppCategory> Categorize,
    string WindowsFolder,
    Func<ProcessIdentity, bool> IsAgent,
    bool PromptActive,
    Func<ProcessIdentity, bool> IsSuspended,
    Func<ProcessIdentity, bool> IsRunning,
    ForegroundProcess? LastForeground = null);

/// <summary>A counted process of a sample.</summary>
public sealed record CountedProcess(ProcessIdentity Process, string ExePath, AppCategory Category, ProbeRole Role, bool IsAgent);

/// <summary>
/// The result of <see cref="CountedProcesses.Select"/>.
/// </summary>
/// <param name="Counted">The counted processes, each process once.</param>
/// <param name="Categories">The categories that get the interval, each once.</param>
/// <param name="ExesToRecord">The exes for <see cref="ForegroundHistory"/>: each Read entry, also a <c>cursor</c>
/// entry without <c>noActivate</c>, and each WebView2 creator.</param>
/// <param name="Blind">A blind sample: a blind sample of the probe or an Unreadable entry.</param>
/// <param name="EndedItem">An Ended entry in a role that would count time: one budget item.</param>
/// <param name="NotAttributed">The trusted WebView2 exes whose creator is not attributed, for one log line.</param>
/// <param name="NextLastForeground">The value of <see cref="SelectionContext.LastForeground"/> for the next sample.</param>
public sealed record SampleSelection(
    IReadOnlyList<CountedProcess> Counted,
    IReadOnlyList<AppCategory> Categories,
    IReadOnlyList<string> ExesToRecord,
    bool Blind,
    bool EndedItem,
    IReadOnlyList<string> NotAttributed,
    ForegroundProcess? NextLastForeground);

/// <summary>
/// The test for a trusted WebView2 process (design D5 rule 3): <c>msedgewebview2.exe</c> below
/// <c>Microsoft\EdgeWebView\Application\</c> in a Program Files folder. Only administrators can change these folders.
/// </summary>
public sealed class TrustedWebView2
{
    private readonly List<string> _folders = [];

    public TrustedWebView2(string? programFilesX86, string? programFiles)
    {
        foreach (var root in new[] { programFilesX86, programFiles })
        {
            if (WindowsPath.CanonicalizeFolder(root) is not { } folder) continue;
            var app = WindowsPath.CanonicalizeFolder(folder + @"Microsoft\EdgeWebView\Application");
            if (app is not null && !_folders.Contains(app, StringComparer.OrdinalIgnoreCase)) _folders.Add(app);
        }
    }

    public bool IsTrusted(string? exePath)
    {
        var canonical = WindowsPath.Canonicalize(exePath);
        return canonical is not null
               && string.Equals(WindowsPath.GetFileName(canonical), ProtectedPrograms.WebView2FileName, StringComparison.OrdinalIgnoreCase)
               && _folders.Any(f => WindowsPath.IsBelow(canonical, f));
    }
}

/// <summary>
/// The counted processes of a Session Probe sample (design D5 steps 1 to 6, the ghost rule of design D4).
/// </summary>
public static class CountedProcesses
{
    public static SampleSelection Select(SampleReading reading, SelectionContext context, TrustedWebView2 trusted)
    {
        var counted = new List<CountedProcess>();
        var categories = new List<AppCategory>();
        var record = new List<string>();
        var notAttributed = new List<string>();
        var blind = reading.ProbeBlind;
        var endedItem = false;
        ForegroundProcess? nextLast = null;
        var dwm = WindowsPath.Canonicalize(context.WindowsFolder.TrimEnd('\\') + @"\System32\dwm.exe");

        foreach (var original in reading.Entries)
        {
            var entry = original;
            var counts = entry.Role != ProbeRole.Cursor || reading.CursorNoActivate;

            if (entry.Outcome == EntryOutcome.Unreadable)
            {
                blind = true;
                continue;
            }
            if (entry.Outcome == EntryOutcome.Ended)
            {
                if (counts) endedItem = true;
                continue;
            }
            if (entry.ExePath is null) continue;

            if (entry.Role == ProbeRole.Foreground)
            {
                var isGhost = dwm is not null && string.Equals(WindowsPath.Canonicalize(entry.ExePath), dwm, StringComparison.OrdinalIgnoreCase);
                if (!isGhost)
                    nextLast = new ForegroundProcess(entry.Process, entry.ExePath);
                else
                {
                    record.Add(entry.ExePath);
                    nextLast = context.LastForeground;
                    if (reading.Hung is { } hung)
                    {
                        // The hung window in the role fg. An Unreadable or Ended hung entry follows D3b.
                        if (hung.Outcome == EntryOutcome.Unreadable)
                        {
                            blind = true;
                            continue;
                        }
                        if (hung.Outcome == EntryOutcome.Ended || hung.ExePath is null)
                        {
                            endedItem = true;
                            continue;
                        }
                        entry = hung with { Role = ProbeRole.Foreground };
                        nextLast = new ForegroundProcess(hung.Process, hung.ExePath);
                    }
                    else if (context.LastForeground is { } last && context.IsRunning(last.Process))
                        entry = new SampleEntry(ProbeRole.Foreground, EntryOutcome.Read, last.Process, last.ExePath);
                }
            }

            record.Add(entry.ExePath!);

            if (trusted.IsTrusted(entry.ExePath))
            {
                if (entry.Creator is not { Outcome: CreatorOutcome.Read, ExePath: not null } creator)
                {
                    notAttributed.Add(entry.ExePath!);
                    continue;
                }
                record.Add(creator.ExePath);
                entry = entry with { Process = creator.Process, ExePath = creator.ExePath, Creator = null };
            }

            if (!counts) continue;
            if (Count(entry, context) is not { } process) continue;
            if (counted.Any(c => c.Process == process.Process)) continue;
            counted.Add(process);
            if (!categories.Contains(process.Category)) categories.Add(process.Category);
        }

        return new SampleSelection(
            counted,
            categories,
            record.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            blind,
            endedItem,
            notAttributed,
            nextLast);
    }

    // The counted process of a Read entry in a role that counts, or null.
    private static CountedProcess? Count(SampleEntry entry, SelectionContext context)
    {
        if (context.IsSuspended(entry.Process)) return null;
        if (context.IsAgent(entry.Process))
        {
            // The Session Agent counts in Unknown as fg, focus or child, never as cursor, never during a prompt (S1,
            // N22, S10).
            if (context.PromptActive || entry.Role == ProbeRole.Cursor) return null;
            return new CountedProcess(entry.Process, entry.ExePath!, AppCategory.Unknown, entry.Role, IsAgent: true);
        }
        var category = context.Categorize(entry.ExePath!);
        return category == AppCategory.System
            ? null
            : new CountedProcess(entry.Process, entry.ExePath!, category, entry.Role, IsAgent: false);
    }
}
