using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests.Blocking;

/// <summary>The path test of the Trusted WebView2 Runtime (suspend-untrusted-webview2, #48).</summary>
public class TrustedWebView2Tests
{
    private static readonly TrustedWebView2 Trusted = new(@"C:\Program Files (x86)", @"C:\Program Files");

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0.2903.70\msedgewebview2.exe")]
    [InlineData(@"C:\Program Files\Microsoft\EdgeWebView\Application\131.0.2903.70\msedgewebview2.exe")]
    [InlineData(@"c:\program files (x86)\microsoft\edgewebview\application\131.0\MSEDGEWEBVIEW2.EXE")]
    [InlineData(@"\\?\C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    [InlineData(@"C:/Program Files (x86)/Microsoft/EdgeWebView/Application/131.0/msedgewebview2.exe")]
    [InlineData(@"""C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe""")]
    // A ".." that stays below the runtime folder: only administrators can write there.
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\..\130.0\msedgewebview2.exe")]
    public void Runtime_in_a_Program_Files_folder_is_trusted(string path) => Assert.True(Trusted.IsTrusted(path));

    [Theory]
    // A copy that the child made in a folder that it can write.
    [InlineData(@"C:\Users\Kid\Downloads\msedgewebview2.exe")]
    [InlineData(@"C:\Users\Kid\Downloads\wv\msedgewebview2.exe")]
    [InlineData(@"\\?\C:\Users\Kid\Downloads\wv\MSEdgeWebView2.exe")]
    // A per-user runtime and a Fixed Version runtime of an app.
    [InlineData(@"C:\Users\Kid\AppData\Local\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    [InlineData(@"C:\Program Files\StudyApp\WebView2\msedgewebview2.exe")]
    // Traversal out of the runtime folder.
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\..\..\..\..\Users\Kid\msedgewebview2.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\..\msedgewebview2.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe")]
    // A folder name that only starts with the runtime folder.
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application2\msedgewebview2.exe")]
    // Another exe in the runtime folder.
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\game.exe")]
    // Another drive and a UNC path to the same disk: not the canonical form, so not trusted (fail closed).
    [InlineData(@"D:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    [InlineData(@"\\localhost\C$\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    // An 8.3 short name: not the canonical form, so not trusted (fail closed).
    [InlineData(@"C:\PROGRA~2\Microsoft\EDGEWE~1\APPLIC~1\131.0\MSEDGE~1.EXE")]
    [InlineData(@"C:\PROGRA~2\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    // Not an absolute Windows path.
    [InlineData(@"msedgewebview2.exe")]
    [InlineData(@"Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe")]
    [InlineData(null)]
    [InlineData("")]
    public void Each_other_path_is_not_trusted(string? path) => Assert.False(Trusted.IsTrusted(path));

    [Fact]
    public void Folders_are_canonical_and_without_duplicates()
    {
        var t = new TrustedWebView2(@"C:\Program Files\", @"c:\program files");
        Assert.Equal([@"C:\Program Files\Microsoft\EdgeWebView\Application\"], t.Folders);
    }

    [Fact]
    public void Without_a_Program_Files_folder_nothing_is_trusted()
    {
        var t = new TrustedWebView2(null, "relative");
        Assert.Empty(t.Folders);
        Assert.False(t.IsTrusted(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\131.0\msedgewebview2.exe"));
    }
}
