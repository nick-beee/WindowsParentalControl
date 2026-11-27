using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Core.Platform;

/// <summary>
/// A process that the service started in a session. The service keeps <see cref="Handle"/> to know when the process
/// ends; the caller disposes it.
/// </summary>
public sealed class LaunchedProcess : IDisposable
{
    internal LaunchedProcess(SafeProcessHandle handle, int pid, DateTime startUtc, string userSid)
    {
        Handle = handle;
        Pid = pid;
        StartUtc = startUtc;
        UserSid = userSid;
    }

    public SafeProcessHandle Handle { get; }
    public int Pid { get; }
    public DateTime StartUtc { get; }

    /// <summary>The SID of the token that started the process (<c>TokenUser</c>).</summary>
    public string UserSid { get; }

    public ProcessIdentity Identity => new(Pid, StartUtc);

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// <c>CreateProcessAsUserW</c> with at most one inherited handle (session-probe-and-agent-channel, design D2 steps 5 to
/// 8 and D7 step 3). The handle is duplicated as inheritable only for the call, under <see cref="ProcessLaunchLock"/>,
/// and the start passes only that duplicate in <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>.
/// </summary>
internal static class ProcessLauncher
{
    /// <summary>The text in the arguments that the launcher replaces with the value of the inherited handle.</summary>
    internal const string HandlePlaceholder = "{handle}";

