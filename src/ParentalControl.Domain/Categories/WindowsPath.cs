namespace ParentalControl.Domain.Categories;

/// <summary>
/// Canonical form of Windows paths, as pure string logic.
/// System.IO.Path is not used: on the Mac it does not know "\" or drive letters,
/// so the tests would not show the Windows behaviour.
/// </summary>
public static class WindowsPath
{
    /// <summary>
    /// Returns the canonical form of an absolute Windows path: "\" separators, no "." or ".." parts,
    /// no empty parts, no "\\?\" prefix, no trailing "\". A ".." part never goes above the root.
    /// Returns null if the path is not an absolute drive path (C:\...) or UNC path (\\server\share\...).
    /// The letter case does not change. Compare canonical paths with OrdinalIgnoreCase.
    /// </summary>
    public static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.IndexOf('\0') >= 0)
            return null;

        // Long-path and device prefixes.
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                 p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];

        string root;
        string rest;
        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
        {
            root = char.ToUpperInvariant(p[0]) + @":";
            rest = p[3..];
        }
        else if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', 3);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0 ||
                parts[0] is "." or ".." || parts[1] is "." or "..")
                return null;
            root = @"\\" + parts[0] + @"\" + parts[1];
            rest = parts.Length == 3 ? parts[2] : "";
        }
        else
        {
            return null; // relative, drive-relative (C:x) or rooted without a drive (\x)
        }

        var stack = new List<string>();
        foreach (var segment in rest.Split('\\'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }

        return stack.Count == 0 ? root : root + @"\" + string.Join('\\', stack);
    }

    /// <summary>The canonical form of a folder, with a trailing "\". Null if the path is not absolute.</summary>
    public static string? CanonicalizeFolder(string? folder)
    {
        var c = Canonicalize(folder);
        return c is null ? null : c + @"\";
    }

    /// <summary>The last part of a path after "\" or "/". The input can be a bare file name.</summary>
    public static string GetFileName(string path)
    {
        var p = path.Trim().Trim('"');
        var i = p.LastIndexOfAny(['\\', '/']);
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>True if the canonical path is below the canonical folder (which ends with "\").</summary>
    public static bool IsBelow(string canonicalPath, string canonicalFolder) =>
        canonicalPath.StartsWith(canonicalFolder, StringComparison.OrdinalIgnoreCase);
}
