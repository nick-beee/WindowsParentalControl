// Test tool for VM 102: lists the audio sessions of this user with pid, process and mute state.
using System.Diagnostics;
using NAudio.CoreAudioApi;
var lines = new List<string> { DateTime.Now.ToString("HH:mm:ss") };
using var en = new MMDeviceEnumerator();
foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
{
    var m = d.AudioSessionManager; m.RefreshSessions();
    for (var i = 0; i < m.Sessions.Count; i++)
    {
        var s = m.Sessions[i]; var pid = (int)s.GetProcessID;
        string name; try { name = pid == 0 ? "system" : Process.GetProcessById(pid).ProcessName; } catch { name = "?"; }
        lines.Add($"{d.FriendlyName} | pid {pid} {name} | state {s.State} | mute {s.SimpleAudioVolume.Mute}");
    }
}
File.WriteAllLines(args.Length > 0 ? args[0] : @"C:\Users\Public\audio.txt", lines);
