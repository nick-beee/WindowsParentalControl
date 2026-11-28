using System.ComponentModel;
using System.Runtime.InteropServices;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Starts a helper of the service as SYSTEM in a session of a user (session-probe-and-agent-channel, design D2): the
/// Session Probe. The child cannot open, end, suspend, read or change such a process.
/// <list type="bullet">
/// <item>A restricted copy of the token of the service: no privilege except <c>SeChangeNotifyPrivilege</c>
/// (<c>DISABLE_MAX_PRIVILEGE</c>), and <c>WRITE_RESTRICTED</c> with the restricting SID <c>S-1-5-18</c> (N7; task 1.3
/// proved that the probe still samples and writes its pipe with it).</item>
/// <item><c>TokenSessionId</c> of the session (needs <c>SeTcbPrivilege</c> in the service) and a SYSTEM-only
/// <c>TokenDefaultDacl</c>, so each object of the process is closed to the child.</item>
/// <item>The environment of SYSTEM (the block of the token, else the environment of the service), never that of the
/// child, without runtime variables and with
/// <c>DOTNET_EnableDiagnostics=0</c> and <c>DOTNET_DISABLE_GUI_ERRORS=1</c>.</item>
/// <item>A SYSTEM-only DACL on the process and its first thread; <c>DETACHED_PROCESS</c> (no console and no console
/// host, S13); <c>HIGH_PRIORITY_CLASS</c>; no Job Object (N17); the folder of the exe as the working folder.</item>
/// </list>
/// The hard rule covers the programs of the child, not this helper of the service.
/// </summary>
public static class SessionSystemProcess
{
    public const string SystemSid = "S-1-5-18";

    /// <summary>
    /// Starts the exe. Throws <see cref="Win32Exception"/> on a failure.
    /// </summary>
    /// <param name="arguments">The arguments; <c>{handle}</c> becomes the value of the inherited handle.</param>
    /// <param name="inheritHandle">The one handle that the process inherits (the write end of its pipe), or
    /// <see cref="IntPtr.Zero"/>.</param>
    /// <param name="environmentWarning">Gets the error if <c>CreateEnvironmentBlock</c> fails; the start then uses the
    /// environment of the service itself (also SYSTEM), through <see cref="LaunchEnvironment.BuildForProbe"/>.</param>
    public static LaunchedProcess Start(int sessionId, string exePath, string? arguments, IntPtr inheritHandle,
        Action<string>? environmentWarning = null)
    {
        var token = CreateToken(sessionId);
        try
        {
            var entries = ProcessLauncher.TryEnvironmentOf(token, out var envError);
            if (entries is null) environmentWarning?.Invoke(envError ?? "CreateEnvironmentBlock failed");
            var environment = LaunchEnvironment.BuildForProbe(entries, Environment.GetEnvironmentVariables());
            var folder = Path.GetDirectoryName(exePath) ?? throw new ArgumentException("The exe path has no folder.", nameof(exePath));
            // Design D2: no error dialog of any kind, also from the loader before Main of the probe runs. The probe
            // inherits the process error mode of the service at CreateProcessAsUserW. The error mode is process-wide, so
            // the service sets it only for this launch, under the launch lock (the agent launch takes the same lock and so
            // never inherits it), and restores it at once. Other threads of the service run with it for this short time
            // only; a session-0 service shows no error dialog anyway.
            lock (ProcessLaunchLock.Gate)
            {
                var previous = LaunchNative.SetErrorMode(
                    LaunchNative.SEM_FAILCRITICALERRORS | LaunchNative.SEM_NOGPFAULTERRORBOX | LaunchNative.SEM_NOOPENFILEERRORBOX);
                try
                {
                    return ProcessLauncher.Launch(token, exePath, arguments, inheritHandle,
                        LaunchNative.DETACHED_PROCESS | LaunchNative.HIGH_PRIORITY_CLASS, environment, systemOnlyObjects: true, folder);
                }
                finally
                {
                    LaunchNative.SetErrorMode(previous);
                }
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    // Design D2 steps 1 to 3.
    private static IntPtr CreateToken(int sessionId)
    {
        if (!LaunchNative.OpenProcessToken(LaunchNative.GetCurrentProcess(),
                LaunchNative.TOKEN_DUPLICATE | LaunchNative.TOKEN_QUERY | LaunchNative.TOKEN_ASSIGN_PRIMARY
                | LaunchNative.TOKEN_ADJUST_DEFAULT | LaunchNative.TOKEN_ADJUST_SESSIONID, out var own))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");

        var system = IntPtr.Zero;
        var restricted = IntPtr.Zero;
        var primary = IntPtr.Zero;
        var descriptor = IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!LaunchNative.ConvertStringSidToSidW(SystemSid, out system))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSidW");
            var restrict = new[] { new LaunchNative.SID_AND_ATTRIBUTES { Sid = system } };
            if (!LaunchNative.CreateRestrictedToken(own, LaunchNative.DISABLE_MAX_PRIVILEGE | LaunchNative.WRITE_RESTRICTED,
                    0, IntPtr.Zero, 0, IntPtr.Zero, restrict.Length, restrict, out restricted))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken");
            if (!NativeMethods.DuplicateTokenEx(restricted, LaunchNative.TOKEN_ALL_ACCESS, IntPtr.Zero,
                    NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");

            Marshal.WriteInt32(buffer, sessionId);
            if (!LaunchNative.SetTokenInformation(primary, LaunchNative.TokenSessionId, buffer, sizeof(int)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenSessionId)");

            if (!LaunchNative.ConvertStringSecurityDescriptorToSecurityDescriptorW(LaunchNative.SystemOnlySddl, 1, out descriptor, out _)
                || !LaunchNative.GetSecurityDescriptorDacl(descriptor, out _, out var dacl, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SYSTEM-only DACL");
            Marshal.WriteIntPtr(buffer, dacl); // TOKEN_DEFAULT_DACL
            if (!LaunchNative.SetTokenInformation(primary, LaunchNative.TokenDefaultDacl, buffer, IntPtr.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenDefaultDacl)");

            var result = primary;
            primary = IntPtr.Zero;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (descriptor != IntPtr.Zero) LaunchNative.LocalFree(descriptor);
            if (system != IntPtr.Zero) LaunchNative.LocalFree(system);
            if (primary != IntPtr.Zero) NativeMethods.CloseHandle(primary);
            if (restricted != IntPtr.Zero) NativeMethods.CloseHandle(restricted);
            NativeMethods.CloseHandle(own);
        }
    }
}
