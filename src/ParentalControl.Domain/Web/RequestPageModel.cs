using System.Globalization;
using System.Text.Json;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Web;

/// <summary>One day of the schedule strip, as the service calculates it for the Restricted User.</summary>
public sealed record RequestPageDay(
    DayOfWeek Day,
    int Minutes,
    string TimeWindow,
    bool IsCustom,
    bool IsToday,
    bool HasException);

/// <summary>The state of the child request page (<c>/request</c>) that the service reads on each page load.</summary>
public sealed record RequestPageInput(
    string? Preset,
    string Username,
    int RemainingSeconds,
    string Curfew,
    bool IsOffline,
    int RequestsToday,
    int MaxRequests,
    bool HasPending,
    string? LatestStatus,
    string? DeclineReason,
    DateTime? DeclineResolvedAt,
    bool IsAdminTesting,
    IReadOnlyList<string> AvailableUsers,
    bool SessionActive,
    bool SessionLocked,
    IReadOnlyList<RequestPageDay> Week,
    int DefaultMinutes,
    TimeOnly DefaultStart,
    TimeOnly DefaultEnd,
    DateOnly Today);

/// <summary>
/// The data of the child request page (web/admin/request.html, ADR 0009). The service puts it as JSON into the page.
/// The page has no texts of its own: <see cref="Texts"/> is the <see cref="RequestPageTexts"/> set of the Language
/// Preset. The service fills each placeholder that needs a number or a word form. Only <c>{status}</c> stays in the
/// texts, because the page shows the status in bold.
/// </summary>
public sealed record RequestPageModel(
    string Lang,
    string Username,
    int RemainingSeconds,
    bool Ticking,
    string Curfew,
    bool IsOffline,
    int RequestsToday,
    int MaxRequests,
    bool HasPending,
    string? LatestStatus,
    string? DeclineReason,
    RequestPageAdminTest? AdminTest,
    string SessionState,
    IReadOnlyList<RequestPageDayModel> Days,
    IReadOnlyList<RequestPageOption> ExtensionOptions,
    IReadOnlyList<RequestPageOption> ScheduleOptions,
    RequestPageDates Dates,
    IReadOnlyDictionary<string, string> Texts)
{
    public const string SessionInactive = "inactive";
    public const string SessionLocked = "locked";
    public const string SessionActive = "active";

    /// <summary>The minutes of the extension options (+15, +30, +60). The first one is selected.</summary>
    public static readonly int[] ExtensionMinutes = { 15, 30, 60 };

    /// <summary>The minutes of the schedule options. 120 is selected.</summary>
    public static readonly int[] ScheduleMinutes = { 60, 90, 120, 150, 180, 240 };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RequestPageModel Build(RequestPageInput input)
    {
        var texts = RequestPageTexts.For(input.Preset);
        var sessionTexts = SessionTexts.For(input.Preset);

        var declined = string.Equals(input.LatestStatus, "DECLINED", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(input.DeclineReason);
        var declineTime = declined && input.DeclineResolvedAt is { } resolved
            ? $" ({resolved.ToString("HH:mm", CultureInfo.InvariantCulture)})"
            : "";

        // The texts with a number or a word form. {status} has no value here, so TextTemplate keeps it.
        var pageTexts = texts.Keys.ToDictionary(k => k, k => texts[k], StringComparer.Ordinal);
        pageTexts["request_used"] = texts.Format("request_used", ("count", input.RequestsToday), ("max", input.MaxRequests));
        pageTexts["request_max_reached"] = texts.Format("request_max_reached", ("max", input.MaxRequests));
        pageTexts["decline_title"] = texts.Format("decline_title", ("time", declineTime));
        pageTexts["schedule_default"] = texts.Format("schedule_default",
            ("minutes", input.DefaultMinutes),
            ("start", input.DefaultStart.ToString("HH:mm", CultureInfo.InvariantCulture)),
            ("end", input.DefaultEnd.ToString("HH:mm", CultureInfo.InvariantCulture)));

        var days = input.Week.Select(d => new RequestPageDayModel(
            DayName: SessionTexts.DayName(sessionTexts, d.Day),
            DayCode: RequestPageTexts.DayCode(texts, d.Day),
            MinutesText: RequestPageTexts.MinutesText(texts, sessionTexts, d.Minutes),
            TimeWindow: d.TimeWindow,
            IsCustom: d.IsCustom,
            IsToday: d.IsToday,
            HasException: d.HasException,
            ExceptionNote: d.HasException && d.IsToday ? texts["exception_today"] : null)).ToList();

        return new RequestPageModel(
            Lang: texts.Preset,
            Username: input.Username,
            RemainingSeconds: input.RemainingSeconds,
            Ticking: input.SessionActive && !input.SessionLocked,
            Curfew: input.Curfew,
            IsOffline: input.IsOffline,
            RequestsToday: input.RequestsToday,
            MaxRequests: input.MaxRequests,
            HasPending: input.HasPending,
            LatestStatus: input.LatestStatus,
            DeclineReason: declined ? input.DeclineReason : null,
            AdminTest: input.IsAdminTesting ? RequestPageAdminTest.For(input.AvailableUsers) : null,
            SessionState: !input.SessionActive ? SessionInactive : input.SessionLocked ? SessionLocked : SessionActive,
            Days: days,
            ExtensionOptions: ExtensionMinutes.Select(m => new RequestPageOption(m, "+" + SessionTexts.Duration(sessionTexts, m))).ToList(),
            ScheduleOptions: ScheduleMinutes.Select(m => new RequestPageOption(m, RequestPageTexts.MinutesText(texts, sessionTexts, m))).ToList(),
            Dates: new RequestPageDates(
                Min: Iso(input.Today),
                Tomorrow: Iso(input.Today.AddDays(1)),
                Max: Iso(input.Today.AddDays(7))),
            Texts: pageTexts);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <param name="ExceptionNote">The note of an exception for today, else null.</param>
public sealed record RequestPageDayModel(
    string DayName,
    string DayCode,
    string MinutesText,
    string TimeWindow,
    bool IsCustom,
    bool IsToday,
    bool HasException,
    string? ExceptionNote);

/// <summary>An option of a request form: the minutes that the page sends and the label in the preset.</summary>
public sealed record RequestPageOption(int Minutes, string Label);

/// <summary>The dates of the schedule form (yyyy-MM-dd, local date of the service).</summary>
public sealed record RequestPageDates(string Min, string Tomorrow, string Max);

/// <summary>
/// The banner of the admin test mode: the page shows a Restricted User to a parent. Only the parent sees it, so its
/// texts are Indonesian and do not follow the preset, as before the move to React (add-german-child-texts).
/// <see cref="Text"/> has the placeholder <c>{user}</c>.
/// </summary>
public sealed record RequestPageAdminTest(string Title, string Text, string SwitchLabel, IReadOnlyList<string> Users)
{
    public static RequestPageAdminTest For(IReadOnlyList<string> users) => new(
        Title: "Mode Uji Coba Administrator",
        Text: "Menampilkan status akun terbatas {user}.",
        SwitchLabel: "Ganti Akun Pengujian:",
        // The page shows the switch links only for two or more users.
        Users: users.Count > 1 ? users : Array.Empty<string>());
}