    /// <summary>The environment entries of a token without the user's environment of the service.</summary>
    internal static List<string> EnvironmentOf(IntPtr token)
    {
        if (!NativeMethods.CreateEnvironmentBlock(out var block, token, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock");
        try
        {
            var entries = new List<string>();
            var p = block;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(p) ?? "";
                if (entry.Length == 0) break;
                entries.Add(entry);
                p += (entry.Length + 1) * 2;
            }
            return entries;
        }
        finally
        {
            NativeMethods.DestroyEnvironmentBlock(block);
        }
    }

    /// <summary>The environment entries of a token, or null with the error if <c>CreateEnvironmentBlock</c> fails.</summary>
    internal static List<string>? TryEnvironmentOf(IntPtr token, out string? error)
    {
        error = null;
        try
        {
            return EnvironmentOf(token);
        }
        catch (Win32Exception ex)
        {
            error = $"{ex.Message} ({ex.NativeErrorCode})";
            return null;
        }
    }

    /// <summary>The string SID of the user of a token.</summary>
    internal static string TokenUserSid(IntPtr token)
    {
        LaunchNative.GetTokenInformation(token, LaunchNative.TokenUser, IntPtr.Zero, 0, out var size);
        var buffer = Marshal.AllocHGlobal(Math.Max(size, 64));
        try
        {
            if (!LaunchNative.GetTokenInformation(token, LaunchNative.TokenUser, buffer, Math.Max(size, 64), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenUser)");
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_USER.User.Sid
            if (!LaunchNative.ConvertSidToStringSidW(sid, out var text))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertSidToStringSidW");
            try { return Marshal.PtrToStringUni(text)!; }
            finally { LaunchNative.LocalFree(text); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> with the token. Throws <see cref="Win32Exception"/> on a failure. If the start
    /// time of the new process cannot be read, it ends the process and throws.
    /// </summary>
    /// <param name="arguments">The arguments; <see cref="HandlePlaceholder"/> becomes the value of the inherited handle.</param>
    /// <param name="inheritHandle">The one handle that the process inherits, or <see cref="IntPtr.Zero"/> for none.</param>
    /// <param name="flags">More creation flags, for example <c>DETACHED_PROCESS</c>.</param>
    /// <param name="environment">The environment entries.</param>
    /// <param name="systemOnlyObjects">Give the process and its first thread a DACL for SYSTEM only.</param>
    internal static LaunchedProcess Launch(IntPtr token, string exePath, string? arguments, IntPtr inheritHandle, uint flags,
        IReadOnlyList<string> environment, bool systemOnlyObjects, string workingFolder)
    {
        var userSid = TokenUserSid(token);
        var env = Marshal.StringToHGlobalUni(LaunchEnvironment.ToBlock(environment));
        var descriptor = IntPtr.Zero;
        var list = IntPtr.Zero;
        var value = IntPtr.Zero;
        try
        {
            if (systemOnlyObjects && !LaunchNative.ConvertStringSecurityDescriptorToSecurityDescriptorW(LaunchNative.SystemOnlySddl, 1, out descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSecurityDescriptorToSecurityDescriptorW");
            var attributes = new LaunchNative.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<LaunchNative.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
            };
            var threadAttributes = attributes;

            // cb is the size of STARTUPINFOEX only with EXTENDED_STARTUPINFO_PRESENT (a start with a handle list).
            var startup = new LaunchNative.STARTUPINFOEX();
            startup.StartupInfo.cb = inheritHandle != IntPtr.Zero
                ? Marshal.SizeOf<LaunchNative.STARTUPINFOEX>()
                : Marshal.SizeOf<NativeMethods.STARTUPINFO>();
            startup.StartupInfo.lpDesktop = @"winsta0\default";

            lock (ProcessLaunchLock.Gate)
            {
                var duplicate = IntPtr.Zero;
                try
                {
                    var commandLine = new StringBuilder($"\"{exePath}\"");
                    if (inheritHandle != IntPtr.Zero)
                    {
                        if (!LaunchNative.DuplicateHandle(LaunchNative.GetCurrentProcess(), inheritHandle, LaunchNative.GetCurrentProcess(),
                                out duplicate, 0, true, LaunchNative.DUPLICATE_SAME_ACCESS))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle");

                        var size = IntPtr.Zero;
                        LaunchNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                        list = Marshal.AllocHGlobal(size);
                        if (!LaunchNative.InitializeProcThreadAttributeList(list, 1, 0, ref size))
                        {
                            Marshal.FreeHGlobal(list);
                            list = IntPtr.Zero;
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                        }
                        value = Marshal.AllocHGlobal(IntPtr.Size);
                        Marshal.WriteIntPtr(value, duplicate);
                        if (!LaunchNative.UpdateProcThreadAttribute(list, 0, LaunchNative.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, value,
                                IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");
                        startup.lpAttributeList = list;
                    }

                    if (!string.IsNullOrEmpty(arguments))
                        commandLine.Append(' ').Append(arguments.Replace(HandlePlaceholder, ((long)duplicate).ToString()));

                    var creationFlags = flags | LaunchNative.CREATE_UNICODE_ENVIRONMENT
                                              | (list != IntPtr.Zero ? LaunchNative.EXTENDED_STARTUPINFO_PRESENT : 0);
                    if (!LaunchNative.CreateProcessAsUserW(token, exePath, commandLine, ref attributes, ref threadAttributes,
                            list != IntPtr.Zero, creationFlags, env, workingFolder, ref startup, out var info))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUserW");

                    NativeMethods.CloseHandle(info.hThread);
                    var handle = new SafeProcessHandle(info.hProcess, ownsHandle: true);
                    // The start time is half of the identity of the process. Without it the launch fails: the
                    // process ends, so no process runs without its launch record (no UtcNow fallback).
                    if (!NativeMethods.GetProcessTimes(info.hProcess, out var creation, out _, out _, out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        NativeMethods.TerminateProcess(info.hProcess, 1);
                        handle.Dispose();
                        throw new Win32Exception(error, "GetProcessTimes");
                    }
                    return new LaunchedProcess(handle, info.dwProcessId, DateTime.FromFileTimeUtc(creation), userSid);
                }
                finally
                {
                    if (duplicate != IntPtr.Zero) NativeMethods.CloseHandle(duplicate);
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                LaunchNative.DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }
            if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
            if (descriptor != IntPtr.Zero) LaunchNative.LocalFree(descriptor);
            Marshal.FreeHGlobal(env);
        }
    }
}
