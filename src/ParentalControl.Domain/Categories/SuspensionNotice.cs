namespace ParentalControl.Domain.Categories;

/// <summary>
/// The child message after a suspension because an exe has no time left. The service sends one message per
/// limit and day: <see cref="Key"/> identifies the limit. An App Limit names the app, a Category Limit names
/// the category.
/// </summary>
/// <param name="Key">The daily key of the limit, for example "cat:Games" or "rule:12" (<see cref="LimitStatus.Key"/>).</param>
/// <param name="Name">The value of <c>{name}</c>: the app for an App Limit, else the category.</param>
/// <param name="CategoryName">The value of <c>{category}</c>: always the category.</param>
public sealed record SuspensionNotice(string Key, string Name, string CategoryName, bool IsAppLimit)
{
    /// <summary>
    /// The notice for an exe of <paramref name="category"/> whose limit today is <paramref name="limit"/>
    /// (<see cref="DayLimits.LimitFor"/>). If the limit is null (the configuration changed during the Pending
    /// Suspension), the notice falls back to the Category Limit of the category.
    /// </summary>
    /// <param name="categoryName">The category name in the language of the Language Preset.</param>
    public static SuspensionNotice For(AppCategory category, LimitStatus? limit, string categoryName)
    {
        if (limit?.Rule is { } rule)
            return new SuspensionNotice(limit.Key, rule.DisplayName, categoryName, IsAppLimit: true);
        return new SuspensionNotice(limit?.Key ?? $"cat:{category}", categoryName, categoryName, IsAppLimit: false);
    }
}
