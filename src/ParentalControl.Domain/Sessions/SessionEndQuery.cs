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
}
