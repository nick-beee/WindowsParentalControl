using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Requests;

/// <summary>
/// The folder decision for the machine-wide Evergreen WebView2 Runtime (request-window, design D4 and D7). The agent and
/// the service read the version <c>pv</c> from the 32-bit view of <see cref="ClientKey"/> and build the folder below
/// the Program Files (x86) known folder. A user variable never gives the folder.
/// </summary>
public static class WebView2RuntimeLocation
{
    /// <summary>The key of the Evergreen runtime below HKLM, in the 32-bit registry view. Only administrators can write it.</summary>
    public const string ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public const string VersionValueName = "pv";

    public const string BrowserExeName = "msedgewebview2.exe";

    private static readonly Regex VersionPattern = new(@"^[0-9]{1,9}(\.[0-9]{1,9}){3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a real runtime version: four numbers with dots, not <c>0.0.0.0</c>. An empty value, <c>..</c>, a path
    /// separator or any other character gives false.
    /// </summary>
    public static bool IsInstalledVersion(string? pv) =>
        pv is not null && VersionPattern.IsMatch(pv) && pv != "0.0.0.0";

    /// <summary>
    /// The runtime folder <c>&lt;programFilesX86&gt;\Microsoft\EdgeWebView\Application\&lt;pv&gt;</c>, or null if the
    /// version is not valid or the base folder is not an absolute Windows path.
    /// </summary>
    public static string? RuntimeFolder(string? programFilesX86, string? pv)
    {
        if (!IsInstalledVersion(pv) || !IsAbsoluteWindowsFolder(programFilesX86)) return null;
        return programFilesX86!.TrimEnd('\\') + @"\Microsoft\EdgeWebView\Application\" + pv;
    }

    /// <summary>The path of <see cref="BrowserExeName"/> in <paramref name="runtimeFolder"/>.</summary>
    public static string BrowserExePath(string runtimeFolder) => runtimeFolder.TrimEnd('\\') + @"\" + BrowserExeName;

    // A drive path such as C:\Program Files (x86). A UNC path or a relative path is refused.
    private static bool IsAbsoluteWindowsFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length >= 3
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && !path.Contains("..", StringComparison.Ordinal)
        && path.IndexOf('/') < 0;
}
