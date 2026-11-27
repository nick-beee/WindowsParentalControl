using System.Text.Json;
using System.Text.RegularExpressions;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.Web;

namespace ParentalControl.Domain.Tests.Web;

/// <summary>
/// The data of the child request page (web/admin/src/request, ADR 0009). The page has no texts of its own, so these
/// tests check the texts that the service fills, and that the page uses only keys of <see cref="RequestPageTexts"/>.
/// </summary>
public class RequestPageModelTests
{
    private const string RequestDir = "web/admin/src/request";

    private static RequestPageInput Input(string preset = "de") => new(
        Preset: preset,
        Username: "Kid",
        RemainingSeconds: 3600,
        Curfew: "08:00 – 20:00",
        IsOffline: false,
        RequestsToday: 1,
        MaxRequests: 3,
        HasPending: false,
        LatestStatus: "DECLINED",
        DeclineReason: "Erst die Hausaufgaben {status}",
        DeclineResolvedAt: new DateTime(2026, 11, 26, 18, 5, 0),
        IsAdminTesting: false,
        AvailableUsers: new[] { "Kid" },
        SessionActive: true,
        SessionLocked: false,
        Week: Enum.GetValues<DayOfWeek>()
            .Select(d => new RequestPageDay(d, 120, "08:00 – 20:00", d == DayOfWeek.Saturday, d == DayOfWeek.Monday, d == DayOfWeek.Monday))
            .OrderBy(d => ((int)d.Day + 6) % 7)
            .ToList(),
        DefaultMinutes: 90,
        DefaultStart: new TimeOnly(8, 0),
        DefaultEnd: new TimeOnly(20, 0),
        Today: new DateOnly(2026, 11, 30));

    [Fact]
    public void GermanPage()
    {
        var model = RequestPageModel.Build(Input());

        Assert.Equal("de", model.Lang);
        Assert.Equal("Deine Restzeit heute", model.Texts["timer_label"]);
        Assert.Equal("Montag", model.Days[0].DayName);
        Assert.Equal("Mo", model.Days[0].DayCode);
        Assert.True(model.Days[0].IsToday);
        Assert.Equal("Ausnahme für heute", model.Days[0].ExceptionNote);
        Assert.Null(model.Days[1].ExceptionNote);
        Assert.Equal("120 Minuten (2 Stunden)", model.Days[0].MinutesText);
        Assert.Equal(new[] { "+15 Minuten", "+30 Minuten", "+1 Stunde" }, model.ExtensionOptions.Select(o => o.Label));
        Assert.Equal(new[] { 15, 30, 60 }, model.ExtensionOptions.Select(o => o.Minutes));
        Assert.Equal("90 Minuten (1 Stunde 30 Minuten)", model.ScheduleOptions[1].Label);
        Assert.Equal("Standard: 90 Minuten (08:00–20:00 Uhr)", model.Texts["schedule_default"]);
    }

    [Fact]
    public void TheServiceFillsTheNumbersAndKeepsTheStatusPlaceholder()
    {
        var texts = RequestPageModel.Build(Input()).Texts;
        Assert.Equal("ℹ️ Du hast heute 1 von 3 Anfragen benutzt (letzter Status: {status}).", texts["request_used"]);
        Assert.Equal("ℹ️ Du hast heute schon 3 Anfragen gesendet. Mehr geht heute nicht (letzter Status: {status}).", texts["request_max_reached"]);
        Assert.Equal("Deine Eltern haben abgelehnt (18:05)", texts["decline_title"]);
        Assert.Contains("{status}", texts["request_pending"]);
    }

