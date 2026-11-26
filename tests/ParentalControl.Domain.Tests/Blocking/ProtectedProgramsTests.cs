using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

public class ProtectedProgramsTests
{
    private static readonly ProtectedPrograms Protected =
        new(@"C:\Windows", [@"C:\Program Files\ParentalControl", @"C:\Program Files\ParentalControl\service\"]);

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\WINDOWS\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\ParentalControl\service\ParentalControl.Service.exe")]
    [InlineData(@"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    [InlineData(@"D:\Apps\MSEdgeWebView2.exe")]
    [InlineData(@"C:\Users\Kid\..\..\Windows\System32\cmd.exe")]
    public void Protected_exe(string path) => Assert.True(Protected.IsProtected(path));

    [Theory]
    [InlineData(@"C:\WindowsApps2\x.exe")]
    [InlineData(@"C:\Windows2\x.exe")]
    [InlineData(@"C:\Program Files\ParentalControlFake\x.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Windows\..\Users\Kid\chrome.exe")]
    public void Not_protected_exe(string path) => Assert.False(Protected.IsProtected(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chrome.exe")]
    [InlineData(@"Tools\chrome.exe")]
    public void A_path_that_is_not_absolute_is_protected(string? path) => Assert.True(Protected.IsProtected(path));

    [Fact]
    public void Folders_are_canonical_and_without_duplicates()
    {
        var p = new ProtectedPrograms(@"c:\windows\", [@"C:\Windows", null, "relative", @"C:\PC\"]);
        Assert.Equal([@"C:\windows\", @"C:\PC\"], p.Folders);
    }

    [Theory]
    [InlineData(@"C:\Windows\", true)]
    [InlineData(@"C:\Windows\System32\", true)]
    [InlineData(@"C:\Program Files\ParentalControl\", true)]
    [InlineData(@"C:\Program Files\", false)]
    [InlineData(@"C:\WindowsApps2\", false)]
    public void Protected_folder(string folder, bool expected) => Assert.Equal(expected, Protected.IsProtectedFolder(folder));
}
