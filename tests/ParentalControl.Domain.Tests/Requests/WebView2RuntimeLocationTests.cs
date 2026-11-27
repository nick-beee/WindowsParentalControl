using ParentalControl.Domain.Requests;

namespace ParentalControl.Domain.Tests.Requests;

public class WebView2RuntimeLocationTests
{
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";

    [Fact]
    public void BuildsTheFolderBelowProgramFiles()
    {
        Assert.Equal(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0.2903.70",
            WebView2RuntimeLocation.RuntimeFolder(ProgramFilesX86, "131.0.2903.70"));
        Assert.Equal(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0.2903.70",
            WebView2RuntimeLocation.RuntimeFolder(ProgramFilesX86 + @"\", "131.0.2903.70"));
        Assert.Equal(@"C:\X\msedgewebview2.exe", WebView2RuntimeLocation.BrowserExePath(@"C:\X"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0.0.0.0")]
    [InlineData("..")]
    [InlineData(@"..\..\Users\Kid\wv")]
    [InlineData("131.0.2903.70\\..\\x")]
    [InlineData("131.0.2903.70/x")]
    [InlineData("131.0.2903")]
    [InlineData("131.0.2903.70.1")]
    [InlineData("131.0.2903.70 ")]
    [InlineData("131.0.2903.x")]
    [InlineData("1..2.3")]
    public void RefusesAnInvalidVersion(string? pv)
    {
        Assert.False(WebView2RuntimeLocation.IsInstalledVersion(pv));
        Assert.Null(WebView2RuntimeLocation.RuntimeFolder(ProgramFilesX86, pv));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Program Files (x86)")]
    [InlineData(@"\\server\share")]
    [InlineData(@"C:")]
    [InlineData(@"C:/Program Files (x86)")]
    [InlineData(@"C:\Users\Kid\..\..\pf")]
    public void RefusesABaseFolderThatIsNotAnAbsoluteDrivePath(string? baseFolder) =>
        Assert.Null(WebView2RuntimeLocation.RuntimeFolder(baseFolder, "131.0.2903.70"));

    [Fact]
    public void AcceptsARealVersion() => Assert.True(WebView2RuntimeLocation.IsInstalledVersion("131.0.2903.70"));
}
