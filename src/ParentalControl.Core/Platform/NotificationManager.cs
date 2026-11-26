using ParentalControl.Domain.Messages;
using ParentalControl.Core.Data;
namespace ParentalControl.Core.Platform;

public static class NotificationManager
{
    public static bool SendMessage(int sessionId, string title, string message, bool isWarning = false, int timeoutSeconds = 20, bool wait = false)
    {
        try
        {
            var style = isWarning
                ? NativeMethods.MB_ICONWARNING | NativeMethods.MB_OK
                : NativeMethods.MB_ICONINFORMATION | NativeMethods.MB_OK;

            var titleBytes = (title ?? string.Empty).Length * 2;
            var messageBytes = (message ?? string.Empty).Length * 2;

            return NativeMethods.WTSSendMessageW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                title ?? string.Empty,
                titleBytes,
                message ?? string.Empty,
                messageBytes,
                style,
                timeoutSeconds,
                out _,
                wait);
        }
        catch
        {
            return false;
        }
    }

    public static void SendLogonBanner(int sessionId, string username, int allowedMinutes, TimeOnly start, TimeOnly end)
    {
        var texts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
        var msg = texts.Format("logon_body",
            ("username", username),
            ("time", SessionTexts.Duration(texts, allowedMinutes)),
            ("start", start.ToString("HH:mm")),
            ("end", end.ToString("HH:mm")));

        SendMessage(sessionId, texts["logon_title"], msg, isWarning: false, timeoutSeconds: 20);
    }

    public static void SendWarning(int sessionId, int minutesRemaining)
    {
        var texts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
        var msg = SettingsRepository.GetMessage(SettingsRepository.KeyMsgLimitWarn, new Dictionary<string, string>
        {
            ["minutes"] = minutesRemaining.ToString()
        });

        SendMessage(sessionId, texts["warn_title"], msg, isWarning: true, timeoutSeconds: 30);
    }

    public static void SendCurfewWarning(int sessionId, int minutesRemaining, TimeOnly curfewTime)
    {
        var texts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
        var msg = SettingsRepository.GetMessage(SettingsRepository.KeyMsgCurfewWarn, new Dictionary<string, string>
        {
            ["minutes"] = minutesRemaining.ToString(),
            ["curfew_end"] = curfewTime.ToString("HH:mm")
        });

        SendMessage(sessionId, texts["curfew_warn_title"], msg, isWarning: true, timeoutSeconds: 30);
    }
}
