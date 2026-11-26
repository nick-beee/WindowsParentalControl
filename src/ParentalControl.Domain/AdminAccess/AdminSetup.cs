namespace ParentalControl.Domain.AdminAccess;

/// <param name="DisableTotp">True if the service must set <c>totp_enabled</c> to <c>"false"</c>, because TOTP is on without a valid secret.</param>
/// <param name="SendSetupNotice">True if the service must send the one-time Telegram notice about the setup.</param>
public sealed record AdminStartupPlan(bool DisableTotp, bool SendSetupNotice);

/// <summary>
/// The setup state of the Remote Admin Interface. The setting <c>totp_enabled</c> means "the setup is complete".
/// The sign-in is set up only when <c>totp_enabled</c> is <c>"true"</c> and the saved secret is valid
/// (see <see cref="TotpSecret"/>).
/// </summary>
public static class AdminSetup
{
    public const string Enabled = "true";

    public static bool IsCredentialSetUp(string? totpEnabled, string? secret) =>
        totpEnabled == Enabled && TotpSecret.IsValid(secret);

    /// <summary>The one-time check at each start of the service (Migration Plan step 2 of require-admin-sign-in).</summary>
    public static AdminStartupPlan PlanStart(string? totpEnabled, string? secret, bool noticeSent, bool telegramConfigured)
    {
        var on = totpEnabled == Enabled;
        var valid = TotpSecret.IsValid(secret);
        var setUp = on && valid;
        return new AdminStartupPlan(
            DisableTotp: on && !valid,
            SendSetupNotice: !setUp && telegramConfigured && !noticeSent);
    }
}
