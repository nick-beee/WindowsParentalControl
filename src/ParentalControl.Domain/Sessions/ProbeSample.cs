namespace ParentalControl.Domain.Sessions;

/// <summary>One window of a Session Probe sample: the window handle and the thread and process that own it.</summary>
public readonly record struct ProbeWindow(long Hwnd, int Tid, int Pid);

/// <summary>
/// The window under the cursor and the processes of its parent windows (session-probe-and-agent-channel, design D4
/// step 6, B5). <see cref="NoActivate"/> is the <c>WS_EX_NOACTIVATE</c> style of the top-level window of the chain.
/// </summary>
public sealed record ProbeCursor(bool NoActivate, IReadOnlyList<ProbeWindow> Entries);

public enum ProbeSampleKind
{
    /// <summary>A window is in front: <see cref="ProbeSample.Foreground"/> is set.</summary>
    Window,
    /// <summary><c>GetForegroundWindow</c> gave NULL (<c>"window": false</c>).</summary>
    NoWindow,
    /// <summary>A blind sample of the probe (<c>"blind"</c>).</summary>
    Blind,
}

public enum ProbeBlindReason
{
    /// <summary>The input desktop cannot be opened or set.</summary>
    Desktop,
    /// <summary>A window is in front, but it gives no thread.</summary>
    Window,
    /// <summary>More than 8 other processes have visible windows below the Foreground Window, or the walk passed 4096
    /// windows.</summary>
    Children,
}

/// <summary>
/// One sample line of the Session Probe (design D3). <see cref="T"/> is the <c>GetTickCount64</c> value of the probe
/// at the sample, in milliseconds. The title and the desktop name come from programs of the child; they are only for
/// the log and App Usage. <see cref="Duration"/> is the time that the probe took for the sample (<c>dur</c>, an integer
/// of milliseconds; zero if it is missing or not valid); the probe reads <c>t</c> after the read of <c>fg</c>.
/// </summary>
public sealed record ProbeSample(
    long T,
    ProbeSampleKind Kind,
    ProbeBlindReason? BlindReason = null,
    ProbeWindow? Foreground = null,
    ProbeWindow? Focus = null,
    IReadOnlyList<ProbeWindow>? Children = null,
    ProbeCursor? Cursor = null,
    ProbeWindow? Hung = null,
    string? Title = null,
    string? Desktop = null,
    TimeSpan Duration = default)
{
    public IReadOnlyList<ProbeWindow> ChildEntries => Children ?? [];
}
