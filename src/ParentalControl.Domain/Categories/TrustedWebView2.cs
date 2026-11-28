namespace ParentalControl.Domain.Categories;

/// <summary>
/// The test for the Trusted WebView2 Runtime (suspend-untrusted-webview2, design D1): an exe with the file name
/// <c>msedgewebview2.exe</c> below <c>Microsoft\EdgeWebView\Application\</c> in a Program Files folder. These are the
/// folders of the Evergreen WebView2 Runtime, and only administrators can change them.
/// <para>
/// This is the only test of this kind. <see cref="ProtectedPrograms"/> (a block), <see cref="SuspensionPlanner"/>
/// (a limit) and the counting of the Session Probe (the creator of a WebView2) use the same instance. Each other
/// <c>msedgewebview2.exe</c> is an ordinary exe: a Fixed Version runtime, a per-user runtime, and a copy that the
/// child made. A path that is not in the canonical form of the folders (an 8.3 short name, a UNC path to the
/// same disk) is not trusted, so the test fails closed.
/// </para>
/// </summary>
public sealed class TrustedWebView2
{
    /// <summary>The folder of the Evergreen WebView2 Runtime below a Program Files folder.</summary>
    public const string RuntimeRelativeFolder = @"Microsoft\EdgeWebView\Application";

    private readonly List<string> _folders = [];

    /// <param name="programFilesX86">The Program Files (x86) known folder. A value that is not an absolute path is ignored.</param>
    /// <param name="programFiles">The Program Files known folder. A value that is not an absolute path is ignored.</param>
    public TrustedWebView2(string? programFilesX86, string? programFiles)
    {
        foreach (var root in new[] { programFilesX86, programFiles })
        {
            if (WindowsPath.CanonicalizeFolder(root) is not { } folder) continue;
            var app = WindowsPath.CanonicalizeFolder(folder + RuntimeRelativeFolder);
            if (app is not null && !_folders.Contains(app, StringComparer.OrdinalIgnoreCase)) _folders.Add(app);
        }
    }

    /// <summary>The runtime folders in canonical form, each with a trailing "\".</summary>
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>True if the exe is <c>msedgewebview2.exe</c> below a runtime folder, after canonicalization.</summary>
    public bool IsTrusted(string? exePath)
    {
        var canonical = WindowsPath.Canonicalize(exePath);
        return canonical is not null
               && string.Equals(WindowsPath.GetFileName(canonical), ProtectedPrograms.WebView2FileName, StringComparison.OrdinalIgnoreCase)
               && _folders.Any(f => WindowsPath.IsBelow(canonical, f));
    }
}
