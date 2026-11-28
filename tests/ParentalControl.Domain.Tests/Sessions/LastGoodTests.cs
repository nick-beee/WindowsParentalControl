using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

// Final review, item 1: a failed read of the category config or of the user must not stop the count of a sample.
public class LastGoodTests
{
    private static readonly Func<string?> Fails = () => throw new InvalidOperationException("database is locked");

    [Fact]
    public void A_good_read_gives_its_value()
    {
        var last = new LastGood<string?>();
        var result = last.Read(() => "config 1", () => "fallback");
        Assert.Equal("config 1", result.Value);
        Assert.False(result.Failed);
        Assert.True(last.HasValue);
    }

    [Fact]
    public void A_failed_read_gives_the_last_good_value_and_the_error()
    {
        var last = new LastGood<string?>();
        last.Read(() => "config 1", () => "fallback");
        var result = last.Read(Fails, () => "fallback");
        Assert.Equal("config 1", result.Value);
        Assert.True(result.Failed);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public void A_failed_read_before_any_good_read_gives_the_fallback()
    {
        var last = new LastGood<string?>();
        var result = last.Read(Fails, () => "fallback");
        Assert.Equal("fallback", result.Value);
        Assert.True(result.Failed);
        Assert.False(last.HasValue);
    }

    [Fact]
    public void A_newer_good_read_replaces_the_value_also_with_null()
    {
        // A user that the parent deleted: the fresh null wins over the old user.
        var last = new LastGood<string?>();
        last.Read(() => "Kid", () => null);
        Assert.Null(last.Read(() => null, () => "fallback").Value);
        Assert.Null(last.Read(Fails, () => "fallback").Value);
    }

    [Fact]
    public void Each_failure_keeps_the_same_last_good_value()
    {
        var last = new LastGood<string?>();
        last.Read(() => "config 1", () => null);
        for (var i = 0; i < 3; i++)
            Assert.Equal("config 1", last.Read(Fails, () => null).Value);
        Assert.Equal("config 2", last.Read(() => "config 2", () => null).Value);
    }
}
