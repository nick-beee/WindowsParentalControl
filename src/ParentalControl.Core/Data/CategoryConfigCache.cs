using ParentalControl.Domain.Categories;

namespace ParentalControl.Core.Data;

/// <summary>
/// An in-memory copy of the Category Limits and the App Rules, with a resolver and a calculator.
/// The category and rule repositories reset it on each write in this process.
/// </summary>
public static class CategoryConfigCache
{
    public sealed record Snapshot(
        IReadOnlyDictionary<AppCategory, DailyLimit> Limits,
        IReadOnlyList<AppRule> Rules,
        CategoryResolver Resolver,
        CategoryLimitCalculator Calculator);

    private static readonly object Gate = new();
    private static Snapshot? _current;

    public static Snapshot Current
    {
        get
        {
            lock (Gate)
            {
                if (_current is not null)
                    return _current;
                var limits = CategoryRepository.GetLimits();
                var rules = AppRuleRepository.GetAll();
                _current = new Snapshot(limits, rules, new CategoryResolver(rules), new CategoryLimitCalculator(limits, rules));
                return _current;
            }
        }
    }

    /// <summary>Raised after a change of a Category Limit or an App Rule. The Category enforcer listens.</summary>
    public static event Action? Changed;

    public static void Invalidate()
    {
        lock (Gate)
            _current = null;
        Changed?.Invoke();
    }
}
