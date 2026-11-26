using ParentalControl.Domain.Breaks;

namespace ParentalControl.Domain.Tests.Breaks;

public class BreakRuleTests
{
    [Fact]
    public void DefaultIsOnWith45MinutesOfUseAnd10MinutesOfBreak()
    {
        Assert.Equal(new BreakRule(true, 45, 10), BreakRule.Default);
        Assert.True(BreakRule.Default.IsValid);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(240, true)]
    [InlineData(241, false)]
    public void UseMinutesRange(int minutes, bool valid)
    {
        Assert.Equal(valid, BreakRule.IsValidUseMinutes(minutes));
        Assert.Equal(valid, new BreakRule(true, minutes, 10).IsValid);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void BreakMinutesRange(int minutes, bool valid)
    {
        Assert.Equal(valid, BreakRule.IsValidBreakMinutes(minutes));
        Assert.Equal(valid, new BreakRule(true, 45, minutes).IsValid);
    }

    [Fact]
    public void RangeCheckDoesNotDependOnEnabled() =>
        Assert.False(new BreakRule(false, 5, 10).IsValid);
}
