using Microsoft.Win32;
using ParentalControl.Domain.Requests;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Finds the machine-wide Evergreen WebView2 Runtime (request-window, design D4 and D7). The Session Agent and the
/// service use the same lookup: <c>pv</c> from the 32-bit view of HKLM, the decision of
/// <see cref="WebView2RuntimeLocation"/>, and the base folder from the Program Files (x86) known folder, never from
/// the variable <c>%ProgramFiles(x86)%</c>.
/// </summary>
public static class WebView2Runtime
{
    /// <summary>The version <c>pv</c> of the Evergreen runtime, or null if the key or the value is missing.</summary>
    public static string? ReadVersion()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(WebView2RuntimeLocation.ClientKey);
            return key?.GetValue(WebView2RuntimeLocation.VersionValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder of the machine-wide runtime that has <c>msedgewebview2.exe</c>, or null if the runtime is missing.
    /// </summary>
    public static string? FindFolder()
    {
        var folder = WebView2RuntimeLocation.RuntimeFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolderOption.DoNotVerify),
            ReadVersion());
        return folder is not null && File.Exists(WebView2RuntimeLocation.BrowserExePath(folder)) ? folder : null;
    }

    /// <summary>
    /// Removes each environment variable whose name starts with <c>WEBVIEW2_</c> (any case) from this process, so that
    /// the WebView2 loader reads no override of the user. The variables of the user stay as they are.
    /// </summary>
    public static IReadOnlyList<string> RemoveEnvironmentOverrides()
    {
        var removed = new List<string>();
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList())
        {
            if (!name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) continue;
            Environment.SetEnvironmentVariable(name, null);
            removed.Add(name);
        }
        return removed;
    }
}
