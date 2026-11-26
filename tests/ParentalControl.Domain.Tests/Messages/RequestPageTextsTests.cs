using ParentalControl.Domain.Messages;

namespace ParentalControl.Domain.Tests.Messages;

public class RequestPageTextsTests
{
    [Fact]
    public void EachPresetHasSevenDayCodes()
    {
        foreach (var preset in LanguagePreset.All)
        {
            var set = RequestPageTexts.For(preset);
            var codes = Enum.GetValues<DayOfWeek>().Select(d => RequestPageTexts.DayCode(set, d)).ToList();
            Assert.Equal(7, codes.Distinct().Count());
            Assert.DoesNotContain(codes, c => c.StartsWith("code_", StringComparison.Ordinal));
        }
        Assert.Equal("Mo", RequestPageTexts.DayCode(RequestPageTexts.For("de"), DayOfWeek.Monday));
    }

    [Theory]
    [InlineData("de", 45, "45 Minuten")]
    [InlineData("de", 90, "90 Minuten (1 Stunde 30 Minuten)")]
    [InlineData("de", 120, "120 Minuten (2 Stunden)")]
    [InlineData("en", 60, "60 minutes (1 hour)")]
    [InlineData("id", 90, "90 Menit (1 jam 30 menit)")]
    public void MinutesText(string preset, int minutes, string expected) =>
        Assert.Equal(expected, RequestPageTexts.MinutesText(RequestPageTexts.For(preset), SessionTexts.For(preset), minutes));

    [Theory]
    [InlineData("de", 1, "Du hast heute schon 1 Anfrage gesendet.")]
    [InlineData("de", 3, "Du hast heute schon 3 Anfragen gesendet.")]
    [InlineData("en", 1, "You reached the maximum of 1 request for today.")]
    public void MaxRequestsUsesWordForms(string preset, int max, string expected) =>
        Assert.Equal(expected, RequestPageTexts.For(preset).Format("api_max_reached", ("max", max)));

    [Fact]
    public void GermanSuccessMessage() =>
        Assert.Equal("Deine Anfrage für +15 Minuten ist bei deinen Eltern.",
            RequestPageTexts.For("de").Format("api_extension_sent", ("minutes", 15)));
}
