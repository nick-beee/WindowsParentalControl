using ParentalControl.Core.Data;
using ParentalControl.Core.Services;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Service;

/// <summary>
/// The check of the admin sign-in at each start of the service (Migration Plan step 2 of require-admin-sign-in).
/// </summary>
public static class AdminAccessStartup
{
    /// <summary>
    /// Turns TOTP off when it is on without a valid secret (an empty secret accepted each code before this change),
    /// and sends the German setup notice to Telegram one time while the sign-in is not set up.
    /// </summary>
    public static async Task RunAsync(TelegramBotService telegram, Serilog.ILogger logger)
    {
        if (!DatabaseManager.DataFolderSecured)
        {
            logger.Error(DatabaseManager.DataFolderSecurityError,
                "The service could not set the owner and the access rules of the data folder. The Remote Admin Interface stays locked.");
        }

        var plan = AdminSetup.PlanStart(
            SettingsRepository.Get(SettingsRepository.KeyTotpEnabled, "false"),
            SettingsRepository.Get(SettingsRepository.KeyTotpSecret),
            noticeSent: SettingsRepository.Get(SettingsRepository.KeyAdminSetupNoticeSent) == "true",
            telegramConfigured: !string.IsNullOrWhiteSpace(SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken))
                                && !string.IsNullOrWhiteSpace(SettingsRepository.Get(SettingsRepository.KeyTelegramChatId)));

        if (plan.DisableTotp)
        {
            SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, "false");
            logger.Warning("TOTP was on without a valid secret. The Remote Admin Interface is now not set up until the setup in the Admin app.");
        }

        if (!AdminAccessRepository.IsCredentialSetUp())
            logger.Warning("The admin sign-in is not set up. The Remote Admin Interface is locked until the setup in Parental Control Admin.");

        if (!plan.SendSetupNotice)
            return;

        try
        {
            if (await telegram.SendAdminSetupNeededNoticeAsync())
            {
                SettingsRepository.Set(SettingsRepository.KeyAdminSetupNoticeSent, "true");
                logger.Information("Sent the one-time Telegram notice about the setup of the admin sign-in.");
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not send the Telegram notice about the setup of the admin sign-in.");
        }
    }
}
