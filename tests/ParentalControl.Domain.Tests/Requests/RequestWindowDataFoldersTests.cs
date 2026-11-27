using ParentalControl.Domain.Requests;

namespace ParentalControl.Domain.Tests.Requests;

public class RequestWindowDataFoldersTests
{
    [Fact]
    public void NewNamesAreUniqueGuids()
    {
        var a = RequestWindowDataFolders.NewName();
        var b = RequestWindowDataFolders.NewName();
        Assert.NotEqual(a, b);
        Assert.True(RequestWindowDataFolders.IsDataFolderName(a));
        Assert.Equal(32, a.Length);
    }

    [Theory]
    [InlineData("EBWebView")]
    [InlineData("Default")]
    [InlineData("0f8e7a4c-6d1b-4a35-9d2e-111111111111")]
    [InlineData("0f8e7a4c6d1b4a359d2e11111111111")]
    [InlineData("0f8e7a4c6d1b4a359d2e1111111111zz")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherNamesAreNotDataFolders(string? name) => Assert.False(RequestWindowDataFolders.IsDataFolderName(name));

    [Fact]
    public void DeletesOnlyOldGuidFolders()
    {
        var current = "0f8e7a4c6d1b4a359d2e111111111111";
        var old1 = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        var old2 = "AAAAAAAABBBBCCCCDDDDEEEEEEEEFFFF";
        var result = RequestWindowDataFolders.ToDelete(new[] { current, old1, "Crashpad", old2, "notes.txt" }, current);
        Assert.Equal(new[] { old1, old2 }, result);
    }
}
