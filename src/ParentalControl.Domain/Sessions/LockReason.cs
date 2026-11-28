namespace ParentalControl.Domain.Sessions;

/// <summary>
/// The Lock Reason: why the session of a Restricted User is locked now (change widget-shows-schedule-lock-after-break,
/// issue #44). The Session Agent shows a text for each reason in the widget and the tray tooltip.
/// </summary>
public enum LockReason
{
    /// <summary>The session is not locked.</summary>
    None,

    /// <summary>The time is outside today's Schedule Window.</summary>
    ScheduleWindow,

    /// <summary>The Allowance Time of today is used up.</summary>
    Allowance,

    /// <summary>A Break runs.</summary>
    Break,

    /// <summary>Another lock: the child, the parent, a Category Limit, a Probe Lock or a Storage Lockdown.</summary>
    Other,
}

/// <summary>
/// Decides the <see cref="LockReason"/> from the state at the time of each status answer, never from the state at
/// the time of the lock. A Break that ends at the Schedule Window end so gives the Schedule Window reason after the
/// end, although the Break started the lock.
/// </summary>
public static class LockReasons
{
    /// <summary>
    /// The reason of a lock. The priority is: Schedule Window, Allowance Time, Break, other. The two limits of the day
    /// come first, because they keep the session locked after the end of a Break.
    /// </summary>
    /// <param name="isLocked">True if the session is locked now.</param>
    /// <param name="outsideScheduleWindow">True if the time of day in the Control Time Zone is outside today's Schedule Window.</param>
    /// <param name="allowanceUsedUp">True if no minute of today's Allowance Time (with the Bonus Minutes) is left.</param>
    /// <param name="breakRunning">True if a Break of the user runs now.</param>
    public static LockReason Resolve(bool isLocked, bool outsideScheduleWindow, bool allowanceUsedUp, bool breakRunning)
    {
        if (!isLocked) return LockReason.None;
        if (outsideScheduleWindow) return LockReason.ScheduleWindow;
        if (allowanceUsedUp) return LockReason.Allowance;
        if (breakRunning) return LockReason.Break;
        return LockReason.Other;
    }

    /// <summary>The wire name of a reason in the status answer of the Agent Channel, or null for <see cref="LockReason.None"/>.</summary>
    public static string? ToWire(LockReason reason) => reason switch
    {
        LockReason.ScheduleWindow => "schedule",
        LockReason.Allowance => "allowance",
        LockReason.Break => "break",
        LockReason.Other => "other",
        _ => null,
    };

    /// <summary>
    /// The reason from the status answer. A locked session with a missing or unknown wire name (for example from an
    /// older service) gives <see cref="LockReason.Other"/>.
    /// </summary>
    public static LockReason FromWire(bool isLocked, string? wire)
    {
        if (!isLocked) return LockReason.None;
        return wire switch
        {
            "schedule" => LockReason.ScheduleWindow,
            "allowance" => LockReason.Allowance,
            "break" => LockReason.Break,
            _ => LockReason.Other,
        };
    }

    /// <summary>The key of the widget text in <see cref="Messages.AgentTexts"/> for a locked session.</summary>
    public static string WidgetTextKey(LockReason reason) => reason switch
    {
        LockReason.ScheduleWindow => "widget_locked_schedule",
        LockReason.Allowance => "widget_locked_allowance",
        LockReason.Break => "widget_locked_break",
        _ => "widget_locked",
    };

    /// <summary>The key of the tray tooltip in <see cref="Messages.AgentTexts"/> for a locked session.</summary>
    public static string TrayTextKey(LockReason reason) => reason switch
    {
        LockReason.ScheduleWindow => "tray_locked_schedule",
        LockReason.Allowance => "tray_locked_allowance",
        LockReason.Break => "tray_locked_break",
        _ => "tray_locked",
    };
}
