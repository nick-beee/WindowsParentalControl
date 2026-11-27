using ParentalControl.Domain.Requests;

namespace ParentalControl.Domain.Tests.Requests;

public class RequestWindowPlacementTests
{
    [Fact]
    public void ALargeWorkAreaKeepsThePreferredSizeInTheCentre()
    {
        var rect = RequestWindowPlacement.Fit(new WindowRect(0, 0, 1920, 1040), 560, 760, 380, 480);
        Assert.Equal(new WindowRect(680, 140, 560, 760), rect);
    }

    [Fact]
    public void ASmallScreenGetsAWindowInsideTheWorkArea()
    {
        // VM 102: 1024 x 768 with a taskbar of 40. With the preferred height, the title bar was at -16.
        var rect = RequestWindowPlacement.Fit(new WindowRect(0, 0, 1024, 728), 560, 760, 380, 480);
        Assert.Equal(new WindowRect(232, 0, 560, 728), rect);
        Assert.True(rect.Top >= 0);
        Assert.True(rect.Top + rect.Height <= 728);
    }

    [Fact]
    public void AWorkAreaWithAnOffsetMovesTheWindow()
    {
        // A taskbar at the top or the left, or a second screen.
        var rect = RequestWindowPlacement.Fit(new WindowRect(1920, 40, 1280, 984), 560, 760, 380, 480);
        Assert.Equal(new WindowRect(2280, 152, 560, 760), rect);
    }

    [Fact]
    public void AWorkAreaBelowTheMinimumKeepsTheTitleBarVisible()
    {
        var rect = RequestWindowPlacement.Fit(new WindowRect(10, 20, 300, 400), 560, 760, 380, 480);
        Assert.Equal(new WindowRect(10, 20, 380, 480), rect);
    }
}
