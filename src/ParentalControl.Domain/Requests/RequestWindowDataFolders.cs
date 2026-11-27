namespace ParentalControl.Domain.Requests;

/// <summary>
/// The data folders of the Request Window (request-window, design D5, Review 1 N7). Each open uses a new, empty
/// subfolder with a GUID name below <c>%LOCALAPPDATA%\ParentalControl\WebView2</c>. At each open the agent deletes each
/// old subfolder that it can delete; a folder that a browser process still holds goes at a later open.
/// </summary>
public static class RequestWindowDataFolders
{
    /// <summary>The path of the parent folder below the local application data folder.</summary>
    public const string RelativeRoot = @"ParentalControl\WebView2";

    /// <summary>A new subfolder name: a GUID with 32 hex digits.</summary>
    public static string NewName() => Guid.NewGuid().ToString("N");

    /// <summary>True only for a name that <see cref="NewName"/> can give. The agent deletes only such folders.</summary>
    public static bool IsDataFolderName(string? name) =>
        name is { Length: 32 } && Guid.TryParseExact(name, "N", out _);

    /// <summary>The old subfolders to delete before an open: each GUID name except <paramref name="current"/>.</summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<string> subfolderNames, string? current) =>
        subfolderNames
            .Where(IsDataFolderName)
            .Where(name => !string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