    [Fact]
    public void TheTextsHaveEachKeyOfTheCatalog()
    {
        foreach (var preset in LanguagePreset.All)
        {
            var model = RequestPageModel.Build(Input(preset));
            Assert.Equal(preset, model.Lang);
            Assert.Equal(RequestPageTexts.For(preset).Keys.OrderBy(k => k), model.Texts.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void AnUnknownPresetGivesGerman() => Assert.Equal("de", RequestPageModel.Build(Input("fr")).Lang);

    [Theory]
    [InlineData("DECLINED", "Nein", "Nein")]
    [InlineData("declined", "Nein", "Nein")]
    [InlineData("DECLINED", "  ", null)]
    [InlineData("APPROVED", "Nein", null)]
    [InlineData("PENDING", null, null)]
    public void TheDeclineReasonOnlyForADecline(string status, string? reason, string? expected)
    {
        var model = RequestPageModel.Build(Input() with { LatestStatus = status, DeclineReason = reason });
        Assert.Equal(expected, model.DeclineReason);
        if (expected is null)
            Assert.Equal("Deine Eltern haben abgelehnt", model.Texts["decline_title"]);
    }

    [Theory]
    [InlineData(false, false, RequestPageModel.SessionInactive, false)]
    [InlineData(true, true, RequestPageModel.SessionLocked, false)]
    [InlineData(true, false, RequestPageModel.SessionActive, true)]
    public void SessionState(bool active, bool locked, string state, bool ticking)
    {
        var model = RequestPageModel.Build(Input() with { SessionActive = active, SessionLocked = locked });
        Assert.Equal(state, model.SessionState);
        Assert.Equal(ticking, model.Ticking);
    }

    [Fact]
    public void TheAdminTestBannerHasSwitchLinksOnlyForTwoOrMoreUsers()
    {
        Assert.Null(RequestPageModel.Build(Input()).AdminTest);
        var one = RequestPageModel.Build(Input() with { IsAdminTesting = true });
        Assert.Empty(one.AdminTest!.Users);
        Assert.Contains("{user}", one.AdminTest.Text);
        var two = RequestPageModel.Build(Input() with { IsAdminTesting = true, AvailableUsers = new[] { "Kid", "Kid2" } });
        Assert.Equal(new[] { "Kid", "Kid2" }, two.AdminTest!.Users);
    }

    [Fact]
    public void TheDatesOfTheScheduleForm()
    {
        var dates = RequestPageModel.Build(Input()).Dates;
        Assert.Equal(new RequestPageDates("2026-11-30", "2026-12-01", "2026-12-07"), dates);
    }

    [Fact]
    public void TheJsonHasTheNamesThatThePageReads()
    {
        using var doc = JsonDocument.Parse(RequestPageModel.Build(Input()).ToJson());
        var dataTs = File.ReadAllText(Path.Combine(RepoRoot(), RequestDir, "data.ts"));
        foreach (var property in doc.RootElement.EnumerateObject())
            Assert.Matches(new Regex($@"\n  {property.Name}: "), dataTs);
        foreach (var property in doc.RootElement.GetProperty("days")[0].EnumerateObject())
            Assert.Matches(new Regex($@"\n  {property.Name}: "), dataTs);
        Assert.Equal("Kid", doc.RootElement.GetProperty("username").GetString());
        Assert.Equal("Montag", doc.RootElement.GetProperty("days")[0].GetProperty("dayName").GetString());
    }

    [Fact]
    public void ThePageUsesOnlyKeysOfTheCatalog()
    {
        // A text key is the argument of t() (t('heading')) or a snake_case string (key: 'request_used'), except the
        // request type of /api/request. Each key must be a text of the server catalog.
        var keyUse = new Regex(@"\bt\('(?<key>[a-z_]+)'\)|'(?<key>[a-z]+(?:_[a-z]+)+)'");
        var catalog = RequestPageTexts.For("de").Keys.ToHashSet(StringComparer.Ordinal);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), RequestDir), "*.ts*")
                     .Where(p => !Regex.IsMatch(p, @"\.test\.tsx?$")))
        {
            foreach (Match m in keyUse.Matches(File.ReadAllText(path)))
                used.Add(m.Groups["key"].Value);
        }

        used.Remove("schedule_change");
        Assert.Contains("heading", used);
        Assert.Contains("request_used", used);
        Assert.Contains("ext_failed", used);
        Assert.Empty(used.Where(k => !catalog.Contains(k)));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, RequestDir)))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Cannot find {RequestDir} above {AppContext.BaseDirectory}.");
    }
}
