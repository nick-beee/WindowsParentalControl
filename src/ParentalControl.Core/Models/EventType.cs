namespace ParentalControl.Core.Models;

public enum EventType
{
    LOGIN,
    LOGOUT,
    SLEEP,
    WAKE,
    LIMIT_REACHED,
    FORCED_LOGOUT,
    LOGIN_DENIED,
    CLOCK_TAMPER,
    SESSION_LOCKED,
    SESSION_UNLOCKED,
    SECURITY_ALERT,
    PROMPT_SENT,
    PROMPT_ANSWERED,
    PROMPT_TIMEOUT,
    BREAK_STARTED,
    BREAK_ENDED,
    PROGRAM_BLOCKED,
    TIME_ZONE_CHANGE,
    STORAGE_LOCKDOWN,
    // session-probe-and-agent-channel (design D5, D6, D6a, D12): the Session Probe.
    PROBE_OUTAGE,
    PROBE_BLIND,
    PROBE_RESTART,
    PROBE_GAP,
    PROBE_WOULD_LOCK
}
