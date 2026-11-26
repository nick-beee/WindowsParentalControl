using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class AdminSetupTests
{
    private static readonly string Valid = TotpSecret.Encode(new byte[20]);
    private static readonly string NineBytes = TotpSecret.Encode(new byte[9]);

    [Fact]
    public void Totp_on_with_a_valid_secret_keeps_its_sign_in_and_sends_no_notice()
    {
        Assert.True(AdminSetup.IsCredentialSetUp("true", Valid));
        Assert.Equal(new AdminStartupPlan(false, false), AdminSetup.PlanStart("true", Valid, noticeSent: false, telegramConfigured: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base32!")]
    public void Totp_on_with_an_empty_or_invalid_secret_is_turned_off_and_sends_the_notice(string secret)
    {
        Assert.False(AdminSetup.IsCredentialSetUp("true", secret));
        Assert.Equal(new AdminStartupPlan(true, true), AdminSetup.PlanStart("true", secret, noticeSent: false, telegramConfigured: true));
    }

    [Fact]
    public void Totp_on_with_a_9_byte_secret_is_turned_off()
    {
        Assert.False(AdminSetup.IsCredentialSetUp("true", NineBytes));
        Assert.True(AdminSetup.PlanStart("true", NineBytes, false, true).DisableTotp);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData(null)]
    public void Totp_off_with_an_old_secret_is_not_set_up_and_sends_the_notice(string? enabled)
    {
        Assert.False(AdminSetup.IsCredentialSetUp(enabled, Valid));
        Assert.Equal(new AdminStartupPlan(false, true), AdminSetup.PlanStart(enabled, Valid, noticeSent: false, telegramConfigured: true));
    }

    [Fact]
    public void Notice_is_sent_one_time_only()
    {
        Assert.False(AdminSetup.PlanStart("false", "", noticeSent: true, telegramConfigured: true).SendSetupNotice);
    }

    [Fact]
    public void No_notice_without_Telegram()
    {
        Assert.False(AdminSetup.PlanStart("false", "", noticeSent: false, telegramConfigured: false).SendSetupNotice);
    }
}
