using System.Collections.Concurrent;
using ParentalControl.Core.Data;
using ParentalControl.Core.Services;

namespace ParentalControl.Service;

/// <summary>
/// Telegram alerts to the parent that need no database write (harden-clock-and-disk, design D8): the Storage Lockdown,
/// a time zone change and the Control Time Zone. A failed send keeps the alert in memory, and
/// <see cref="UsageMonitorWorker"/> tries again every minute while the service runs. It does not use the Offline
/// Notification Queue, because that queue is a database write.
/// </summary>
public sealed class ParentAlertOutbox
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Func<string, string, Task<bool>> _send;
    private readonly Func<string?> _chatId;
    private readonly Serilog.ILogger _logger;
    private int _flushing;

    public ParentAlertOutbox(TelegramBotService telegram, Serilog.ILogger logger)
        : this(telegram.SendTextMessageAsync, () => SettingsRepository.Get(SettingsRepository.KeyTelegramChatId), logger) { }

    public ParentAlertOutbox(Func<string, string, Task<bool>> send, Func<string?> chatId, Serilog.ILogger logger)
    {
        _send = send;
        _chatId = chatId;
        _logger = logger;
    }

    /// <summary>The alerts that wait for a send.</summary>
    public int Count => _queue.Count;

    /// <summary>Adds a plain text alert and starts a send. Untrusted parts must already be in the text as data only.</summary>
    public void Post(string text)
    {
        _logger.Information("Parent alert queued: {Text}", text);
        _queue.Enqueue(text);
        _ = FlushAsync();
    }

    /// <summary>Sends the waiting alerts in order. A failed send stops the run; the alert stays for the next run.</summary>
    public async Task FlushAsync()
    {
        if (_queue.IsEmpty || Interlocked.Exchange(ref _flushing, 1) == 1)
            return;
        try
        {
            string? chatId;
            try
            {
                chatId = _chatId();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not read the Telegram chat for the parent alerts; trying again later");
                return;
            }

            if (string.IsNullOrWhiteSpace(chatId))
            {
                while (_queue.TryDequeue(out var dropped))
                    _logger.Warning("No Telegram chat configured; parent alert not sent: {Text}", dropped);
                return;
            }

            while (_queue.TryPeek(out var text))
            {
                bool ok;
                try
                {
                    ok = await _send(chatId, Escape(text));
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Telegram send of a parent alert failed");
                    ok = false;
                }

                if (!ok)
                {
                    _logger.Warning("Parent alert not sent; it stays in memory and the service tries again in one minute");
                    return;
                }
                _queue.TryDequeue(out _);
                _logger.Information("Parent alert sent: {Text}", text);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    /// <summary>The text goes as Telegram Markdown: escape the markup characters.</summary>
    private static string Escape(string text) =>
        text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");
}
