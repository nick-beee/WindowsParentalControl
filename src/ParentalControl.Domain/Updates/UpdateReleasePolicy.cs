using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParentalControl.Domain.Updates;

/// <summary>One asset of a GitHub release, as the releases API gives it.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>The installer and its checksum file of a release that the updater can install.</summary>
public sealed record UpdateCandidate(string Tag, string Version, ReleaseAsset Installer, ReleaseAsset Checksum);

/// <summary>The result of <see cref="UpdateReleasePolicy.Select"/>: a candidate, or the reason for the refusal.</summary>
public sealed record UpdateSelection(UpdateCandidate? Candidate, string? Refusal)
{
    public bool IsAccepted => Candidate is not null;

    public static UpdateSelection Accept(UpdateCandidate candidate) => new(candidate, null);
    public static UpdateSelection Refuse(string reason) => new(null, reason);
}

/// <summary>
/// The rules that decide which installer the updater can run. The service runs the installer
/// as an administrator, so the installer must come from the release of our own repository,
/// with a version above the installed version and a SHA-256 that the release publishes.
/// This class has no I/O: the service gives it the release JSON and the downloaded bytes.
/// </summary>
public static class UpdateReleasePolicy
{
    /// <summary>The installer file. It must be equal to <c>OutputBaseFilename</c> in installer/ParentalControl.iss.</summary>
    public const string InstallerFileName = "ParentalControlSetup.exe";

    /// <summary>The checksum file. The release workflow (.github/workflows/release.yml) publishes it.</summary>
    public const string ChecksumFileName = InstallerFileName + ".sha256";

    /// <summary>The largest installer that the updater downloads.</summary>
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    /// <summary>The largest checksum file that the updater downloads.</summary>
    public const long MaxChecksumBytes = 1024;

    private static readonly Regex VersionPattern = new(@"^[vV]?(\d{1,9})\.(\d{1,9})\.(\d{1,9})$", RegexOptions.CultureInvariant);
    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Line = new(@"^([0-9a-fA-F]{64})(?:[ \t]+\*?(\S.*))?$", RegexOptions.CultureInvariant);

    /// <summary>Reads a strict version "1.2.3" or "v1.2.3". Any other text gives false.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null) return false;
        var m = VersionPattern.Match(text.Trim());
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    /// <summary>True only if both versions are strict versions and <paramref name="latest"/> is above <paramref name="current"/>.</summary>
    public static bool IsNewer(string? latest, string? current) =>
        TryParseVersion(latest, out var l) && TryParseVersion(current, out var c) && l > c;

    /// <summary>
    /// True if <paramref name="url"/> is exactly
    /// <c>https://github.com/{repo}/releases/download/{tag}/{assetName}</c>,
    /// with no user info, no port, no query and no fragment.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string repo, string tag, string assetName, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !RepoPattern.IsMatch(repo)) return false;
        if (!IsPlainSegment(tag) || !IsPlainSegment(assetName)) return false;
        // A raw backslash or an encoded slash or backslash can change the segments after the parse.
        if (url.Contains('\\') || url.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || url.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;

        var expected = $"/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
        // The owner and the repository name are not case-sensitive on GitHub. The tag and the asset name are.
        var repoPrefix = $"/{repo}/";
        var path = uri.AbsolutePath;
        return path.Length == expected.Length
            && path.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(path[repoPrefix.Length..], expected[repoPrefix.Length..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the JSON of <c>GET /repos/{repo}/releases/latest</c> and selects the installer.
    /// It refuses a release that is a draft or a prerelease, a tag that is not a strict version,
    /// a version that is not above <paramref name="currentVersion"/>, and a release without
    /// an installer or a checksum file on an allowed URL.
    /// </summary>
    public static UpdateSelection Select(string releaseJson, string repo, string currentVersion)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(releaseJson);
        }
        catch (JsonException)
        {
            return UpdateSelection.Refuse("The release data is not valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return UpdateSelection.Refuse("The release data is not a JSON object.");
            if (GetBool(root, "draft") || GetBool(root, "prerelease"))
                return UpdateSelection.Refuse("The release is a draft or a prerelease.");

            var tag = GetString(root, "tag_name") ?? "";
            if (!TryParseVersion(tag, out var latest))
                return UpdateSelection.Refuse("The release tag is not a version (vX.Y.Z).");
            if (!TryParseVersion(currentVersion, out var current))
                return UpdateSelection.Refuse("The installed version is not a version (X.Y.Z).");
            if (latest <= current)
                return UpdateSelection.Refuse($"The release {latest} is not above the installed version {current}.");

            var assets = ReadAssets(root);
            var installer = FindAsset(assets, repo, tag, InstallerFileName);
            if (installer is null)
                return UpdateSelection.Refuse($"The release has no {InstallerFileName} on the release URL of {repo}.");
            if (installer.Size <= 0 || installer.Size > MaxInstallerBytes)
                return UpdateSelection.Refuse($"The size of {InstallerFileName} is not valid.");

            var checksum = FindAsset(assets, repo, tag, ChecksumFileName);
            if (checksum is null)
                return UpdateSelection.Refuse($"The release has no {ChecksumFileName} on the release URL of {repo}.");
            if (checksum.Size <= 0 || checksum.Size > MaxChecksumBytes)
                return UpdateSelection.Refuse($"The size of {ChecksumFileName} is not valid.");

            return UpdateSelection.Accept(new UpdateCandidate(tag, latest.ToString(3), installer, checksum));
        }
    }

    /// <summary>
    /// Reads a checksum file: the bare hash, or the <c>sha256sum</c> form "hash  name".
    /// If the file gives a name, it must be <paramref name="expectedFileName"/>.
    /// Gives the hash in lower case, or null.
    /// </summary>
    public static string? ParseChecksumFile(string? content, string expectedFileName)
    {
        if (content is null) return null;
        var text = content.TrimStart('﻿').Trim();
        if (text.Contains('\n') || text.Contains('\r')) return null;
        var m = Sha256Line.Match(text);
        if (!m.Success) return null;
        if (m.Groups[2].Success && !string.Equals(m.Groups[2].Value.Trim(), expectedFileName, StringComparison.Ordinal))
            return null;
        return m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>Computes the SHA-256 of the stream from its current position and compares it with <paramref name="expectedHex"/>.</summary>
    public static bool HashMatches(Stream content, string expectedHex)
    {
        if (expectedHex is null || expectedHex.Length != 64) return false;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(content);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsPlainSegment(string s) =>
        !string.IsNullOrEmpty(s) && s != "." && s != ".." && s.IndexOfAny(['/', '\\', '?', '#', '%']) < 0;

    private static ReleaseAsset? FindAsset(IEnumerable<ReleaseAsset> assets, string repo, string tag, string name)
    {
        // Two assets with the same name are not possible on GitHub. If the data has them, refuse.
        var matches = assets.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) return null;
        var asset = matches[0];
        return IsAllowedDownloadUrl(repo, tag, name, asset.DownloadUrl) ? asset : null;
    }

    private static List<ReleaseAsset> ReadAssets(JsonElement root)
    {
        var list = new List<ReleaseAsset>();
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(a, "name");
            var url = GetString(a, "browser_download_url");
            if (name is null || url is null) continue;
            var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : -1;
            list.Add(new ReleaseAsset(name, url, size));
        }
        return list;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
