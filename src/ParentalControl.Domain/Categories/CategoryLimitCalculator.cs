namespace ParentalControl.Domain.Categories;

public enum LimitWarning
{
    None,
    FiveMinutes,
    OneMinute,
}

/// <summary>
/// The state of one limit on one day: a Category Limit (<see cref="Rule"/> is null) or an App Limit.
/// <see cref="LimitMinutes"/> includes today's Bonus Minutes. <see cref="UsedMinutes"/> is the Category Usage
/// of the category, because an App Limit shares the time of its category.
/// </summary>
public sealed record LimitStatus(AppCategory Category, AppRule? Rule, int LimitMinutes, int UsedMinutes)
{
    public bool IsAppLimit => Rule is not null;

    public int RemainingMinutes => Math.Max(0, LimitMinutes - UsedMinutes);

    public bool IsReached => UsedMinutes >= LimitMinutes;

    /// <summary>
    /// The warning that is due now: 5 minutes while 2 to 5 minutes remain, 1 minute while 1 minute remains.
    /// The caller sends each warning at most one time per limit and day.
    /// </summary>
    public LimitWarning DueWarning => RemainingMinutes switch
    {
        <= 0 => LimitWarning.None,
        1 => LimitWarning.OneMinute,
        <= 5 => LimitWarning.FiveMinutes,
        _ => LimitWarning.None,
    };

    /// <summary>A stable key for the deduplication of warnings, for example "cat:Games" or "rule:12".</summary>
    public string Key => Rule is null ? $"cat:{Category}" : $"rule:{Rule.Id}";
}

/// <summary>The limits of one Restricted User on one day.</summary>
public sealed class DayLimits
{
    private readonly Dictionary<AppCategory, LimitStatus> _categoryLimits;
    private readonly Dictionary<long, LimitStatus> _appLimits;

    internal DayLimits(Dictionary<AppCategory, LimitStatus> categoryLimits, Dictionary<long, LimitStatus> appLimits)
    {
        _categoryLimits = categoryLimits;
        _appLimits = appLimits;
    }

    /// <summary>All limits that apply today: the Category Limits first, then the App Limits.</summary>
    public IReadOnlyList<LimitStatus> All =>
        _categoryLimits.Values.OrderBy(s => s.Category)
            .Concat(_appLimits.Values.OrderBy(s => s.Rule!.Id))
            .ToList();

    public LimitStatus? CategoryLimit(AppCategory category) => _categoryLimits.GetValueOrDefault(category);

    /// <summary>
    /// The limit of an exe today: its App Limit if its rule has one for today, else the Category Limit
    /// of its category. Null if the exe has no limit today (System always has none).
    /// </summary>
    public LimitStatus? LimitFor(CategoryResolution resolution)
    {
        if (resolution.Category == AppCategory.System)
            return null;
        if (resolution.Rule is { } rule && _appLimits.TryGetValue(rule.Id, out var appLimit))
            return appLimit;
        return _categoryLimits.GetValueOrDefault(resolution.Category);
    }

    /// <summary>True if the exe has no time left today.</summary>
    public bool IsExhausted(CategoryResolution resolution) => LimitFor(resolution)?.IsReached ?? false;
}

/// <summary>
/// Calculates the Category Limits and App Limits of a day: the weekend value on Saturday and Sunday,
/// the weekday value on the other days, plus today's Bonus Minutes.
/// </summary>
public sealed class CategoryLimitCalculator
{
    private readonly IReadOnlyDictionary<AppCategory, DailyLimit> _categoryLimits;
    private readonly IReadOnlyList<AppRule> _rules;

    public CategoryLimitCalculator(IReadOnlyDictionary<AppCategory, DailyLimit> categoryLimits, IEnumerable<AppRule> rules)
    {
        _categoryLimits = categoryLimits;
        _rules = rules.ToList();
    }

    /// <param name="date">The local date of the computer.</param>
    /// <param name="usage">Today's Category Usage in minutes. A missing category has 0 minutes.</param>
    /// <param name="bonusMinutes">Today's Bonus Minutes of the Restricted User.</param>
    public DayLimits Evaluate(DateOnly date, IReadOnlyDictionary<AppCategory, int> usage, int bonusMinutes)
    {
        var bonus = Math.Max(0, bonusMinutes);
        int Used(AppCategory c) => Math.Max(0, usage.GetValueOrDefault(c));

        var categoryLimits = new Dictionary<AppCategory, LimitStatus>();
        foreach (var (category, limit) in _categoryLimits)
        {
            if (category == AppCategory.System || limit.MinutesOn(date) is not { } minutes)
                continue;
            categoryLimits[category] = new LimitStatus(category, null, minutes + bonus, Used(category));
        }

        var appLimits = new Dictionary<long, LimitStatus>();
        foreach (var rule in _rules)
        {
            if (rule.Category == AppCategory.System || rule.AppLimit?.MinutesOn(date) is not { } minutes)
                continue;
            appLimits.TryAdd(rule.Id, new LimitStatus(rule.Category, rule, minutes + bonus, Used(rule.Category)));
        }

        return new DayLimits(categoryLimits, appLimits);
    }
}
