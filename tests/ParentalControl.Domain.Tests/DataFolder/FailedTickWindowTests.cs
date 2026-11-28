using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class FailedTickWindowTests
{
    private const long Minute = 60_000;

    [Fact]
    public void GoodTickDoesNotRemoveAFailedTick()
    {
        var w = new FailedTickWindow();
        w.Report(true, 0);
        Assert.Equal(1, w.Report(false, Minute));
    }

    [Fact]
    public void FailedTickExactlyTenMinutesOldIsOut()
    {
        var w = new FailedTickWindow();
        w.Report(true, 0);
        Assert.Equal(1, w.Report(false, 10 * Minute - 1));
        Assert.Equal(0, w.Report(false, 10 * Minute));
    }

    [Fact]
    public void ClearEmptiesTheWindow()
    {
        var w = new FailedTickWindow();
        w.Report(true, 0);
        w.Report(true, Minute);
        w.Clear();
        Assert.Equal(0, w.Report(false, 2 * Minute));
    }
}
