using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Usage;

/// <summary>
/// One entry of the Pending Usage: minutes of Allowance Time (<see cref="Category"/> null) or of Category Usage
/// for a user and a day of the Control Time Zone.
/// </summary>
public sealed record PendingEntry(int UserId, DateOnly Day, AppCategory? Category, int Minutes);

/// <summary>
/// The Pending Usage (storage-failure, design D6): counted minutes that the database did not store yet. Each reader
/// of a limit adds them to the stored minutes. An entry leaves only after its write returned without an error.
/// The state is in memory only. All members are thread-safe.
/// </summary>
public sealed class PendingUsage
{
    private readonly object _gate = new();
    private readonly object _storeGate = new();
    private readonly Dictionary<(int UserId, DateOnly Day), int> _allowance = new();
    private readonly Dictionary<(int UserId, DateOnly Day, AppCategory Category), int> _categories = new();

    public void AddAllowance(int userId, DateOnly day, int minutes)
    {
        if (minutes <= 0) return;
        lock (_gate)
            _allowance[(userId, day)] = _allowance.GetValueOrDefault((userId, day)) + minutes;
    }

    /// <summary>Adds minutes of Category Usage. System is never counted.</summary>
    public void AddCategory(int userId, DateOnly day, AppCategory category, int minutes)
    {
        if (minutes <= 0 || category == AppCategory.System) return;
        lock (_gate)
            _categories[(userId, day, category)] = _categories.GetValueOrDefault((userId, day, category)) + minutes;
    }

    public bool IsEmpty
    {
        get { lock (_gate) return _allowance.Count == 0 && _categories.Count == 0; }
    }

    /// <summary>A copy of all entries, the Allowance Time first.</summary>
    public IReadOnlyList<PendingEntry> Entries()
    {
        lock (_gate)
        {
            var list = _allowance.Select(e => new PendingEntry(e.Key.UserId, e.Key.Day, null, e.Value)).ToList();
            list.AddRange(_categories.Select(e => new PendingEntry(e.Key.UserId, e.Key.Day, e.Key.Category, e.Value)));
            return list;
        }
    }

    /// <summary>
    /// Takes the minutes of a stored entry out. Minutes that were added after the copy of the entry stay pending.
    /// </summary>
    public void Remove(PendingEntry entry)
    {
        lock (_gate)
        {
            if (entry.Category is { } category)
                Subtract(_categories, (entry.UserId, entry.Day, category), entry.Minutes);
            else
                Subtract(_allowance, (entry.UserId, entry.Day), entry.Minutes);
        }
    }

    private static void Subtract<TKey>(Dictionary<TKey, int> map, TKey key, int minutes) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var value)) return;
        var rest = value - minutes;
        if (rest > 0) map[key] = rest;
        else map.Remove(key);
    }

    /// <summary>The pending minutes of Allowance Time of a user on a day.</summary>
    public int AllowanceFor(int userId, DateOnly day)
    {
        lock (_gate) return _allowance.GetValueOrDefault((userId, day));
    }

    /// <summary>The pending minutes of Category Usage of a user on a day.</summary>
    public IReadOnlyDictionary<AppCategory, int> CategoriesFor(int userId, DateOnly day)
    {
        lock (_gate)
            return _categories.Where(e => e.Key.UserId == userId && e.Key.Day == day)
                .ToDictionary(e => e.Key.Category, e => e.Value);
    }

    /// <summary>The stored Category Usage plus the pending minutes: the value that a limit check uses.</summary>
    public Dictionary<AppCategory, int> AddCategoriesTo(IReadOnlyDictionary<AppCategory, int> stored, int userId, DateOnly day)
    {
        var sum = new Dictionary<AppCategory, int>(stored);
        foreach (var (category, minutes) in CategoriesFor(userId, day))
            sum[category] = sum.GetValueOrDefault(category) + minutes;
        return sum;
    }

    /// <summary>
    /// Writes each pending entry with <paramref name="write"/> and takes it out after the write returned. One write
    /// must be one statement, so a returned write is committed and no minute counts two times. Only one store runs at
    /// a time. Returns null if all entries are stored, else the first error (the failed entries stay pending).
    /// </summary>
    public Exception? StoreAll(Action<PendingEntry> write)
    {
        lock (_storeGate)
        {
            Exception? first = null;
            foreach (var entry in Entries())
            {
                try
                {
                    write(entry);
                    Remove(entry);
                }
                catch (Exception ex)
                {
                    first ??= ex;
                }
            }
            return first;
        }
    }
}
