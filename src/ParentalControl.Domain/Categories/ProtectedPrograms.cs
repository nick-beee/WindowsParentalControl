namespace ParentalControl.Domain.Categories;

/// <summary>
/// The programs that a block never closes or suspends: each exe below the Windows folder, each exe below an
/// install folder of Parental Control, and the Trusted WebView2 Runtime (<see cref="TrustedWebView2"/>).
/// An App Rule cannot change this list. Thus a delete of a default System rule never lets a block stop
/// Explorer or the service itself. Each other <see cref="WebView2FileName"/> is an ordinary exe
/// (suspend-untrusted-webview2, design D1).
/// </summary>
public sealed class ProtectedPrograms
{
    public const string WebView2FileName = "msedgewebview2.exe";

    /// <summary>The exe file names of Parental Control. A blocked file rule for one of them is refused.</summary>
    public static IReadOnlyList<string> ParentalControlFileNames { get; } =
        ["ParentalControl.Service.exe", "ParentalControl.Agent.exe", "ParentalControl.Admin.exe"];

    private readonly List<string> _folders = [];

    /// <param name="windowsFolder">The Windows folder, for example C:\Windows.</param>
    /// <param name="installFolders">The install folders of Parental Control. A value that is not an absolute path is ignored.</param>
    /// <param name="trustedWebView2">The test for the Trusted WebView2 Runtime. The suspension and the counting use
    /// the same instance.</param>
    public ProtectedPrograms(string? windowsFolder, IEnumerable<string?> installFolders, TrustedWebView2 trustedWebView2)
    {
        TrustedWebView2 = trustedWebView2;
        foreach (var folder in installFolders.Prepend(windowsFolder))
        {
            var canonical = WindowsPath.CanonicalizeFolder(folder);
            if (canonical is not null && !_folders.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                _folders.Add(canonical);
        }
    }

    /// <summary>The test for the Trusted WebView2 Runtime.</summary>
    public TrustedWebView2 TrustedWebView2 { get; }

    /// <summary>The protected folders in canonical form, each with a trailing "\".</summary>
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>
    /// True if a block must never act on this exe. A path that is not an absolute Windows path is protected too:
    /// the service cannot decide its folder, so it does not act on it.
    /// </summary>
    public bool IsProtected(string? exePath)
    {
        var canonical = WindowsPath.Canonicalize(exePath);
        if (canonical is null)
            return true;
        if (TrustedWebView2.IsTrusted(canonical))
            return true;
        return _folders.Any(f => WindowsPath.IsBelow(canonical, f));
    }

    /// <summary>True if the canonical folder (with a trailing "\") is a protected folder or a folder below one.</summary>
    public bool IsProtectedFolder(string canonicalFolder) =>
        _folders.Any(f => WindowsPath.IsBelow(canonicalFolder, f));

    /// <summary>
    /// True if a blocked file rule for this file name is refused. A file rule for <see cref="WebView2FileName"/>
    /// matches the Trusted WebView2 Runtime too, so it stays refused; a folder rule can block an untrusted copy.
    /// </summary>
    public static bool IsProtectedFileName(string fileName) =>
        string.Equals(fileName, WebView2FileName, StringComparison.OrdinalIgnoreCase)
        || ParentalControlFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}
