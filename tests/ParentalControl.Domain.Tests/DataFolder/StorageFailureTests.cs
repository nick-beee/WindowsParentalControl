using ParentalControl.Domain.DataFolder;
using static ParentalControl.Domain.DataFolder.StorageFailureClassifier;

namespace ParentalControl.Domain.Tests.DataFolder;

public class StorageFailureTests
{
    private const long FiveGb = 5L * 1024 * 1024 * 1024;

    private static StorageFailureKind Sqlite(int code) => Classify(new StorageFailureFacts(code, null, false, false));

    [Fact]
    public void SqliteFullIsDiskFull() => Assert.Equal(StorageFailureKind.DiskFull, Sqlite(SqliteFull));

    [Fact]
    public void ExtendedCodeWithThePrimaryCodeFullIsDiskFull() =>
        Assert.Equal(StorageFailureKind.DiskFull, Sqlite(SqliteFull | (1 << 8)));

    [Theory]
    [InlineData(HResultDiskFull)]
    [InlineData(HResultHandleDiskFull)]
    public void WindowsDiskFullIsDiskFull(int hresult) =>
        Assert.Equal(StorageFailureKind.DiskFull, Classify(new StorageFailureFacts(null, hresult, true, false)));

    [Fact]
    public void ReadOnlyAndAccessDeniedAreReadOnly()
    {
        Assert.Equal(StorageFailureKind.ReadOnly, Sqlite(SqliteReadOnly));
        Assert.Equal(StorageFailureKind.ReadOnly, Sqlite(SqliteReadOnly | (2 << 8))); // SQLITE_READONLY_CANTLOCK
        Assert.Equal(StorageFailureKind.ReadOnly, Classify(new StorageFailureFacts(null, null, false, true)));
    }

    [Fact]
    public void BusyAndLockedAreLocked()
    {
        Assert.Equal(StorageFailureKind.Locked, Sqlite(SqliteBusy));
        Assert.Equal(StorageFailureKind.Locked, Sqlite(SqliteLocked));
    }

    [Fact]
    public void IoErrorIsDiskError()
    {
        Assert.Equal(StorageFailureKind.DiskError, Sqlite(SqliteIoErr));
        Assert.Equal(StorageFailureKind.DiskError, Sqlite(SqliteIoErr | (3 << 8))); // SQLITE_IOERR_WRITE
        Assert.Equal(StorageFailureKind.DiskError, Classify(new StorageFailureFacts(null, unchecked((int)0x80070015), true, false)));
    }

    [Fact]
    public void OtherSqliteCodeIsDatabaseError() => Assert.Equal(StorageFailureKind.DatabaseError, Sqlite(11)); // SQLITE_CORRUPT

    [Fact]
    public void NoCodeAndNoIoErrorIsInternal() =>
        Assert.Equal(StorageFailureKind.Internal, Classify(new StorageFailureFacts(null, null, false, false)));

    [Fact]
    public void DiskFullTextsSayDiskFullAndFreeSpace()
    {
        var failure = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        var entry = StorageLockdownTexts.EntryEvent(failure, 3L * 1024 * 1024);
        var alert = StorageLockdownTexts.Alert("PC1", failure, 3L * 1024 * 1024);
        Assert.Contains("the disk is full", entry);
        Assert.Contains("free space on the disk", entry);
        Assert.Contains("the disk is full", alert);
        Assert.Contains("free space on the disk", alert);
        Assert.Contains("usage store", alert);
        Assert.Contains("3 MB", entry);
        Assert.Contains("3 MB", alert);
        Assert.Contains("PC1", alert);
    }

    [Theory]
    [InlineData(StorageFailureKind.Internal, "limit check", "an internal error of the service, not a database error")]
    [InlineData(StorageFailureKind.ReadOnly, "storage probe", "the database is read-only or access is denied")]
    [InlineData(StorageFailureKind.Locked, "usage store", "another program locks the database")]
    [InlineData(StorageFailureKind.DiskError, "suspension record", "a disk or file error")]
    [InlineData(StorageFailureKind.DatabaseError, "storage probe", "a database error")]
    public void OtherCausesDoNotClaimAFullDisk(StorageFailureKind kind, string operation, string cause)
    {
        var failure = new StorageFailure(operation, kind, "InvalidOperationException");
        foreach (var text in new[] { StorageLockdownTexts.EntryEvent(failure, FiveGb), StorageLockdownTexts.Alert("PC1", failure, FiveGb) })
        {
            Assert.DoesNotContain("disk is full", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("free space", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(operation, text);
            Assert.Contains(cause, text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("service log", text);
        }
        Assert.Contains("event 1015", StorageLockdownTexts.Alert("PC1", failure, FiveGb));
        Assert.Contains("not a full disk", StorageLockdownTexts.EntryEvent(failure, FiveGb));
    }

    // Review of PR #67, finding 2: the exit needs 100 MB free for each cause, so a text with less free space (or an
    // unknown free space) asks the parent to free space, and does not say "not a full disk".
    [Theory]
    [InlineData(StorageFailureKind.DiskError, 0L)]
    [InlineData(StorageFailureKind.Locked, 80L * 1024 * 1024)]
    [InlineData(StorageFailureKind.Internal, null)]
    public void OtherCauseWithLowSpaceAsksToFreeSpace(StorageFailureKind kind, long? free)
    {
        var failure = new StorageFailure("usage store", kind, "SqliteException");
        foreach (var text in new[] { StorageLockdownTexts.EntryEvent(failure, free), StorageLockdownTexts.Alert("PC1", failure, free) })
        {
            Assert.Contains("free space on the disk, to at least 100 MB", text);
            Assert.Contains("service log", text);
            Assert.Contains(StorageLockdownTexts.Cause(kind), text);
            Assert.DoesNotContain("not a full disk", text);
            Assert.DoesNotContain("disk is full", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void InternalErrorWithFiveGbFreeDoesNotAskToFreeSpace()
    {
        var failure = new StorageFailure("category check", StorageFailureKind.Internal, "InvalidOperationException");
        var entry = StorageLockdownTexts.EntryEvent(failure, FiveGb);
        var alert = StorageLockdownTexts.Alert("PC1", failure, FiveGb);
        Assert.Contains("not a full disk", entry);
        foreach (var text in new[] { entry, alert })
        {
            Assert.DoesNotContain("free space", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("category check", text);
            Assert.Contains("5120 MB", text);
        }
    }

    [Fact]
    public void DiskFullTextAsksForTheExitMinimum()
    {
        var failure = new StorageFailure("usage store", StorageFailureKind.DiskFull, "SqliteException");
        Assert.Contains("to at least 100 MB", StorageLockdownTexts.EntryEvent(failure, FiveGb));
        Assert.Contains("to at least 100 MB", StorageLockdownTexts.Alert("PC1", failure, 0));
    }

    [Fact]
    public void UnknownCauseDoesNotClaimAFullDisk()
    {
        Assert.DoesNotContain("disk is full", StorageLockdownTexts.EntryEvent(null, null));
        Assert.Contains("unknown", StorageLockdownTexts.Alert("PC1", null, null));
        Assert.DoesNotContain("disk is full", StorageLockdownTexts.Alert("PC1", null, null));
        Assert.Contains("to at least 100 MB", StorageLockdownTexts.Alert("PC1", null, null));
    }
}
