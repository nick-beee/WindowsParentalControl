using System.Collections.Concurrent;

namespace ParentalControl.Service;

/// <summary>
/// The live activity of each Restricted User for the admin page and the Telegram status: the exe file name and the
/// window title of the last Session Probe sample (session-probe-and-agent-channel, design D5). Only
/// <see cref="SessionProbeHost"/> writes it; it never takes data of the Session Agent.
/// </summary>
public static class LiveActivities
{
    public static ConcurrentDictionary<string, (string ProcessName, string WindowTitle, DateTime LastSeen)> Shared { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
