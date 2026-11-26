using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", @"C:\Windows\explorer.exe")]
    [InlineData(@"c:\windows\explorer.exe", @"C:\windows\explorer.exe")]
    [InlineData(@"C:/Games//x.exe", @"C:\Games\x.exe")]
    [InlineData(@"C:\Games\.\x.exe", @"C:\Games\x.exe")]
    [InlineData(@"C:\Windows\..\Users\Kid\x.exe", @"C:\Users\Kid\x.exe")]
    [InlineData(@"C:\..\..\x.exe", @"C:\x.exe")]
    [InlineData(@"C:\Games\", @"C:\Games")]
    [InlineData(@"C:\", @"C:")]
    [InlineData(@"\\?\C:\Games\x.exe", @"C:\Games\x.exe")]
    [InlineData(@"\\?\UNC\srv\share\x.exe", @"\\srv\share\x.exe")]
    [InlineData(@"\\srv\share\a\..\x.exe", @"\\srv\share\x.exe")]
    [InlineData(@"\\srv\share\..\..\x.exe", @"\\srv\share\x.exe")]
    [InlineData("  \"C:\\Games\\x.exe\" ", @"C:\Games\x.exe")]
    public void Canonicalize_gives_the_canonical_form(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.Canonicalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x.exe")]
    [InlineData(@"Games\x.exe")]
    [InlineData(@"C:x.exe")]
    [InlineData(@"\Games\x.exe")]
    [InlineData(@"\\srv")]
    [InlineData(@"\\srv\..\x")]
    [InlineData("C:\\a\0b.exe")]
    public void Canonicalize_refuses_a_path_that_is_not_absolute(string? input) =>
        Assert.Null(WindowsPath.Canonicalize(input));

    [Theory]
    [InlineData(@"C:\Games\x.exe", "x.exe")]
    [InlineData(@"C:/Games/x.exe", "x.exe")]
    [InlineData("x.exe", "x.exe")]
    public void GetFileName_takes_the_last_part(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.GetFileName(input));

    [Fact]
    public void Folder_match_is_on_a_whole_folder_boundary()
    {
        var folder = WindowsPath.CanonicalizeFolder(@"C:\Games")!;
        Assert.True(WindowsPath.IsBelow(@"C:\Games\x.exe", folder));
        Assert.True(WindowsPath.IsBelow(@"c:\GAMES\sub\x.exe", folder));
        Assert.False(WindowsPath.IsBelow(@"C:\GamesBackup\x.exe", folder));
    }
}
