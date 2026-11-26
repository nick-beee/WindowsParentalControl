using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParentalControl.Domain.Updates;
using Serilog;

namespace ParentalControl.Core.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string? PublishedAt,
    string? DownloadUrl,
    long? FileSizeBytes,
    string? Error = null);

public static class UpdateService
{
    private static readonly ILogger _logger = Log.ForContext(typeof(UpdateService));
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly HttpClient DownloadHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly SemaphoreSlim ApplyLock = new(1, 1);

    private static string LatestReleaseUrl => $"https://api.github.com/repos/{AppVersion.GitHubRepo}/releases/latest";
    static UpdateService()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "WindowsParentalControl-Updater");
        HttpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        DownloadHttpClient.DefaultRequestHeaders.Add("User-Agent", "WindowsParentalControl-Updater");
    }
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVerStr = AppVersion.Current;
        try
        {
            var response = await HttpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.Information("No published releases found on GitHub repo {Repo} (404 NotFound).", AppVersion.GitHubRepo);
                    var clean = currentVerStr.TrimStart('v', 'V').Trim();
                    return new UpdateCheckResult(
                        false,
                        clean,
                        clean,
                        "Versi Terbaru",
                        "Belum ada rilis baru yang dipublikasikan di repositori GitHub.",
                        null,
                        null,
                        null,
                        Error: null);
                }

                var err = $"GitHub API returned {response.StatusCode}";
                _logger.Warning("Update check failed: {Error}", err);
                return new UpdateCheckResult(false, currentVerStr, currentVerStr, "", "", null, null, null, err);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonNode.Parse(json);
            if (doc == null)
            {
                return new UpdateCheckResult(false, currentVerStr, currentVerStr, "", "", null, null, null, "Failed to parse release metadata.");
            }

            var tagName = doc["tag_name"]?.ToString() ?? "";
            var releaseTitle = doc["name"]?.ToString() ?? tagName;
            var body = doc["body"]?.ToString() ?? "";
            var publishedAt = doc["published_at"]?.ToString();

            var cleanLatest = tagName.TrimStart('v', 'V').Trim();
            var cleanCurrent = currentVerStr.TrimStart('v', 'V').Trim();

            var hasUpdate = IsNewerVersion(cleanLatest, cleanCurrent);

            // Only an installer that the update can install (same rules as the apply step).
            var selection = UpdateReleasePolicy.Select(json, AppVersion.GitHubRepo, AppVersion.Current);
            if (hasUpdate && !selection.IsAccepted)
            {
                _logger.Warning("Release {Tag} has no installable installer: {Reason}", tagName, selection.Refusal);
            }
            var downloadUrl = selection.Candidate?.Installer.DownloadUrl;
            long? fileSize = selection.Candidate?.Installer.Size;

            return new UpdateCheckResult(
                hasUpdate,
                cleanCurrent,
                cleanLatest,
                releaseTitle,
                body,
                publishedAt,
                downloadUrl,
                fileSize);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Exception while checking for updates from GitHub Releases");
            return new UpdateCheckResult(false, currentVerStr, currentVerStr, "", "", null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Reads the latest release of <see cref="AppVersion.GitHubRepo"/>, downloads its installer and
    /// checks the SHA-256 that the release publishes. The caller gives no URL: the service and the
    /// Admin app install only what <see cref="UpdateReleasePolicy.Select"/> accepts.
    /// The returned handle is open with <see cref="FileShare.Read"/>: while the caller holds it,
    /// no other process can change, rename or delete the checked file.
    /// </summary>
    private static async Task<(bool Success, string? InstallerPath, FileStream? Handle, string Message)> DownloadVerifiedInstallerAsync(
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        string releaseJson;
        try
        {
            using var response = await HttpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, null, $"GitHub API returned {response.StatusCode}");
            }
            releaseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read the latest release for the update");
            return (false, null, null, $"Gagal membaca rilis terbaru: {ex.Message}");
        }

        var selection = UpdateReleasePolicy.Select(releaseJson, AppVersion.GitHubRepo, AppVersion.Current);
        if (!selection.IsAccepted)
        {
            _logger.Warning("Update refused: {Reason}", selection.Refusal);
            return (false, null, null, $"Pembaruan ditolak: {selection.Refusal}");
        }

        var candidate = selection.Candidate!;
        string installerPath;
        try
        {
            var checksumText = await DownloadTextAsync(candidate.Checksum.DownloadUrl, UpdateReleasePolicy.MaxChecksumBytes, cancellationToken);
            var expectedHash = UpdateReleasePolicy.ParseChecksumFile(checksumText, UpdateReleasePolicy.InstallerFileName);
            if (expectedHash is null)
            {
                _logger.Warning("Update refused: the checksum file of {Tag} is not valid", candidate.Tag);
                return (false, null, null, $"Pembaruan ditolak: {UpdateReleasePolicy.ChecksumFileName} tidak valid.");
            }

            // A new directory with a random name for each download. The child cannot prepare it in advance.
            var tempDir = Path.Combine(Path.GetTempPath(), "ParentalControlUpdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            installerPath = Path.Combine(tempDir, UpdateReleasePolicy.InstallerFileName);

            _logger.Information("Downloading update {Version} from {Url} to {Path}", candidate.Version, candidate.Installer.DownloadUrl, installerPath);
            await DownloadFileAsync(candidate.Installer.DownloadUrl, installerPath, candidate.Installer.Size, progress, cancellationToken);

            // Check the hash through a handle that stays open until the installer starts.
            var handle = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool matches;
            try
            {
                matches = UpdateReleasePolicy.HashMatches(handle, expectedHash);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            if (!matches)
            {
                handle.Dispose();
                TryDelete(installerPath);
                _logger.Warning("Update refused: the SHA-256 of {Path} is not the hash of release {Tag}", installerPath, candidate.Tag);
                return (false, null, null, "Pembaruan ditolak: SHA-256 installer tidak cocok dengan rilis.");
            }

            _logger.Information("Installer {Version} downloaded and SHA-256 verified: {Path}", candidate.Version, installerPath);
            return (true, installerPath, handle, "File pembaruan berhasil diunduh dan diverifikasi.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download the update installer");
            return (false, null, null, $"Gagal mengunduh file pembaruan: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads and checks the installer of the latest release (see <see cref="DownloadVerifiedInstallerAsync"/>).
    /// For the Admin app: it releases the handle and gives the path of the checked installer.
    /// </summary>
    public static async Task<(bool Success, string? InstallerPath, string Message)> DownloadVerifiedUpdateAsync(
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var (success, path, handle, message) = await DownloadVerifiedInstallerAsync(progress, cancellationToken);
        handle?.Dispose();
        return (success, path, message);
    }

    private static async Task<string> DownloadTextAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await DownloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(stream, buffer, maxBytes, -1, null, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task DownloadFileAsync(string url, string path, long expectedBytes, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        // GitHub redirects the release URL to its download host. The SHA-256 check covers that step.
        using var response = await DownloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyWithLimitAsync(stream, fileStream, UpdateReleasePolicy.MaxInstallerBytes, expectedBytes, progress, cancellationToken);
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long maxBytes, long totalBytes, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidDataException($"The download is larger than {maxBytes} bytes.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (totalBytes > 0 && progress != null)
            {
                progress.Report((int)Math.Min(100, totalRead * 100 / totalBytes));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public static (bool Success, string Message) LaunchInstaller(string installerPath, bool silent = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                return (false, "File installer tidak ditemukan.");
            }

            _logger.Information("Launching update installer (Silent={Silent}): {Path}", silent, installerPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : "",
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(startInfo);
            return (true, "Installer berhasil dijalankan.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to launch update installer");
            return (false, $"Gagal menjalankan installer: {ex.Message}");
        }
    }

    /// <summary>
    /// The update of the service (web admin): reads the latest release of <see cref="AppVersion.GitHubRepo"/>,
    /// downloads and checks its installer, and runs it. It takes no URL from the caller.
    /// </summary>
    public static async Task<(bool Success, string Message)> ApplyLatestUpdateAsync(bool silent = true, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await ApplyLock.WaitAsync(0, cancellationToken))
        {
            return (false, "Pembaruan sedang berjalan.");
        }

        try
        {
            var (downloaded, installerPath, handle, msg) = await DownloadVerifiedInstallerAsync(progress, cancellationToken);
            if (!downloaded || string.IsNullOrWhiteSpace(installerPath) || handle is null)
            {
                handle?.Dispose();
                return (false, msg);
            }

            (bool launched, string launchMsg) result;
            using (handle)
            {
                // The handle stays open until the process starts: the started file is the checked file.
                result = LaunchInstaller(installerPath, silent);
            }

            if (!result.launched)
            {
                return (false, result.launchMsg);
            }

            return (true, silent
                ? "Pembaruan berhasil diunduh dan instalasi otomatis telah dimulai."
                : "Pembaruan berhasil diunduh dan wizard instalasi telah dibuka.");
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    /// <summary>True only if both are strict versions (X.Y.Z, optional "v") and <paramref name="latest"/> is above <paramref name="current"/>.</summary>
    public static bool IsNewerVersion(string latest, string current) => UpdateReleasePolicy.IsNewer(latest, current);
}
