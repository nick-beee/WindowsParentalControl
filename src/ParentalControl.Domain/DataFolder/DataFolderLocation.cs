namespace ParentalControl.Domain.DataFolder;

/// <summary>Where the location of the data folder came from.</summary>
public enum DataFolderLocationSource
{
    /// <summary>The registry value is absent: the default folder <c>&lt;root&gt;\ParentalControl</c>.</summary>
    Default,
    /// <summary>The registry value names the folder.</summary>
    Registry,
}

/// <param name="Folder">The full path of the data folder, or null if the location is not usable.</param>
/// <param name="Error">Why the location is not usable, or null.</param>
public sealed record DataFolderLocationReading(string? Folder, DataFolderLocationSource Source, string? Error)
{
    public bool IsUsable => Folder is not null && Error is null;
}

/// <summary>
/// The rules for the location of the data folder (require-admin-sign-in, D14). The location is in an admin-only
/// registry value. A missing value means the default folder <c>&lt;root&gt;\ParentalControl</c>. The only other
/// accepted folder is a new folder that the service made: <c>&lt;root&gt;\ParentalControl-&lt;32 hex digits&gt;</c>.
/// <c>&lt;root&gt;</c> is <c>C:\ProgramData</c> on a normal PC. Paths use the Windows separator <c>\</c>.
/// </summary>
public static class DataFolderLocation
{
    public const string DefaultName = "ParentalControl";
    public const string FallbackPrefix = DefaultName + "-";
    /// <summary>The number of random bytes in the name of a new folder (128 bits).</summary>
    public const int RandomBytes = 16;

    public static string DefaultFolder(string root) => Join(root, DefaultName);

    /// <summary>The name of a new folder from <see cref="RandomBytes"/> random bytes, for example <c>ParentalControl-0f3a…</c>.</summary>
    public static string FallbackName(ReadOnlySpan<byte> random)
    {
        if (random.Length != RandomBytes)
            throw new ArgumentException($"A new folder name needs {RandomBytes} random bytes.", nameof(random));
        return FallbackPrefix + Convert.ToHexString(random).ToLowerInvariant();
    }

    public static string FallbackFolder(string root, ReadOnlySpan<byte> random) => Join(root, FallbackName(random));

    /// <summary>True if <paramref name="name"/> is <c>ParentalControl-</c> and 32 hex digits.</summary>
    public static bool IsFallbackName(string name)
    {
        if (!name.StartsWith(FallbackPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var hex = name.AsSpan(FallbackPrefix.Length);
        if (hex.Length != RandomBytes * 2)
            return false;
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }
        return true;
    }

    /// <summary>True if the folder is the default folder or a new folder of the service, directly in <paramref name="root"/>.</summary>
    public static bool IsAccepted(string folder, string root)
    {
        var prefix = root.TrimEnd('\\') + "\\";
        if (!folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var name = folder.Substring(prefix.Length);
        return string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase) || IsFallbackName(name);
    }

    /// <summary>
    /// The data folder from the registry. Each component that opens the database or a log file uses this.
    /// </summary>
    /// <param name="keyError">Null if the registry key is trusted (or absent), else why it is not trusted.</param>
    /// <param name="value">The registry value, or null if it is absent.</param>
    public static DataFolderLocationReading Resolve(string? keyError, string? value, string root)
    {
        if (keyError is not null)
            return new DataFolderLocationReading(null, DataFolderLocationSource.Registry, "The registry key of Parental Control is not trusted: " + keyError);
        if (value is null)
            return new DataFolderLocationReading(DefaultFolder(root), DataFolderLocationSource.Default, null);
        if (!IsAccepted(value, root))
            return new DataFolderLocationReading(null, DataFolderLocationSource.Registry,
                $"The registry value of the data folder is not accepted: \"{value}\". Only {DefaultFolder(root)} or {Join(root, FallbackPrefix)}<32 hex digits> is accepted.");
        return new DataFolderLocationReading(value, DataFolderLocationSource.Registry, null);
    }

    private static string Join(string root, string name) => root.TrimEnd('\\') + "\\" + name;
}
