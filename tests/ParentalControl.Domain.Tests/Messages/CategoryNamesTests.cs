using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class CategoryNamesTests
{
    [Fact]
    public void EachCategoryHasANameInEachPreset()
    {
        foreach (var preset in LanguagePreset.All)
            foreach (var category in AppCategories.All)
                Assert.False(string.IsNullOrWhiteSpace(CategoryNames.For(category, preset)));
    }

    [Theory]
    [InlineData(AppCategory.Games, "de", "Spiele")]
    [InlineData(AppCategory.Social, "de", "Soziale Medien")]
    [InlineData(AppCategory.Unknown, "de", "Sonstiges")]
    [InlineData(AppCategory.Games, "en", "Games")]
    [InlineData(AppCategory.Games, "id", "Games")]
    public void NamePerPreset(AppCategory category, string preset, string expected) =>
        Assert.Equal(expected, CategoryNames.For(category, preset));
}
