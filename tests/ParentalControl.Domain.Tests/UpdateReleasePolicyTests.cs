using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParentalControl.Domain.Updates;

namespace ParentalControl.Domain.Tests;

public class UpdateReleasePolicyTests
{
    private const string Repo = "nick-beee/WindowsParentalControl";
    private const string Base = "https://github.com/nick-beee/WindowsParentalControl/releases/download";

    private static string ReleaseJson(
        string tag = "v1.6.0",
        bool draft = false,
        bool prerelease = false,
        params (string Name, string Url, long Size)[] assets)
    {
        if (assets.Length == 0)
        {
            assets =
            [
                ("ParentalControlSetup.exe", $"{Base}/{tag}/ParentalControlSetup.exe", 50_000_000),
                ("ParentalControlSetup.exe.sha256", $"{Base}/{tag}/ParentalControlSetup.exe.sha256", 91),
            ];
        }

        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = a.Size }),
        });
    }

    // ── Versions ──

    [Theory]
    [InlineData("1.6.0", 1, 6, 0)]
    [InlineData("v1.6.0", 1, 6, 0)]
    [InlineData("V2.0.10", 2, 0, 10)]
    [InlineData(" v1.5.1 ", 1, 5, 1)]
    public void TryParseVersion_reads_a_strict_version(string text, int major, int minor, int build)
    {
        Assert.True(UpdateReleasePolicy.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zzz")]
    [InlineData("1.6")]
    [InlineData("1.6.0.0")]
    [InlineData("1.6.0-beta")]
    [InlineData("vv1.6.0")]
    [InlineData("1.6.x")]
    public void TryParseVersion_refuses_other_text(string? text) =>
        Assert.False(UpdateReleasePolicy.TryParseVersion(text, out _));

    [Theory]
    [InlineData("1.5.1", "1.5.0", true)]
    [InlineData("v1.6.0", "1.5.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.4.9", "1.5.0", false)]
    [InlineData("zzz", "1.5.0", false)]
    [InlineData("1.6.0", "garbage", false)]
    public void IsNewer_compares_strict_versions(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateReleasePolicy.IsNewer(latest, current));

    // ── URLs ──

    [Fact]
    public void IsAllowedDownloadUrl_accepts_the_release_url_of_the_repo() =>
        Assert.True(UpdateReleasePolicy.IsAllowedDownloadUrl(Repo, "v1.6.0", "ParentalControlSetup.exe",
            $"{Base}/v1.6.0/ParentalControlSetup.exe"));

    [Fact]
    public void IsAllowedDownloadUrl_ignores_the_case_of_the_repo_name() =>
        Assert.True(UpdateReleasePolicy.IsAllowedDownloadUrl(Repo, "v1.6.0", "ParentalControlSetup.exe",
            "https://GitHub.com/Nick-Beee/windowsparentalcontrol/releases/download/v1.6.0/ParentalControlSetup.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ParentalControlSetup.exe")]
    [InlineData("http://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("ftp://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("file:///C:/evil/ParentalControlSetup.exe")]
    [InlineData("https://github.com.evil.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://evil.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com@evil.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://user@github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com:8443/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://objects.githubusercontent.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com/DarkTama/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com/evil/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.5.9/ParentalControlSetup.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/Other.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe?x=1")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe#x")]
    [InlineData("https://github.com/evil/repo/releases/download/x/../../../nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/../../../../evil/repo/raw/ParentalControlSetup.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0%2F..%2F..%2Fx/ParentalControlSetup.exe")]
    [InlineData("https://github.com\\@evil.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe")]
    [InlineData("https://github.com/nick-beee/WindowsParentalControl/releases/download/v1.6.0/ParentalControlSetup.exe/extra")]
    public void IsAllowedDownloadUrl_refuses_other_urls(string? url) =>
        Assert.False(UpdateReleasePolicy.IsAllowedDownloadUrl(Repo, "v1.6.0", "ParentalControlSetup.exe", url));

    [Theory]
    [InlineData("..")]
    [InlineData("v1/../x")]
    [InlineData("")]
    public void IsAllowedDownloadUrl_refuses_a_tag_that_is_not_one_segment(string tag) =>
        Assert.False(UpdateReleasePolicy.IsAllowedDownloadUrl(Repo, tag, "ParentalControlSetup.exe",
            $"{Base}/{tag}/ParentalControlSetup.exe"));

    // ── Select ──

    [Fact]
    public void Select_accepts_a_newer_release_with_installer_and_checksum()
    {
        var sel = UpdateReleasePolicy.Select(ReleaseJson(), Repo, "1.5.0");

        Assert.True(sel.IsAccepted, sel.Refusal);
        Assert.Equal("v1.6.0", sel.Candidate!.Tag);
        Assert.Equal("1.6.0", sel.Candidate.Version);
        Assert.Equal($"{Base}/v1.6.0/ParentalControlSetup.exe", sel.Candidate.Installer.DownloadUrl);
        Assert.Equal($"{Base}/v1.6.0/ParentalControlSetup.exe.sha256", sel.Candidate.Checksum.DownloadUrl);
    }

    [Theory]
    [InlineData("v1.5.0")]
    [InlineData("v1.4.9")]
    [InlineData("latest")]
    [InlineData("v1.6.0-rc1")]
    public void Select_refuses_a_tag_that_is_not_a_higher_version(string tag) =>
        Assert.False(UpdateReleasePolicy.Select(ReleaseJson(tag), Repo, "1.5.0").IsAccepted);

    [Fact]
    public void Select_refuses_a_draft_or_a_prerelease()
    {
        Assert.False(UpdateReleasePolicy.Select(ReleaseJson(draft: true), Repo, "1.5.0").IsAccepted);
        Assert.False(UpdateReleasePolicy.Select(ReleaseJson(prerelease: true), Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_a_release_without_a_checksum_file()
    {
        var json = ReleaseJson(assets: [("ParentalControlSetup.exe", $"{Base}/v1.6.0/ParentalControlSetup.exe", 1000)]);
        var sel = UpdateReleasePolicy.Select(json, Repo, "1.5.0");

        Assert.False(sel.IsAccepted);
        Assert.Contains(".sha256", sel.Refusal);
    }

    [Fact]
    public void Select_refuses_a_release_without_the_installer()
    {
        var json = ReleaseJson(assets:
        [
            ("Other.exe", $"{Base}/v1.6.0/Other.exe", 1000),
            ("ParentalControlSetup.exe.sha256", $"{Base}/v1.6.0/ParentalControlSetup.exe.sha256", 91),
        ]);
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_an_installer_on_a_url_outside_the_release()
    {
        var json = ReleaseJson(assets:
        [
            ("ParentalControlSetup.exe", "https://evil.com/ParentalControlSetup.exe", 1000),
            ("ParentalControlSetup.exe.sha256", $"{Base}/v1.6.0/ParentalControlSetup.exe.sha256", 91),
        ]);
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_a_checksum_on_a_url_outside_the_release()
    {
        var json = ReleaseJson(assets:
        [
            ("ParentalControlSetup.exe", $"{Base}/v1.6.0/ParentalControlSetup.exe", 1000),
            ("ParentalControlSetup.exe.sha256", "https://evil.com/ParentalControlSetup.exe.sha256", 91),
        ]);
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_two_assets_with_the_installer_name()
    {
        var json = ReleaseJson(assets:
        [
            ("ParentalControlSetup.exe", $"{Base}/v1.6.0/ParentalControlSetup.exe", 1000),
            ("ParentalControlSetup.exe", $"{Base}/v1.6.0/ParentalControlSetup.exe", 1000),
            ("ParentalControlSetup.exe.sha256", $"{Base}/v1.6.0/ParentalControlSetup.exe.sha256", 91),
        ]);
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(UpdateReleasePolicy.MaxInstallerBytes + 1)]
    public void Select_refuses_an_installer_size_that_is_not_valid(long size)
    {
        var json = ReleaseJson(assets:
        [
            ("ParentalControlSetup.exe", $"{Base}/v1.6.0/ParentalControlSetup.exe", size),
            ("ParentalControlSetup.exe.sha256", $"{Base}/v1.6.0/ParentalControlSetup.exe.sha256", 91),
        ]);
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": 5}")]
    public void Select_refuses_bad_release_data(string json) =>
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);

    // Odd release JSON must give "no update", never an exception (issue #57).

    [Fact]
    public void Select_refuses_a_tag_with_a_lone_surrogate() =>
        Assert.False(UpdateReleasePolicy.Select("{\"tag_name\":\"\\ud800\"}", Repo, "1.5.0").IsAccepted);

    [Fact]
    public void Select_refuses_an_asset_name_with_a_lone_surrogate()
    {
        var json = "{\"tag_name\":\"v1.6.0\",\"assets\":[{\"name\":\"\\ud800\",\"browser_download_url\":\"x\",\"size\":1}]}";
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_an_asset_url_with_a_lone_surrogate()
    {
        var json = "{\"tag_name\":\"v1.6.0\",\"assets\":[{\"name\":\"ParentalControlSetup.exe\",\"browser_download_url\":\"\\udc00\",\"size\":1}]}";
        Assert.False(UpdateReleasePolicy.Select(json, Repo, "1.5.0").IsAccepted);
    }

    [Fact]
    public void Select_refuses_a_root_that_is_an_array_of_objects() =>
        Assert.False(UpdateReleasePolicy.Select("[{\"tag_name\":\"v1.6.0\"}]", Repo, "1.5.0").IsAccepted);

    [Fact]
    public void Select_refuses_when_assets_is_a_number() =>
        Assert.False(UpdateReleasePolicy.Select("{\"tag_name\":\"v1.6.0\",\"assets\":5}", Repo, "1.5.0").IsAccepted);

    [Fact]
    public void Select_refuses_when_an_asset_entry_is_a_number() =>
        Assert.False(UpdateReleasePolicy.Select("{\"tag_name\":\"v1.6.0\",\"assets\":[5]}", Repo, "1.5.0").IsAccepted);

    // ── Checksum file ──

    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Theory]
    [InlineData(Hash)]
    [InlineData(Hash + "\n")]
    [InlineData(Hash + "\r\n")]
    [InlineData(Hash + "  ParentalControlSetup.exe\n")]
    [InlineData(Hash + " *ParentalControlSetup.exe\r\n")]
    [InlineData("\uFEFF" + Hash + "  ParentalControlSetup.exe")]
    public void ParseChecksumFile_reads_the_hash(string content) =>
        Assert.Equal(Hash, UpdateReleasePolicy.ParseChecksumFile(content, "ParentalControlSetup.exe"));

    [Fact]
    public void ParseChecksumFile_gives_lower_case() =>
        Assert.Equal(Hash, UpdateReleasePolicy.ParseChecksumFile(Hash.ToUpperInvariant(), "ParentalControlSetup.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a088")]
    [InlineData("zf86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData(Hash + "  Other.exe")]
    [InlineData(Hash + "  ParentalControlSetup.exe\n" + Hash + "  Other.exe")]
    public void ParseChecksumFile_refuses_other_content(string? content) =>
        Assert.Null(UpdateReleasePolicy.ParseChecksumFile(content, "ParentalControlSetup.exe"));

    // ── Hash ──

    [Fact]
    public void HashMatches_accepts_the_correct_hash()
    {
        var bytes = Encoding.UTF8.GetBytes("installer bytes");
        var hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.True(UpdateReleasePolicy.HashMatches(new MemoryStream(bytes), hex));
        Assert.True(UpdateReleasePolicy.HashMatches(new MemoryStream(bytes), hex.ToUpperInvariant()));
    }

    [Fact]
    public void HashMatches_refuses_other_bytes()
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installer bytes")));

        Assert.False(UpdateReleasePolicy.HashMatches(new MemoryStream(Encoding.UTF8.GetBytes("evil bytes")), hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zf86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void HashMatches_refuses_a_bad_expected_hash(string hex) =>
        Assert.False(UpdateReleasePolicy.HashMatches(new MemoryStream([1, 2, 3]), hex));
}
