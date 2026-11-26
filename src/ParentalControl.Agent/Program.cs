using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ParentalControl.Agent;

internal static class Program
{
    private static Mutex? _mutex;

    // The highest shutdown level for applications: Windows sends WM_QUERYENDSESSION to the agent before the other
    // programs of the session (default level 0x280). SHUTDOWN_NORETRY keeps the agent out of the "This app is
    // preventing" list.
    private const uint ShutdownLevel = 0x3FF;
    private const uint ShutdownNoRetry = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [STAThread]
    private static void Main(string[] args)
    {
        // 1. Prevent duplicate agent instances for the same user session.
        var mutexName = $"Global\\ParentalControlAgent_{Environment.UserName}";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            return;
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
        var sessionEnding = false;
        app.SessionEnding += (_, _) => sessionEnding = true;

        using var controller = new AgentController(app);
        app.Run();

        if (sessionEnding)
            controller.WaitForSessionEndQuery(TimeSpan.FromSeconds(3));
    }
}
