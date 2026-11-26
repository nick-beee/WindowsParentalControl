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
        await AlertQuarantineAsync(telegram, logger);

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

    /// <summary>
    /// Tells the parent one time on Telegram that the service moved an untrusted data folder away (finding B2).
    /// Right after the move the database is new and has no Telegram configuration; the message then goes out at the
    /// first start after the parent configures Telegram again. The event log and the Admin app show the move at once.
    /// </summary>
    private static async Task AlertQuarantineAsync(TelegramBotService telegram, Serilog.ILogger logger)
    {
        var report = Core.Platform.DataFolderStatusStore.Read();
        if (string.IsNullOrEmpty(report.QuarantinedTo)
            || SettingsRepository.Get(SettingsRepository.KeyDataFolderQuarantineNotified) == report.QuarantinedTo)
            return;
        try
        {
            var when = (report.QuarantinedAtUtc ?? DateTimeOffset.UtcNow).ToLocalTime().DateTime;
            if (await telegram.SendDataFolderQuarantinedAlertAsync(report.QuarantinedTo, when))
                SettingsRepository.Set(SettingsRepository.KeyDataFolderQuarantineNotified, report.QuarantinedTo);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not send the Telegram alert about the moved data folder.");
        }
    }
}
