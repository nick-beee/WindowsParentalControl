using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ParentalControl.Core.Platform;

namespace ParentalControl.Agent;

internal static class Program
{
    // The highest shutdown level for applications: Windows sends WM_QUERYENDSESSION to the agent before the other
    // programs of the session (default level 0x280). SHUTDOWN_NORETRY keeps the agent out of the "This app is
    // preventing" list.
    private const uint ShutdownLevel = 0x3FF;
    private const uint ShutdownNoRetry = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        // 1. Only the service starts the agent (session-probe-and-agent-channel, design D7 and D9): it gives the agent
        // the client end of its Agent Channel. Without a channel, or with a channel whose server does not run in
        // session 0, the agent exits before it shows any window. This check is a guard for the user interface only;
        // the service takes the identity of each message from its own launch record.
        if (!TryReadChannelHandle(args, out var handle))
        {
            AgentLog.Logger.Information("Agent started without --channel: exit");
            return 1;
        }
        if (!AgentChannelPlatform.IsServiceChannel(handle, out var refusal))
        {
            AgentLog.Logger.Warning("Agent channel refused ({Reason}): exit", refusal);
            return 2;
        }

        AgentChannelClient channel;
        try
        {
            var pipe = new SafePipeHandle(handle, ownsHandle: true);
            // The overload with isConnected is the only one with a handle in .NET 10; it gives no SYSLIB0063 warning
            // there. The handle was opened with FILE_FLAG_OVERLAPPED (S12), so the asynchronous stream can bind it.
            channel = new AgentChannelClient(new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, pipe));
        }
        catch (Exception ex)
        {
            AgentLog.Logger.Warning(ex, "Agent channel could not be opened: exit");
            return 3;
        }

        // 2. Get the end-session query before the Suspended Apps, so that the agent can resume them first.
        if (!SetProcessShutdownParameters(ShutdownLevel, ShutdownNoRetry))
            AgentLog.Logger.Error("SetProcessShutdownParameters failed: error {Error}", Marshal.GetLastWin32Error());

        // 3. Run native WPF Application message loop for smooth rendering, drag, and layered windows
        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };

        // WPF shuts the agent down at its own WM_QUERYENDSESSION. The window of the session-end resume must still get
        // its query, so wait for it before the controller closes that window.
        long? sessionEndingMs = null;
        AgentController? controllerRef = null;
        app.SessionEnding += (_, e) =>
        {
            var now = Environment.TickCount64;
            sessionEndingMs = now;
            // Diagnosis: the order of the query of the hidden window and the query of the WPF window.
            AgentLog.Logger.Information("WPF SessionEnding {Reason}: the hidden window got the query already {Handled}",
                e.ReasonSessionEnding, controllerRef?.IsSessionEndQueryHandled(now) ?? false);
        };

        // If the channel breaks (the service stopped or closed it), the agent exits. A new service run starts a new agent.
        channel.Broken += reason =>
        {
            AgentLog.Logger.Information("Agent channel broken ({Reason}): exit", reason);
            app.Dispatcher.BeginInvoke(() => app.Shutdown());
        };

        using (channel)
        using (var controller = new AgentController(app, channel))
        {
            controllerRef = controller;
            channel.Start();
            app.Run();

            if (sessionEndingMs is { } endingMs)
                controller.WaitForSessionEndQuery(TimeSpan.FromSeconds(3), endingMs);
        }
        return 0;
    }

    /// <summary>Reads the handle value of <c>--channel &lt;value&gt;</c>.</summary>
    private static bool TryReadChannelHandle(string[] args, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--channel"
                && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                handle = new IntPtr(value);
                return true;
            }
        }
        return false;
    }
}
