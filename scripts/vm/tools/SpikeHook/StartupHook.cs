// A startup hook for task 1.3 of session-probe-and-agent-channel: Kid sets DOTNET_STARTUP_HOOKS to this DLL.
// If a process loads it, it writes a line to C:\Users\Public\spike\hook.log. Not part of the product.
internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            File.AppendAllText(@"C:\Users\Public\spike\hook.log",
                $"{DateTime.UtcNow:O} hook loaded in {Environment.ProcessPath} pid={Environment.ProcessId} user={Environment.UserName}{Environment.NewLine}");
        }
        catch { }
    }
}
