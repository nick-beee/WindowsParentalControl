// Test program for VM 102: a window with a running counter and a looping tone.
// A suspended process shows a frozen counter and its tone stops.
using System.Media;

ApplicationConfiguration.Initialize();
var name = Path.GetFileName(Environment.ProcessPath);
var form = new Form { Text = $"{name} (pid {Environment.ProcessId})", Width = 420, Height = 200 };
var label = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 28), TextAlign = ContentAlignment.MiddleCenter };
form.Controls.Add(label);
var start = DateTime.Now;
// The cpu=on argument uses about 20 ms of CPU time in each tick (about 80 ms per second), also while the window is
// minimized. A resumed copy then shows a clear CPU growth (lock-after-repeated-resume, design D10).
var cpuLoad = args.Contains("cpu=on");
var timer = new System.Windows.Forms.Timer { Interval = 250 };
timer.Tick += (_, _) =>
{
    label.Text = $"{name}\n{(DateTime.Now - start):hh\\:mm\\:ss}";
    if (cpuLoad)
    {
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 20) { }
    }
};
timer.Start();

// A 440 Hz tone, 1 s, played in a loop. The sound=off argument starts it without sound.
if (!args.Contains("sound=off"))
{
    const int rate = 22050;
    var samples = rate;
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write("RIFF"u8.ToArray()); w.Write(36 + samples * 2); w.Write("WAVE"u8.ToArray());
    w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8.ToArray()); w.Write(samples * 2);
    for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 3000));
    ms.Position = 0;
    var player = new SoundPlayer(ms);
    player.PlayLooping();
}

Application.Run(form);
