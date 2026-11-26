using NAudio.CoreAudioApi;

namespace ParentalControl.Agent;

/// <summary>
/// Mutes the audio sessions of the processes that the service suspended (Suspended Apps), and restores
/// the previous mute state of a session when its process is not suspended any more.
/// The service cannot do this itself: the audio sessions belong to the user's session.
/// </summary>
public static class SuspendedAppMuter
{
    private static readonly object Gate = new();

    // Session instance id -> (pid, mute state before the agent muted it).
    private static readonly Dictionary<string, (int Pid, bool WasMuted)> MutedByAgent = new();

    public static void Apply(IReadOnlyCollection<int> suspendedPids)
    {
        lock (Gate)
        {
            if (suspendedPids.Count == 0 && MutedByAgent.Count == 0)
                return;

            var seen = new HashSet<string>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (var i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            var pid = (int)session.GetProcessID;
                            var id = session.GetSessionInstanceIdentifier ?? $"{device.ID}|{pid}|{i}";
                            seen.Add(id);

                            if (suspendedPids.Contains(pid))
                            {
                                if (!MutedByAgent.ContainsKey(id))
                                {
                                    MutedByAgent[id] = (pid, session.SimpleAudioVolume.Mute);
                                    session.SimpleAudioVolume.Mute = true;
                                }
                            }
                            else if (MutedByAgent.Remove(id, out var previous))
                            {
                                session.SimpleAudioVolume.Mute = previous.WasMuted;
                            }
                        }
                    }
                }
            }
            catch
            {
                // No audio device or the audio service is not available: the suspension still holds.
                return;
            }

            // Forget sessions that do not exist any more.
            foreach (var id in MutedByAgent.Keys.Where(k => !seen.Contains(k)).ToList())
                MutedByAgent.Remove(id);
        }
    }
}
