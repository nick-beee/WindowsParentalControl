namespace ParentalControl.Domain.Sessions;

public enum SessionEndKind
{
    /// <summary>The Restart Manager closes only this program (ENDSESSION_CLOSEAPP). The session does not end.</summary>
    None,
    /// <summary>A sign-out (ENDSESSION_LOGOFF).</summary>
    Logoff,
    /// <summary>A shutdown or a restart (lParam 0 or ENDSESSION_CRITICAL).</summary>
    Shutdown,
}

/// <summary>Reads the lParam of WM_QUERYENDSESSION.</summary>
public static class SessionEndQuery
{
    public const long EndSessionCloseApp = 0x1;
    public const long EndSessionCritical = 0x40000000;
    public const long EndSessionLogoff = 0x80000000;

    /// <summary>
    /// Each query without ENDSESSION_CLOSEAPP is an end of the session. A restart can send ENDSESSION_CRITICAL
    /// without ENDSESSION_LOGOFF (VM result R4), so the method does not require a known flag.
    /// </summary>
    public static SessionEndKind Classify(long lParam)
    {
        if ((lParam & EndSessionCloseApp) != 0)
            return SessionEndKind.None;
        return (lParam & EndSessionLogoff) != 0 ? SessionEndKind.Logoff : SessionEndKind.Shutdown;
    }

    /// <summary>
    /// The longest time between the query of the hidden window of the agent and the SessionEnding event of WPF
    /// for the same end of the session. Windows queries the windows of one process within milliseconds.
    /// </summary>
    public static readonly TimeSpan SameEndWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tells if the last query of the hidden window belongs to the end of the session that WPF reported.
    /// An older query does not count: a CLOSEAPP query of an installer, or a fake query from the child.
    /// The times are milliseconds of one monotonic clock (Environment.TickCount64).
    /// </summary>
    public static bool IsQueryOfThisEnd(long? lastQueryMs, long sessionEndingMs) =>
        lastQueryMs is { } query && query >= sessionEndingMs - (long)SameEndWindow.TotalMilliseconds;
}
