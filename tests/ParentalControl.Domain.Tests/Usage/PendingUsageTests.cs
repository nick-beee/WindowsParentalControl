using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Usage;

namespace ParentalControl.Domain.Tests.Usage;

public class PendingUsageTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    /// <summary>A fake table: the stored minutes per entry key, and a switch that makes each write throw.</summary>
    private sealed class FakeStore
    {
        public bool Fail;
        public readonly Dictionary<(int, DateOnly, AppCategory?), int> Rows = new();
        public void Write(PendingEntry e)
        {
            if (Fail) throw new IOException("database or disk is full");
            Rows[(e.UserId, e.Day, e.Category)] = Rows.GetValueOrDefault((e.UserId, e.Day, e.Category)) + e.Minutes;
        }
        public int Allowance(int user, DateOnly day) => Rows.GetValueOrDefault((user, day, null));
        public int Category(int user, DateOnly day, AppCategory c) => Rows.GetValueOrDefault((user, day, c));
    }

    /// <summary>One tick as the ledger runs it: add the minute, then try to store all pending entries.</summary>
    private static Exception? Tick(PendingUsage pending, FakeStore store, int user = 1, DateOnly? day = null)
    {
        pending.AddAllowance(user, day ?? Today, 1);
        pending.AddCategory(user, day ?? Today, AppCategory.Games, 1);
        return pending.StoreAll(store.Write);
    }

    [Fact]
    public void DiskFullWhileTheChildPlays()
    {
        var pending = new PendingUsage();
        var store = new FakeStore { Rows = { [(1, Today, null)] = 30 } };
        store.Fail = true;
        Assert.NotNull(Tick(pending, store));
        Assert.NotNull(Tick(pending, store));

        // The reader sum: stored plus pending, so the limit check and the remaining time see the real minutes.
        Assert.Equal(32, store.Allowance(1, Today) + pending.AllowanceFor(1, Today));
        var categories = pending.AddCategoriesTo(new Dictionary<AppCategory, int> { [AppCategory.Games] = 10 }, 1, Today);
        Assert.Equal(12, categories[AppCategory.Games]);
    }

    [Fact]
    public void WriteWorksAgainAndNoMinuteCountsTwoTimes()
    {
        var pending = new PendingUsage();
        var store = new FakeStore { Fail = true };
        Tick(pending, store);
        Tick(pending, store);
        store.Fail = false;
        Assert.Null(Tick(pending, store));

        Assert.Equal(3, store.Allowance(1, Today));
        Assert.Equal(3, store.Category(1, Today, AppCategory.Games));
        Assert.True(pending.IsEmpty);
        Assert.Null(pending.StoreAll(store.Write));
        Assert.Equal(3, store.Allowance(1, Today));
    }

    [Fact]
    public void PendingEntryOfYesterdayIsStoredWithItsOwnDate()
    {
        var pending = new PendingUsage();
        var store = new FakeStore { Fail = true };
        var yesterday = Today.AddDays(-1);
        Tick(pending, store, day: yesterday);
        store.Fail = false;
        Tick(pending, store, day: Today);

        Assert.Equal(1, store.Allowance(1, yesterday));
        Assert.Equal(1, store.Allowance(1, Today));
        Assert.Equal(0, pending.AllowanceFor(1, Today));
    }

    [Fact]
    public void SystemIsNeverCounted()
    {
        var pending = new PendingUsage();
        pending.AddCategory(1, Today, AppCategory.System, 5);
        Assert.True(pending.IsEmpty);
        Assert.Empty(pending.CategoriesFor(1, Today));
    }

    [Fact]
    public void MinutesAddedDuringAStoreStayPending()
    {
        var pending = new PendingUsage();
        pending.AddAllowance(1, Today, 2);
        var error = pending.StoreAll(_ => pending.AddAllowance(1, Today, 1)); // another tick adds while the write runs
        Assert.Null(error);
        Assert.Equal(1, pending.AllowanceFor(1, Today));
    }

    [Fact]
    public void AFailedEntryDoesNotStopTheOthers()
    {
        var pending = new PendingUsage();
        pending.AddAllowance(1, Today, 1);
        pending.AddAllowance(2, Today, 1);
        var error = pending.StoreAll(e => { if (e.UserId == 1) throw new IOException("full"); });
        Assert.NotNull(error);
        Assert.Equal(1, pending.AllowanceFor(1, Today));
        Assert.Equal(0, pending.AllowanceFor(2, Today));
    }
}
