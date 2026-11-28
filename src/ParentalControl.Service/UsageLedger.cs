using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Usage;

namespace ParentalControl.Service;

/// <summary>
/// The writes of the counted time (harden-clock-and-disk, design D6). Each add goes into the Pending Usage first, then
/// the ledger tries to store all pending entries. An entry leaves the Pending Usage only after its write returned.
/// Each reader of a limit uses the stored time plus the Pending Usage.
/// </summary>
public sealed class UsageLedger
{
    /// <summary>The one instance of the service.</summary>
    public static UsageLedger Shared { get; } = new(
        UsageRepository.AddMinutes, CategoryUsageRepository.AddMinutes, StorageHealth.Shared);

    /// <summary>The operation of a failed store in event 1015 and the alert (harden-storage-lockdown, design D2).</summary>
    public const string StoreOperation = "usage store";

    private readonly Action<int, DateOnly, int> _writeAllowance;
    private readonly Action<int, DateOnly, AppCategory, int> _writeCategory;
    private readonly StorageHealth _health;
    private readonly Func<int, DateOnly, UsageRecord?> _readAllowance;
    private readonly Func<int, DateOnly, IReadOnlyDictionary<AppCategory, int>> _readCategories;

    public UsageLedger(Action<int, DateOnly, int> writeAllowance, Action<int, DateOnly, AppCategory, int> writeCategory, StorageHealth health,
        Func<int, DateOnly, UsageRecord?>? readAllowance = null, Func<int, DateOnly, IReadOnlyDictionary<AppCategory, int>>? readCategories = null)
    {
        _writeAllowance = writeAllowance;
        _writeCategory = writeCategory;
        _health = health;
        _readAllowance = readAllowance ?? UsageRepository.GetUsage;
        _readCategories = readCategories ?? ((userId, day) => CategoryUsageRepository.GetForUserAndDate(userId, day));
    }

    public PendingUsage Pending { get; } = new();

    /// <summary>The error of the last store that failed, for the log. Null after a successful store.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Adds minutes of Allowance Time. Never throws.</summary>
    public void AddAllowance(int userId, DateOnly day, int minutes)
    {
        Pending.AddAllowance(userId, day, minutes);
        Store();
    }

    /// <summary>Adds minutes of Category Usage. System is never counted. Never throws.</summary>
    public void AddCategory(int userId, DateOnly day, AppCategory category, int minutes)
    {
        Pending.AddCategory(userId, day, category, minutes);
        Store();
    }

    /// <summary>Stores all pending entries. Returns true if none is left. A failure is reported to <see cref="StorageHealth"/>.</summary>
    public bool Store()
    {
        var error = Pending.StoreAll(e =>
        {
            if (e.Category is { } category)
                _writeCategory(e.UserId, e.Day, category, e.Minutes);
            else
                _writeAllowance(e.UserId, e.Day, e.Minutes);
        });
        LastError = error;
        if (error is not null)
            _health.ReportFailure(StoreOperation, error);
        return error is null;
    }

    /// <summary>
    /// The used Allowance Time: the stored minutes plus the pending minutes. Both reads run while no store runs
    /// (harden-storage-lockdown, design D7), so no minute counts two times or not at all.
    /// </summary>
    public int AllowanceUsed(int userId, DateOnly day) =>
        Pending.Consistent(() => (_readAllowance(userId, day)?.MinutesUsed ?? 0) + Pending.AllowanceFor(userId, day));

    /// <summary>
    /// The used Allowance Time, for the callers that read the stored row first. It reads the stored minutes again
    /// together with the pending minutes (design D7), so the minutes of <paramref name="stored"/> are not used: a store
    /// between the read of <paramref name="stored"/> and this call cannot count a minute two times.
    /// </summary>
    public int AllowanceUsed(int userId, DateOnly day, UsageRecord? stored) => AllowanceUsed(userId, day);

    /// <summary>The Category Usage of a day: the stored minutes plus the pending minutes, read while no store runs.</summary>
    public Dictionary<AppCategory, int> CategoryUsage(int userId, DateOnly day) =>
        Pending.Consistent(() => Pending.AddCategoriesTo(_readCategories(userId, day), userId, day));
}
