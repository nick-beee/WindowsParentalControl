namespace ParentalControl.Domain.DataFolder;

/// <summary>
/// The time limit of the walk through the data folder (require-admin-sign-in, CB1 c). The child can make the walk long,
/// for example with many uploads to the capture route. One budget covers all walks of one data folder step: the tries
/// of the secure step and the read-only check.
/// </summary>
public sealed class WalkBudget
{
    /// <summary>The default limit of one data folder step.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;
    private readonly TimeSpan _limit;
    private readonly int? _maxItems;
    private int _items;

    /// <param name="maxItems">Tests only: the budget ends after this number of items.</param>
    public WalkBudget(TimeSpan limit, TimeProvider? time = null, int? maxItems = null)
    {
        _time = time ?? TimeProvider.System;
        _start = _time.GetUtcNow();
        _limit = limit;
        _maxItems = maxItems;
    }

    public static WalkBudget Default() => new(DefaultLimit);

    public int ItemsVisited => _items;

    /// <summary>Counts one item. Returns false if the budget is used up; the caller then stops the walk.</summary>
    public bool TryVisit()
    {
        _items++;
        if (_maxItems is { } max && _items > max)
            return false;
        return _time.GetUtcNow() - _start < _limit;
    }
}

/// <summary>One entry of the DACL of the data folder itself, with its inheritance flags.</summary>
public sealed record RootAce(AceKind Kind, string Sid, uint Mask, bool ObjectInherit, bool ContainerInherit, bool InheritOnly);

/// <summary>The owner and the DACL of the data folder itself.</summary>
/// <param name="DaclProtected">True if the DACL does not inherit from the parent folder.</param>
/// <param name="Dacl">The entries, or null for a null DACL.</param>
public sealed record RootRules(string? OwnerSid, bool DaclProtected, IReadOnlyList<RootAce>? Dacl);

/// <summary>
/// The fast path of the data folder step (require-admin-sign-in, CB1 b). The walk through all items runs only when
/// it is necessary. The service skips it when the last start secured this same folder and the folder still has the
/// rules that the service wrote (<c>O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c>). With these rules, only SYSTEM or an
/// administrator can add or change an item, so the content is as the last walk left it.
/// </summary>
public static class DataFolderFastPath
{
    /// <summary>FILE_ALL_ACCESS.</summary>
    public const uint FullAccess = 0x001F_01FF;

    /// <summary>
    /// True if the rules of the folder are exactly the rules that the service writes: owner Administrators, a
    /// protected DACL, and only two allow entries with full access, inherited by files and folders: SYSTEM and
    /// Administrators. The compare uses the meaning, not the SDDL text, because Windows can give other flags or another order.
    /// </summary>
    public static bool HasExpectedRules(RootRules rules)
    {
        if (!string.Equals(rules.OwnerSid, DataFolderTrust.AdministratorsSid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!rules.DaclProtected || rules.Dacl is null || rules.Dacl.Count != 2)
            return false;
        var system = false;
        var admins = false;
        foreach (var ace in rules.Dacl)
        {
            if (ace.Kind != AceKind.Allow || ace.Mask != FullAccess || !ace.ObjectInherit || !ace.ContainerInherit || ace.InheritOnly)
                return false;
            if (string.Equals(ace.Sid, DataFolderTrust.SystemSid, StringComparison.OrdinalIgnoreCase))
                system = true;
            else if (string.Equals(ace.Sid, DataFolderTrust.AdministratorsSid, StringComparison.OrdinalIgnoreCase))
                admins = true;
            else
                return false;
        }
        return system && admins;
    }

    /// <summary>
    /// True if the service can skip the walk: the last start secured this same folder without a warning, the folder
    /// has the expected rules, and the quick check of the items that the service uses passed.
    /// </summary>
    /// <param name="lastSecuredFolder">The folder of the last Secured state without a detail, from the registry, or null.</param>
    /// <param name="folder">The folder of this start.</param>
    /// <param name="quickItemsOk">The database, <c>logs</c> and <c>captures</c> are no link, and the database has one name.</param>
    public static bool CanSkipWalk(string? lastSecuredFolder, string folder, RootRules rules, bool quickItemsOk) =>
        lastSecuredFolder is not null
        && string.Equals(lastSecuredFolder.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && HasExpectedRules(rules)
        && quickItemsOk;
}
