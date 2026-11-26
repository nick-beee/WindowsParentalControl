using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Breaks;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Core.Services;

public sealed class TelegramBotService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly Serilog.ILogger _logger;
    private long _lastUpdateId = 0;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int RequestId, DateTime ExpiresAt)> _pendingCustomDeclines = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingAskUsers = new();

    public static Func<string, Task<ScreenCaptureRecord?>>? OnCaptureRequested { get; set; }
    public static Func<Task<string>>? OnStatusRequested { get; set; }
    public static Func<string, string, string, int, string?, Task<(bool success, string message)>>? OnPromptDispatchRequested { get; set; }
    public static Func<int, Task<bool>>? OnLockSessionRequested { get; set; }
    public static Func<string, int, Task<bool>>? OnGrantBonusMinutesRequested { get; set; }
    public static Func<Task<List<(int sessionId, string username, string sid)>>>? OnGetActiveSessionsRequested { get; set; }
    /// <summary>The Break Rule and the break state of each Restricted User (Telegram <c>/break</c>).</summary>
    public static Func<Task<IReadOnlyList<BreakStatus>>>? OnBreakStatusRequested { get; set; }
    /// <summary>Ends the Break of a user that started at the given time (Unix seconds). Returns the outcome and the user name.</summary>
    public static Func<int, long, Task<(ParentEndOutcome Outcome, string Username)>>? OnEndBreakRequested { get; set; }

    public TelegramBotService(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    public static async Task<bool> CheckConnectivityAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var response = await HttpClient.GetAsync("https://api.telegram.org", cts.Token);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SendGraceRequestAlertAsync(GraceRequest request, string username)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
        {
            _logger.Warning("Telegram alert skipped: bot token or chat ID is not configured.");
            return false;
        }

        string text;
        object inlineKeyboard;

        if (string.Equals(request.RequestType, "schedule_change", StringComparison.OrdinalIgnoreCase))
        {
            var targetDate = request.TargetDate ?? request.Date;
            var dayName = GetIndonesianDay(targetDate.DayOfWeek);
            var startStr = request.RequestedStart.HasValue ? request.RequestedStart.Value.ToString("HH:mm") : "08:00";
            var endStr = request.RequestedEnd.HasValue ? request.RequestedEnd.Value.ToString("HH:mm") : "21:00";

            text = $"📅 *Pengajuan Perubahan Jadwal Layar*\n"
                 + $"*Pengguna:* `{username}`\n"
                 + $"*Tanggal Sasaran:* `{targetDate:yyyy-MM-dd}` ({dayName})\n"
                 + $"*Kuota & Jam:* {request.RequestedMinutes} menit ({startStr} – {endStr})\n"
                 + $"*Alasan:* _{EscapeMarkdown(request.Reason)}_\n"
                 + $"*Diajukan:* {request.CreatedAt:HH:mm:ss}";

            inlineKeyboard = new
            {
                inline_keyboard = new object[][]
                {
                    new object[] { new { text = "✅ Setujui (1 Hari Saja)", callback_data = $"app_exc:{request.Id}:0" } },
                    new object[] { new { text = "🌟 Setujui (Permanen)", callback_data = $"app_perm:{request.Id}:0" } },
                    new object[] { new { text = "❌ Tolak Permintaan", callback_data = $"dec_menu:{request.Id}:0" } }
                }
            };
        }
        else
        {
            text = $"🎮 *Screen Time Request*\n"
                 + $"*User:* `{username}`\n"
                 + $"*Requested:* {request.RequestedMinutes} minutes\n"
                 + $"*Reason:* _{EscapeMarkdown(request.Reason)}_\n"
                 + $"*Submitted:* {request.CreatedAt:HH:mm:ss}";

            var approveMinutes = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            approveMinutes.Add(request.RequestedMinutes);
            if (request.RequestedMinutes > 60) approveMinutes.Add(60);
            if (request.RequestedMinutes > 30) approveMinutes.Add(30);
            if (request.RequestedMinutes > 15) approveMinutes.Add(15);

            var allButtons = approveMinutes
                .Select(m => (object)new { text = $"✅ Approve {m}m", callback_data = $"approve:{request.Id}:{m}" })
                .ToList();
            allButtons.Add(new { text = "❌ Tolak", callback_data = $"dec_menu:{request.Id}:0" });

            object[][] buttonRows;
            if (allButtons.Count <= 3)
            {
                buttonRows = new[] { allButtons.ToArray() };
            }
            else
            {
                buttonRows = allButtons
                    .Select((btn, idx) => new { btn, idx })
                    .GroupBy(x => x.idx / 2)
                    .Select(g => g.Select(x => x.btn).ToArray())
                    .ToArray();
            }

            inlineKeyboard = new
            {
                inline_keyboard = buttonRows
            };
        }
        var payload = new
        {
            chat_id = chatId,
            text = text,
            parse_mode = "Markdown",
            reply_markup = inlineKeyboard
        };

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to dispatch Telegram grace request notification");
            return false;
        }
    }

    public async Task PollUpdatesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
            var configuredChatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(configuredChatId))
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={_lastUpdateId + 1}&timeout=10";
                var response = await HttpClient.GetAsync(url, stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var jsonStr = await response.Content.ReadAsStringAsync(stoppingToken);
                var doc = JsonNode.Parse(jsonStr);
                var results = doc?["result"]?.AsArray();

                if (results != null)
                {
                    foreach (var item in results)
                    {
                        var updateId = item?["update_id"]?.GetValue<long>() ?? 0;
                        if (updateId > _lastUpdateId)
                        {
                            _lastUpdateId = updateId;
                        }

                        var callbackQuery = item?["callback_query"];
                        if (callbackQuery != null)
                        {
                            await HandleCallbackQueryAsync(token, configuredChatId, callbackQuery);
                        }

                        var message = item?["message"];
                        if (message != null)
                        {
                            await HandleMessageAsync(token, configuredChatId, message);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error while polling Telegram updates");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleCallbackQueryAsync(string botToken, string configuredChatId, JsonNode callbackQuery)
    {
        var callbackId = callbackQuery["id"]?.GetValue<string>() ?? "";
        var data = callbackQuery["data"]?.GetValue<string>() ?? "";
        var messageNode = callbackQuery["message"];
        var messageId = messageNode?["message_id"]?.GetValue<long>() ?? 0;
        var chatId = messageNode?["chat"]?["id"]?.ToString() ?? "";

        if (chatId != configuredChatId)
        {
            _logger.Warning("Unauthorized Telegram callback attempt from chat {ChatId}", chatId);
            await AnswerCallbackQueryAsync(botToken, callbackId, "Unauthorized.");
            return;
        }

        var parts = data.Split(':');
        if (parts.Length < 3)
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, "Invalid command.");
            return;
        }
        var action = parts[0];
        if (action == BreakCallback.Action)
        {
            await HandleEndBreakCallbackAsync(botToken, configuredChatId, callbackId, messageId, data);
            return;
        }
        if (action == "admin_verify")
        {
            var subAction = parts[1].ToLowerInvariant(); // "self" or "lock"
            int.TryParse(parts[2], out var targetSessionId);

            if (subAction == "self")
            {
                await AnswerCallbackQueryAsync(botToken, callbackId, "Akses administrator diverifikasi.");
                await EditMessageTextAsync(botToken, configuredChatId, messageId,
                    $"✅ *Login Administrator Diverifikasi oleh Orang Tua*\n"
                    + $"Waktu verifikasi: `{DateTime.Now:HH:mm:ss}`\n"
                    + $"Sesi pada workstation `{Environment.MachineName}` telah dikonfirmasi aman.");
            }
            else if (subAction == "lock")
            {
                await AnswerCallbackQueryAsync(botToken, callbackId, "Workstation dikunci!");
                if (targetSessionId > 0)
                {
                    SessionManager.LockSession(targetSessionId);
                }
                var consoleId = SessionManager.GetActiveConsoleSessionId();
                if (consoleId >= 0 && consoleId != targetSessionId)
                {
                    SessionManager.LockSession(consoleId);
                }

                EventRepository.LogEvent("SYSTEM", EventType.SECURITY_ALERT,
                    $"Workstation locked remotely via Telegram: unauthorized admin login alert on session {targetSessionId}");

                await EditMessageTextAsync(botToken, configuredChatId, messageId,
                    $"🔒 *AKSES DITOLAK: Komputer Telah Dikunci*\n"
                    + $"Perintah penguncian darurat dieksekusi pada `{DateTime.Now:HH:mm:ss}`.\n"
                    + $"Workstation `{Environment.MachineName}` berhasil dikunci aman.");
            }
            return;
        }

        if (action == "ask_pick_user")
        {
            var targetUser = parts[1];
            _pendingAskUsers[configuredChatId] = targetUser;
            await AnswerCallbackQueryAsync(botToken, callbackId, $"Dipilih: {targetUser}");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"💬 Ketik pesan Anda untuk *{targetUser}*:\n_(Contoh: Sudah selesai tugasnya? [Sudah|Belum 15m])_");
            return;
        }

        if (action == "prompt_act")
        {
            var subAction = parts[1];
            if (subAction == "grant")
            {
                int.TryParse(parts[2], out var mins);
                var targetUser = parts.Length > 3 ? parts[3] : "";
                if (OnGrantBonusMinutesRequested != null && !string.IsNullOrEmpty(targetUser))
                {
                    await OnGrantBonusMinutesRequested(targetUser, mins);
                    await AnswerCallbackQueryAsync(botToken, callbackId, $"+{mins} menit ditambahkan.");
                    var origText = messageNode?["text"]?.ToString() ?? "";
                    await EditMessageTextAsync(botToken, configuredChatId, messageId,
                        origText + $"\n\n✅ *Admin menambahkan +{mins} menit untuk {targetUser}.*");
                }
                else
                {
                    await AnswerCallbackQueryAsync(botToken, callbackId, "Gagal menambah waktu.");
                }
            }
            else if (subAction == "lock")
            {
                int.TryParse(parts[2], out var targetSessionId);
                if (OnLockSessionRequested != null)
                {
                    await OnLockSessionRequested(targetSessionId);
                    await AnswerCallbackQueryAsync(botToken, callbackId, "Workstation dikunci!");
                    var origText = messageNode?["text"]?.ToString() ?? "";
                    await EditMessageTextAsync(botToken, configuredChatId, messageId,
                        origText + $"\n\n🔒 *Sesi komputer berhasil dikunci oleh orang tua.*");
                }
                else
                {
                    await AnswerCallbackQueryAsync(botToken, callbackId, "Gagal mengunci sesi.");
                }
            }
            else if (subAction == "dismiss")
            {
                if (int.TryParse(parts[2], out var promptId))
                {
                    SessionPromptRepository.DismissPrompt(promptId);
                }
                await AnswerCallbackQueryAsync(botToken, callbackId, "Pemberitahuan ditutup.");
                var origText = messageNode?["text"]?.ToString() ?? "";
                await EditMessageTextAsync(botToken, configuredChatId, messageId,
                    origText + $"\n\n👌 *Pemberitahuan ditutup.*");
            }
            return;
        }


        if (!int.TryParse(parts[1], out var requestId))
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, "Invalid payload.");
            return;
        }
        _ = int.TryParse(parts[2], out var bonusMins);

        var req = GraceRequestRepository.GetById(requestId);
        if (req == null)
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, "Request not found.");
            return;
        }

        if (req.Status != "PENDING")
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, $"Already resolved as {req.Status}.");
            return;
        }

        if (action == "approve")
        {
            GraceRequestRepository.Resolve(requestId, "APPROVED");
            UsageRepository.AddBonusMinutes(req.UserId, req.Date, bonusMins);

            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Grace granted: +{bonusMins}m by Admin (Request #{requestId})");

                var activeSessions = SessionManager.GetActiveSessions();
                var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                if (userSession.Sid != null)
                {
                    var texts = ChildTexts();
                    NotificationManager.SendMessage(userSession.SessionId, texts["bonus_title"],
                        texts.Format("bonus_body", ("minutes", bonusMins)),
                        isWarning: false, timeoutSeconds: 20);
                }
            }

            await AnswerCallbackQueryAsync(botToken, callbackId, $"Approved +{bonusMins}m!");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"✅ *Screen Time Request #{requestId} APPROVED* (+{bonusMins} mins)\nResolved at {DateTime.Now:HH:mm:ss}");
        }
        else if (action == "app_exc")
        {
            var targetDate = req.TargetDate ?? req.Date;
            var start = req.RequestedStart ?? new TimeOnly(8, 0);
            var end = req.RequestedEnd ?? new TimeOnly(21, 0);
            var mins = req.RequestedMinutes > 0 ? req.RequestedMinutes : 120;

            ScheduleExceptionRepository.Upsert(new ScheduleException
            {
                UserId = req.UserId,
                ExceptionDate = targetDate,
                DailyMinutes = mins,
                ScheduleStart = start,
                ScheduleEnd = end,
                CreatedAt = DateTime.Now
            });

            GraceRequestRepository.Resolve(requestId, "APPROVED");

            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Schedule exception approved for {targetDate:yyyy-MM-dd}: {mins}m ({start:HH:mm}-{end:HH:mm}) (Request #{requestId})");
                var activeSessions = SessionManager.GetActiveSessions();
                var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                if (userSession.Sid != null)
                {
                    var texts = ChildTexts();
                    NotificationManager.SendMessage(userSession.SessionId, texts["exception_title"],
                        texts.Format("exception_body",
                            ("date", SessionTexts.FormatDate(texts, targetDate)),
                            ("minutes", mins),
                            ("start", start.ToString("HH:mm")),
                            ("end", end.ToString("HH:mm"))),
                        isWarning: false, timeoutSeconds: 20);
                }
            }

            await AnswerCallbackQueryAsync(botToken, callbackId, "Pengecualian Disetujui!");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"✅ *Schedule Change #{requestId} APPROVED (1 Hari Saja)*\nTanggal: `{targetDate:yyyy-MM-dd}`\nKuota: {mins} menit ({start:HH:mm} – {end:HH:mm})\nResolved at {DateTime.Now:HH:mm:ss}");
        }
        else if (action == "app_perm")
        {
            var targetDate = req.TargetDate ?? req.Date;
            var targetDayOfWeek = targetDate.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            var start = req.RequestedStart ?? new TimeOnly(8, 0);
            var end = req.RequestedEnd ?? new TimeOnly(21, 0);
            var mins = req.RequestedMinutes > 0 ? req.RequestedMinutes : 120;

            ScheduleRepository.SaveDaySchedule(new DaySchedule
            {
                UserId = req.UserId,
                DayOfWeek = targetDayOfWeek,
                DailyMinutes = mins,
                ScheduleStart = start,
                ScheduleEnd = end
            });
            GraceRequestRepository.Resolve(requestId, "APPROVED");

            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Permanent schedule approved for {targetDayOfWeek}: {mins}m ({start:HH:mm}-{end:HH:mm}) (Request #{requestId})");
                var activeSessions = SessionManager.GetActiveSessions();
                var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                if (userSession.Sid != null)
                {
                    var texts = ChildTexts();
                    NotificationManager.SendMessage(userSession.SessionId, texts["weekly_title"],
                        texts.Format("weekly_body",
                            ("day", SessionTexts.DayName(texts, targetDayOfWeek)),
                            ("minutes", mins),
                            ("start", start.ToString("HH:mm")),
                            ("end", end.ToString("HH:mm"))),
                        isWarning: false, timeoutSeconds: 20);
                }
            }

            await AnswerCallbackQueryAsync(botToken, callbackId, "Jadwal Permanen Disetujui!");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"🌟 *Schedule Change #{requestId} APPROVED (Jadwal Permanen)*\nHari: `{GetIndonesianDay(targetDayOfWeek)}`\nKuota: {mins} menit ({start:HH:mm} – {end:HH:mm})\nResolved at {DateTime.Now:HH:mm:ss}");
        }
        else if (action == "dec_menu")
        {
            var presetsKeyboard = new
            {
                inline_keyboard = new object[][]
                {
                    new object[] { new { text = "📚 Belum Selesai Tugas", callback_data = $"dec_preset:{requestId}:1" } },
                    new object[] { new { text = "🌙 Waktunya Tidur", callback_data = $"dec_preset:{requestId}:2" } },
                    new object[] { new { text = "🍽️ Waktunya Makan", callback_data = $"dec_preset:{requestId}:3" } },
                    new object[] { new { text = "⚠️ Melanggar Aturan", callback_data = $"dec_preset:{requestId}:4" } },
                    new object[] { new { text = "✍️ Tulis Alasan Sendiri", callback_data = $"dec_custom:{requestId}:0" } },
                    new object[] { new { text = "❌ Tolak Tanpa Alasan", callback_data = $"dec_preset:{requestId}:0" } }
                }
            };

            await AnswerCallbackQueryAsync(botToken, callbackId, "Pilih alasan penolakan:");
            await EditMessageTextAndKeyboardAsync(botToken, configuredChatId, messageId,
                $"❌ *Pilih Alasan Penolakan untuk Permintaan #{requestId}:*", presetsKeyboard);
        }
        else if (action == "dec_preset")
        {
            // The child sees the reason, so it follows the Language Preset. The Telegram buttons stay Indonesian.
            var reasonTexts = ChildTexts();
            string declineReason = bonusMins is >= 1 and <= 4
                ? reasonTexts[$"decline_reason_{bonusMins}"]
                : reasonTexts["decline_reason_default"];

            GraceRequestRepository.Resolve(requestId, "DECLINED", declineReason);
            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN_DENIED, $"Grace declined by parent: {declineReason} (Request #{requestId})");
                var activeSessions = SessionManager.GetActiveSessions();
                var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                if (userSession.Sid != null)
                {
                    NotificationManager.SendMessage(userSession.SessionId, ChildTexts()["declined_title"],
                        ChildTexts().Format("declined_with_reason", ("reason", declineReason)),
                        isWarning: true, timeoutSeconds: 15);
                }
            }

            await AnswerCallbackQueryAsync(botToken, callbackId, "Ditolak.");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"❌ *Permintaan #{requestId} DITOLAK*\nAlasan: _{EscapeMarkdown(declineReason)}_\nResolved at {DateTime.Now:HH:mm:ss}");
        }
        else if (action == "dec_custom")
        {
            _pendingCustomDeclines[configuredChatId] = (requestId, DateTime.UtcNow.AddMinutes(3));
            await AnswerCallbackQueryAsync(botToken, callbackId, "Ketik alasan...");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"✍️ *Silakan balas/ketik alasan penolakan untuk Permintaan #{requestId}:*\n(Waktu respons: 3 menit)");
        }
        else if (action == "decline")
        {
            GraceRequestRepository.Resolve(requestId, "DECLINED");
            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN_DENIED, $"Grace declined by Admin (Request #{requestId})");

                var activeSessions = SessionManager.GetActiveSessions();
                var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                if (userSession.Sid != null)
                {
                    NotificationManager.SendMessage(userSession.SessionId, ChildTexts()["declined_title"],
                        ChildTexts()["declined"],
                        isWarning: true, timeoutSeconds: 15);
                }
            }

            await AnswerCallbackQueryAsync(botToken, callbackId, "Declined.");
            await EditMessageTextAsync(botToken, configuredChatId, messageId,
                $"❌ *Screen Time Request #{requestId} DECLINED*\nResolved at {DateTime.Now:HH:mm:ss}");
        }
    }

    private static async Task AnswerCallbackQueryAsync(string botToken, string callbackId, string text)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{botToken}/answerCallbackQuery";
            var payload = new { callback_query_id = callbackId, text = text };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await HttpClient.PostAsync(url, content);
        }
        catch { }
    }

    private static async Task EditMessageTextAsync(string botToken, string chatId, long messageId, string text)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{botToken}/editMessageText";
            var payload = new
            {
                chat_id = chatId,
                message_id = messageId,
                text = text,
                parse_mode = "Markdown"
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await HttpClient.PostAsync(url, content);
        }
        catch { }
    }

    /// <summary>
    /// The button "end the Break". The chat check ran before. The service ends the Break only if the stored start
    /// equals the start in the button, so an old button does not end a later Break.
    /// </summary>
    private async Task HandleEndBreakCallbackAsync(string botToken, string configuredChatId, string callbackId, long messageId, string data)
    {
        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        if (!BreakCallback.TryParse(data, out var userId, out var breakStart))
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, "Invalid command.");
            return;
        }
        if (OnEndBreakRequested == null)
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, BreakTelegramTexts.NotReady(lang));
            return;
        }

        var (outcome, username) = await OnEndBreakRequested(userId, breakStart);
        if (outcome == ParentEndOutcome.Ended)
        {
            var text = BreakTelegramTexts.Ended(EscapeMarkdown(username), lang);
            await AnswerCallbackQueryAsync(botToken, callbackId, BreakTelegramTexts.Ended(username, lang));
            await EditMessageTextAsync(botToken, configuredChatId, messageId, text);
        }
        else
        {
            await AnswerCallbackQueryAsync(botToken, callbackId, BreakTelegramTexts.AlreadyEnded(lang));
            await EditMessageTextAsync(botToken, configuredChatId, messageId, BreakTelegramTexts.AlreadyEnded(lang));
        }
    }

    /// <summary>Telegram <c>/break</c>: one line per Restricted User, and a button for each running Break.</summary>
    private async Task SendBreakStatusAsync(string chatId)
    {
        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        if (OnBreakStatusRequested == null)
        {
            await SendTextMessageAsync(chatId, BreakTelegramTexts.NotReady(lang));
            return;
        }

        var items = await OnBreakStatusRequested();
        if (items.Count == 0)
        {
            await SendTextMessageAsync(chatId, BreakTelegramTexts.NoUsers(lang));
            return;
        }

        var now = DateTime.UtcNow;
        var lines = items.Select(s => BreakTelegramTexts.Line(s, EscapeMarkdown(s.Username), now, TimeZoneInfo.Local, lang));
        var text = BreakTelegramTexts.Header(lang) + "\n" + string.Join("\n", lines);
        var buttons = items
            .Where(s => s.BreakStartedUtc is not null && s.BreakEndsUtc is { } end && now < end)
            .Select(s => new object[] { new { text = BreakTelegramTexts.EndButton(s.Username, lang), callback_data = BreakCallback.Format(s.UserId, s.BreakStartedUtc!.Value) } })
            .ToArray();

        if (buttons.Length == 0)
            await SendTextMessageAsync(chatId, text);
        else
            await SendTextMessageWithKeyboardAsync(chatId, text, new { inline_keyboard = buttons });
    }

    /// <summary>The notice at a Break start, with the button that ends this Break. The caller checks the setting.</summary>
    public async Task<bool> SendBreakStartedNoticeAsync(int userId, string username, DateTime startUtc, DateTime endUtc)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        var text = BreakTelegramTexts.Notice(EscapeMarkdown(username), startUtc, endUtc, TimeZoneInfo.Local, lang);
        var keyboard = new
        {
            inline_keyboard = new object[][]
            {
                new object[] { new { text = BreakTelegramTexts.EndButton(username, lang), callback_data = BreakCallback.Format(userId, startUtc) } }
            }
        };
        return await SendWithRetryAsync(() => SendTextMessageWithKeyboardAsync(chatId, text, keyboard));
    }

    private async Task HandleMessageAsync(string botToken, string configuredChatId, JsonNode message)
    {
        var chatId = message?["chat"]?["id"]?.ToString() ?? "";
        if (chatId != configuredChatId) return;

        var text = message?["text"]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_pendingAskUsers.TryGetValue(configuredChatId, out var pendingTargetUser))
        {
            _pendingAskUsers.TryRemove(configuredChatId, out _);
            await DispatchAskPromptAsync(chatId, pendingTargetUser, text);
            return;
        }

        if (_pendingCustomDeclines.TryGetValue(configuredChatId, out var pending))
        {
            if (DateTime.UtcNow < pending.ExpiresAt)
            {
                _pendingCustomDeclines.TryRemove(configuredChatId, out _);
                var customReason = text.Trim();
                GraceRequestRepository.Resolve(pending.RequestId, "DECLINED", customReason);

                var customReq = GraceRequestRepository.GetById(pending.RequestId);
                if (customReq != null)
                {
                    var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == customReq.UserId);
                    if (user != null)
                    {
                        EventRepository.LogEvent(user.Sid, EventType.LOGIN_DENIED, $"Grace declined with custom reason: {customReason} (Request #{pending.RequestId})");
                        var activeSessions = SessionManager.GetActiveSessions();
                        var userSession = activeSessions.FirstOrDefault(s => s.Sid == user.Sid);
                        if (userSession.Sid != null)
                        {
                            NotificationManager.SendMessage(userSession.SessionId, ChildTexts()["declined_title"],
                                ChildTexts().Format("declined_with_reason", ("reason", customReason)),
                                isWarning: true, timeoutSeconds: 15);
                        }
                    }
                }

                await SendTextMessageAsync(chatId, $"❌ Permintaan #{pending.RequestId} telah ditolak dengan alasan:\n_{EscapeMarkdown(customReason)}_");
                return;
            }
            else
            {
                _pendingCustomDeclines.TryRemove(configuredChatId, out _);
            }
        }
        if (text.StartsWith("/capture", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var userArg = parts.Length > 1 ? parts[1].Trim() : "";

            if (OnCaptureRequested == null)
            {
                await SendTextMessageAsync(chatId, "⚠️ Layanan tangkapan layar belum siap.");
                return;
            }

            await SendTextMessageAsync(chatId, $"📸 Meminta tangkapan layar desktop untuk {(string.IsNullOrEmpty(userArg) ? "pengguna aktif" : userArg)}...");
            var record = await OnCaptureRequested(userArg);
            if (record != null && File.Exists(record.FilePath))
            {
                try
                {
                    var photoBytes = await File.ReadAllBytesAsync(record.FilePath);
                    var caption = $"📸 *Tangkapan Layar Desktop*\n⏰ Waktu: `{record.Timestamp:yyyy-MM-dd HH:mm:ss}`\n📏 Resolusi: {record.Width}x{record.Height}\n🏷️ Trigger: {record.TriggerType}";
                    await SendPhotoAsync(chatId, photoBytes, caption);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Gagal membaca atau mengirim foto tangkapan layar");
                    await SendTextMessageAsync(chatId, "❌ Gagal mengirim gambar tangkapan layar.");
                }
            }
            else
            {
                await SendTextMessageAsync(chatId, "❌ Gagal mengambil tangkapan layar. Pastikan pengguna sedang aktif dan sesi tidak sedang terkunci.");
            }
        }
        else if (text.StartsWith("/break", StringComparison.OrdinalIgnoreCase))
        {
            await SendBreakStatusAsync(chatId);
        }
        else if (text.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            if (OnStatusRequested != null)
            {
                var statusText = await OnStatusRequested();
                await SendTextMessageAsync(chatId, statusText);
            }
        }
        else if (text.StartsWith("/ask", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/tanya", StringComparison.OrdinalIgnoreCase))
        {
            var cmdParts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (cmdParts.Length < 3)
            {
                if (cmdParts.Length == 2)
                {
                    _pendingAskUsers[configuredChatId] = cmdParts[1];
                    await SendTextMessageAsync(chatId, $"💬 Ketik pesan Anda untuk *{cmdParts[1]}*:\n_(Contoh: Waktunya makan siang ya? [Makan|Nanti 5m])_");
                    return;
                }

                if (OnGetActiveSessionsRequested != null)
                {
                    var sessions = await OnGetActiveSessionsRequested();
                    var activeUsers = sessions.Select(s => s.username).Distinct().ToList();
                    if (activeUsers.Count == 0)
                    {
                        await SendTextMessageAsync(chatId, "ℹ️ Tidak ada pengguna yang sedang aktif saat ini.");
                        return;
                    }

                    var buttons = activeUsers.Select(u => new[]
                    {
                        new { text = $"👤 {u}", callback_data = $"ask_pick_user:{u}" }
                    }).ToArray();

                    await SendTextMessageWithKeyboardAsync(chatId,
                        "💬 *Pilih pengguna aktif yang ingin dikirimi pesan:*",
                        new { inline_keyboard = buttons });
                    return;
                }
                await SendTextMessageAsync(chatId, "ℹ️ Format: `/ask <username> <pesan>` atau `/ask`");
                return;
            }

            var targetUser = cmdParts[1];
            var messageBody = cmdParts[2];
            await DispatchAskPromptAsync(chatId, targetUser, messageBody);
        }
        else if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var helpMsg = "🛡️ *Parental Control Admin Bot*\n\n"
                        + "Perintah yang tersedia:\n"
                        + "• `/ask [user] [pesan]` — Kirim pesan interaktif ke anak\n"
                        + "• `/capture [user]` — Ambil tangkapan layar desktop diam-diam\n"
                        + "• `/status` — Cek sisa screen time dan aplikasi aktif anak\n"
                        + BreakTelegramTexts.HelpLine(LanguagePreset.Id) + "\n"
                        + "• `/help` — Tampilkan bantuan ini";
            await SendTextMessageAsync(chatId, helpMsg);
        }
    }

    private async Task DispatchAskPromptAsync(string chatId, string targetUser, string rawMessage)
    {
        if (OnPromptDispatchRequested == null)
        {
            await SendTextMessageAsync(chatId, "⚠️ Layanan pesan interaktif belum siap.");
            return;
        }

        var urgency = "NORMAL";
        var body = rawMessage;

        if (body.StartsWith("!urgent ", StringComparison.OrdinalIgnoreCase) || body.StartsWith("!mendesak ", StringComparison.OrdinalIgnoreCase))
        {
            urgency = "URGENT";
            var firstSpace = body.IndexOf(' ');
            body = body.Substring(firstSpace + 1).Trim();
        }

        string? customOptions = null;
        var match = System.Text.RegularExpressions.Regex.Match(body, @"\[(.*?)\]");
        if (match.Success)
        {
            customOptions = match.Groups[1].Value;
            body = body.Remove(match.Index, match.Length).Trim();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await SendTextMessageAsync(chatId, "⚠️ Pesan tidak boleh kosong.");
            return;
        }

        var (success, errMsg) = await OnPromptDispatchRequested(targetUser, body, urgency, -1, customOptions);
        if (success)
        {
            var optStr = !string.IsNullOrEmpty(customOptions) ? $"\n• Pilihan khusus: `{customOptions}`" : "";
            var urgStr = urgency == "URGENT" ? "🚨 Mendesak" : "Biasa";
            await SendTextMessageAsync(chatId,
                $"💬 *Pesan Terkirim ke `{EscapeMarkdown(targetUser)}`*\n"
                + $"━━━━━━━━━━━━━━━━━━━━\n"
                + $"• *Pesan:* _{EscapeMarkdown(body)}_\n"
                + $"• *Prioritas:* {urgStr}{optStr}\n"
                + $"• *Batas Waktu Respon:* 120 detik\n\n"
                + $"_Menunggu respon dari komputer pengguna..._");
        }
        else
        {
            await SendTextMessageAsync(chatId, $"❌ Gagal mengirim pesan ke `{EscapeMarkdown(targetUser)}`: {errMsg}");
        }
    }

    public async Task<bool> SendPhotoAsync(string chatId, byte[] photoBytes, string caption)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendPhoto";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            form.Add(new StringContent(caption), "caption");
            form.Add(new StringContent("Markdown"), "parse_mode");

            var fileContent = new ByteArrayContent(photoBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(fileContent, "photo", "screenshot.jpg");

            var response = await HttpClient.PostAsync(url, form);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send Telegram photo");
            return false;
        }
    }

    public async Task<bool> SendTextMessageAsync(string chatId, string text)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new
            {
                chat_id = chatId,
                text = text,
                parse_mode = "Markdown"
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send Telegram text message");
            return false;
        }
    }
    public async Task<bool> SendTextMessageWithKeyboardAsync(string chatId, string text, object inlineKeyboard)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new
            {
                chat_id = chatId,
                text,
                parse_mode = "Markdown",
                reply_markup = inlineKeyboard
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send Telegram message with keyboard");
            return false;
        }
    }

    private async Task<bool> SendWithRetryAsync(Func<Task<bool>> sendAction, int maxRetries = 3)
    {
        int[] delays = { 0, 15, 30 };
        for (int i = 0; i < maxRetries; i++)
        {
            if (delays[i] > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delays[i]));
            }
            try
            {
                if (await sendAction())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Telegram notification attempt {Attempt} failed", i + 1);
            }
        }
        return false;
    }

    public static string FormatTimeWithDelay(DateTime eventTime, string lang)
    {
        var baseStr = $"{eventTime:HH:mm:ss}";
        if ((DateTime.Now - eventTime).TotalSeconds > 60)
        {
            return lang == "en"
                ? $"`{baseStr}` _(Delayed delivery: {DateTime.Now:HH:mm:ss})_"
                : $"`{baseStr}` _(Terkirim tertunda: {DateTime.Now:HH:mm:ss})_";
        }
        return $"`{baseStr}`";
    }

    public static string BuildSignInNotificationText(string username, string machineName, DateTime time, int remainingMinutes, TimeOnly? start, TimeOnly? end, string lang)
    {
        var remainingHours = remainingMinutes / 60;
        var remainingMins = remainingMinutes % 60;
        var remainingStr = remainingHours > 0 ? $"{remainingHours}j {remainingMins}m" : $"{remainingMins}m";
        if (lang == "en")
        {
            remainingStr = remainingHours > 0 ? $"{remainingHours}h {remainingMins}m" : $"{remainingMins}m";
        }

        var curfewStr = (start.HasValue && end.HasValue) ? $"{start.Value:HH:mm} – {end.Value:HH:mm}" : "-";
        var timeStr = FormatTimeWithDelay(time, lang);

        if (lang == "en")
        {
            return $"👤 *Notification: User Signed In*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *User:* `{EscapeMarkdown(username)}`\n"
                 + $"• *Computer:* `{EscapeMarkdown(machineName)}`\n"
                 + $"• *Time:* {timeStr}\n"
                 + $"• *Remaining Screen Time:* {remainingStr}\n"
                 + $"• *Curfew Window:* {curfewStr}";
        }
        return $"👤 *Pemberitahuan: Pengguna Masuk*\n"
             + $"━━━━━━━━━━━━━━━━━━━━\n"
             + $"• *Pengguna:* `{EscapeMarkdown(username)}`\n"
             + $"• *Komputer:* `{EscapeMarkdown(machineName)}`\n"
             + $"• *Waktu:* {timeStr}\n"
             + $"• *Sisa Waktu Layar:* {remainingStr}\n"
             + $"• *Jam Operasional:* {curfewStr}";
    }

    public static string BuildSignOutNotificationText(string username, string machineName, DateTime time, int minutesUsedToday, string lang)
    {
        var usedHours = minutesUsedToday / 60;
        var usedMins = minutesUsedToday % 60;
        var usedStr = usedHours > 0 ? $"{usedHours}j {usedMins}m" : $"{usedMins}m";
        if (lang == "en")
        {
            usedStr = usedHours > 0 ? $"{usedHours}h {usedMins}m" : $"{usedMins}m";
        }

        var timeStr = FormatTimeWithDelay(time, lang);

        if (lang == "en")
        {
            return $"🚪 *Notification: User Signed Out*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *User:* `{EscapeMarkdown(username)}`\n"
                 + $"• *Computer:* `{EscapeMarkdown(machineName)}`\n"
                 + $"• *Time:* {timeStr}\n"
                 + $"• *Screen Time Used Today:* {usedStr}";
        }
        return $"🚪 *Pemberitahuan: Pengguna Keluar*\n"
             + $"━━━━━━━━━━━━━━━━━━━━\n"
             + $"• *Pengguna:* `{EscapeMarkdown(username)}`\n"
             + $"• *Komputer:* `{EscapeMarkdown(machineName)}`\n"
             + $"• *Waktu:* {timeStr}\n"
             + $"• *Total Waktu Terpakai Hari Ini:* {usedStr}";
    }

    public static (string Text, object Keyboard) BuildAdminSignInAlert(int sessionId, string username, string machineName, DateTime time, string lang)
    {
        var timeStr = FormatTimeWithDelay(time, lang);
        string text;
        object keyboard;

        if (lang == "en")
        {
            text = $"🚨 *SECURITY ALERT: Administrator Login Detected*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *User:* `{EscapeMarkdown(username)}` (Administrator / Unrestricted)\n"
                 + $"• *Computer:* `{EscapeMarkdown(machineName)}`\n"
                 + $"• *Time:* {timeStr}\n\n"
                 + $"⚠️ *Is this you?*\n"
                 + $"If this logon was unauthorized, tap *Not Me — Lock PC!* immediately to secure your workstation.";

            keyboard = new
            {
                inline_keyboard = new object[][]
                {
                    new object[]
                    {
                        new { text = "✅ It's Me", callback_data = $"admin_verify:self:{sessionId}" },
                        new { text = "🔒 Not Me — Lock PC!", callback_data = $"admin_verify:lock:{sessionId}" }
                    }
                }
            };
        }
        else
        {
            text = $"🚨 *PERINGATAN KEAMANAN: Login Admin Terdeteksi*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *Pengguna:* `{EscapeMarkdown(username)}` (Administrator / Hak Penuh)\n"
                 + $"• *Komputer:* `{EscapeMarkdown(machineName)}`\n"
                 + $"• *Waktu:* {timeStr}\n\n"
                 + $"⚠️ *Apakah ini Anda?*\n"
                 + $"Jika ini bukan Anda atau mencurigakan, segera ketuk tombol *Kunci Komputer* di bawah untuk mengamankan PC.";

            keyboard = new
            {
                inline_keyboard = new object[][]
                {
                    new object[]
                    {
                        new { text = "✅ Saya Sendiri", callback_data = $"admin_verify:self:{sessionId}" },
                        new { text = "🔒 Bukan Saya — Kunci Komputer!", callback_data = $"admin_verify:lock:{sessionId}" }
                    }
                }
            };
        }

        return (text, keyboard);
    }

    public async Task<bool> SendUserSignInNotificationAsync(string username, string machineName, DateTime time, int remainingMinutes, TimeOnly? start, TimeOnly? end)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        var text = BuildSignInNotificationText(username, machineName, time, remainingMinutes, start, end, lang);

        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, text));
        if (!success)
        {
            var payload = JsonSerializer.Serialize(new SignInNotificationPayload
            {
                Username = username,
                MachineName = machineName,
                RemainingMinutes = remainingMinutes,
                Start = start,
                End = end
            });
            PendingNotificationRepository.Enqueue("USER_SIGN_IN", payload, time);
        }
        return success;
    }

    public async Task<bool> SendUserSignOutNotificationAsync(string username, string machineName, DateTime time, int minutesUsedToday)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        var text = BuildSignOutNotificationText(username, machineName, time, minutesUsedToday, lang);

        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, text));
        if (!success)
        {
            var payload = JsonSerializer.Serialize(new SignOutNotificationPayload
            {
                Username = username,
                MachineName = machineName,
                MinutesUsedToday = minutesUsedToday
            });
            PendingNotificationRepository.Enqueue("USER_SIGN_OUT", payload, time);
        }
        return success;
    }

    public async Task<bool> SendAdminSignInAlertAsync(int sessionId, string username, string machineName, DateTime time)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        var (text, keyboard) = BuildAdminSignInAlert(sessionId, username, machineName, time, lang);

        var success = await SendWithRetryAsync(() => SendTextMessageWithKeyboardAsync(chatId, text, keyboard));
        if (!success)
        {
            var payload = JsonSerializer.Serialize(new AdminSignInPayload
            {
                SessionId = sessionId,
                Username = username,
                MachineName = machineName
            });
            PendingNotificationRepository.Enqueue("ADMIN_SIGN_IN", payload, time);
        }
        return success;
    }

    /// <summary>
    /// Tells the parent that a source or all sources reached the limit on failed admin logins.
    /// Returns false without a Telegram configuration; the caller then writes the event to the log only.
    /// Returns true when the message is sent, or when it waits in the Offline Notification Queue.
    /// </summary>
    public async Task<bool> SendAdminLoginBlockedAlertAsync(string source, DateTime blockedUntilLocal, bool allSources)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var payload = new AdminLoginBlockedPayload
        {
            MachineName = Environment.MachineName,
            Source = source,
            AllSources = allSources,
            BlockedUntilLocal = blockedUntilLocal
        };
        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, BuildAdminLoginBlockedText(payload)));
        if (!success)
        {
            PendingNotificationRepository.Enqueue("ADMIN_LOGIN_BLOCKED", JsonSerializer.Serialize(payload), DateTime.Now);
        }
        return true;
    }

    /// <summary>
    /// Tells the parent one time that the Remote Admin Interface is locked until the setup in the Admin app.
    /// Returns false without a Telegram configuration. Returns true when the message is sent, or when it waits in the
    /// Offline Notification Queue because the PC is offline.
    /// </summary>
    public async Task<bool> SendAdminSetupNeededNoticeAsync()
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var payload = new AdminSetupNeededPayload { MachineName = Environment.MachineName };
        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, BuildAdminSetupNeededText(payload)));
        if (!success)
        {
            PendingNotificationRepository.Enqueue("ADMIN_SETUP_NEEDED", JsonSerializer.Serialize(payload), DateTime.Now);
        }
        return true;
    }

    /// <summary>
    /// Tells the parent that the service moved an untrusted data folder away and started with a new database.
    /// Returns false without a Telegram configuration. Returns true when the message is sent, or when it waits in the
    /// Offline Notification Queue because the PC is offline.
    /// </summary>
    public async Task<bool> SendDataFolderQuarantinedAlertAsync(string movedTo, DateTime whenLocal)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var payload = new DataFolderQuarantinedPayload { MachineName = Environment.MachineName, MovedTo = movedTo, WhenLocal = whenLocal };
        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, BuildDataFolderQuarantinedText(payload)));
        if (!success)
        {
            PendingNotificationRepository.Enqueue("DATA_FOLDER_QUARANTINED", JsonSerializer.Serialize(payload), DateTime.Now);
        }
        return true;
    }

    /// <summary>
    /// Tells the parent that the service made a new data folder with a new database, because the old folder was not
    /// usable (require-admin-sign-in, D14). Returns false without a Telegram configuration. Returns true when the message
    /// is sent, or when it waits in the Offline Notification Queue because the PC is offline.
    /// </summary>
    public async Task<bool> SendDataFolderRelocatedAlertAsync(string from, string to, DateTime whenLocal)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var payload = new DataFolderRelocatedPayload { MachineName = Environment.MachineName, From = from, To = to, WhenLocal = whenLocal };
        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, BuildDataFolderRelocatedText(payload)));
        if (!success)
        {
            PendingNotificationRepository.Enqueue("DATA_FOLDER_RELOCATED", JsonSerializer.Serialize(payload), DateTime.Now);
        }
        return true;
    }

    /// <summary>
    /// Tells the parent that another program holds port 5050 of the web server (require-admin-sign-in, third review
    /// SF3). The owner text comes from an exe (untrusted): it is escaped and sent only as data.
    /// </summary>
    public async Task<bool> SendWebPortBlockedAlertAsync(string owner, DateTime whenLocal)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var payload = new WebPortBlockedPayload { MachineName = Environment.MachineName, Owner = owner, WhenLocal = whenLocal };
        var success = await SendWithRetryAsync(() => SendTextMessageAsync(chatId, BuildWebPortBlockedText(payload)));
        if (!success)
        {
            PendingNotificationRepository.Enqueue("WEB_PORT_BLOCKED", JsonSerializer.Serialize(payload), DateTime.Now);
        }
        return true;
    }

    private static string BuildWebPortBlockedText(WebPortBlockedPayload payload) =>
        ParentalControl.Domain.AdminAccess.AdminAccessTexts.TelegramWebPortBlocked(
            EscapeMarkdown(payload.MachineName), EscapeMarkdown(payload.Owner), payload.WhenLocal);

    private static string BuildDataFolderRelocatedText(DataFolderRelocatedPayload payload) =>
        ParentalControl.Domain.AdminAccess.AdminAccessTexts.TelegramDataFolderRelocated(
            EscapeMarkdown(payload.MachineName), EscapeMarkdown(payload.From), EscapeMarkdown(payload.To), payload.WhenLocal);

    private static string BuildDataFolderQuarantinedText(DataFolderQuarantinedPayload payload) =>
        ParentalControl.Domain.AdminAccess.AdminAccessTexts.TelegramDataFolderQuarantined(
            EscapeMarkdown(payload.MachineName), EscapeMarkdown(payload.MovedTo), payload.WhenLocal);

    private static string BuildAdminLoginBlockedText(AdminLoginBlockedPayload payload) =>
        ParentalControl.Domain.AdminAccess.AdminAccessTexts.TelegramLoginBlocked(
            EscapeMarkdown(payload.MachineName),
            EscapeMarkdown(ParentalControl.Domain.AdminAccess.AdminAccessTexts.SourceName(payload.Source)),
            payload.AllSources ? ParentalControl.Domain.AdminAccess.LoginLimitScope.AllSources : ParentalControl.Domain.AdminAccess.LoginLimitScope.Source,
            payload.BlockedUntilLocal);

    private static string BuildAdminSetupNeededText(AdminSetupNeededPayload payload) =>
        ParentalControl.Domain.AdminAccess.AdminAccessTexts.TelegramSetupNeeded(EscapeMarkdown(payload.MachineName));

    public async Task<bool> SendPromptResponseAlertAsync(SessionPrompt prompt, int? sessionId = null)
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
        var isYes = string.Equals(prompt.Response, "YES", StringComparison.OrdinalIgnoreCase);
        var isTimeout = string.Equals(prompt.Response, "TIMEOUT", StringComparison.OrdinalIgnoreCase);
        var durationStr = lang == "en" ? $"{prompt.TurnaroundSeconds} seconds" : $"{prompt.TurnaroundSeconds} detik";

        string text;
        if (lang == "en")
        {
            var statusIcon = isYes ? "✅" : (isTimeout ? "⏳" : "❌");
            var respText = isYes ? "Yes / Acknowledged" : (isTimeout ? "Timed Out (No Response)" : "No / Declined");
            text = $"💬 *Notification: User Responded to Prompt*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *User:* `{EscapeMarkdown(prompt.Username)}`\n"
                 + $"• *Prompt:* _{EscapeMarkdown(prompt.Message)}_\n"
                 + $"• *Response:* {statusIcon} *{respText}*\n";

            if (!isYes && !isTimeout && !string.IsNullOrWhiteSpace(prompt.ResponseReason))
            {
                text += $"• *Reason:* _{EscapeMarkdown(prompt.ResponseReason)}_\n";
            }
            text += $"• *Turnaround Time:* {durationStr}\n"
                 + $"• *Priority:* {(prompt.Urgency == "URGENT" ? "🚨 Urgent" : "Normal")}";
        }
        else
        {
            var statusIcon = isYes ? "✅" : (isTimeout ? "⏳" : "❌");
            var respText = isYes ? "Ya / Siap" : (isTimeout ? "Waktu Habis (Tidak Ada Respon)" : "Tidak");
            text = $"💬 *Pemberitahuan: Respon Pesan Pengguna*\n"
                 + $"━━━━━━━━━━━━━━━━━━━━\n"
                 + $"• *Pengguna:* `{EscapeMarkdown(prompt.Username)}`\n"
                 + $"• *Pesan:* _{EscapeMarkdown(prompt.Message)}_\n"
                 + $"• *Jawaban:* {statusIcon} *{respText}*\n";

            if (!isYes && !isTimeout && !string.IsNullOrWhiteSpace(prompt.ResponseReason))
            {
                text += $"• *Alasan:* _{EscapeMarkdown(prompt.ResponseReason)}_\n";
            }
            text += $"• *Waktu Menjawab:* {durationStr}\n"
                 + $"• *Prioritas:* {(prompt.Urgency == "URGENT" ? "🚨 Mendesak" : "Biasa")}";
        }

        var rows = new List<object[]>();
        var targetSid = sessionId ?? 0;

        if (!isYes && !isTimeout)
        {
            rows.Add(new object[]
            {
                new { text = "➕ 15 Menit", callback_data = $"prompt_act:grant:15:{prompt.Username}" },
                new { text = "➕ 30 Menit", callback_data = $"prompt_act:grant:30:{prompt.Username}" }
            });
        }

        rows.Add(new object[]
        {
            new { text = "🔒 Kunci PC Sekarang", callback_data = $"prompt_act:lock:{targetSid}" },
            new { text = "👌 Tutup", callback_data = $"prompt_act:dismiss:{prompt.Id}" }
        });

        var keyboard = new { inline_keyboard = rows.ToArray() };
        return await SendWithRetryAsync(() => SendTextMessageWithKeyboardAsync(chatId, text, keyboard));
    }

    public async Task<int> ProcessPendingQueueAsync()
    {
        var token = SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken);
        var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return 0;

        if (!await CheckConnectivityAsync()) return 0;

        var pending = PendingNotificationRepository.GetPending(10);
        if (pending.Count == 0) return 0;

        var processed = 0;
        foreach (var item in pending)
        {
            if (item.RetryCount >= 20)
            {
                PendingNotificationRepository.Delete(item.Id);
                continue;
            }

            try
            {
                var lang = LanguagePreset.ParentLanguage(SettingsRepository.GetLanguagePreset());
                bool sent = false;

                if (item.NotificationType == "USER_SIGN_IN")
                {
                    var payload = JsonSerializer.Deserialize<SignInNotificationPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        var text = BuildSignInNotificationText(payload.Username, payload.MachineName, item.EventTime, payload.RemainingMinutes, payload.Start, payload.End, lang);
                        sent = await SendTextMessageAsync(chatId, text);
                    }
                }
                else if (item.NotificationType == "USER_SIGN_OUT")
                {
                    var payload = JsonSerializer.Deserialize<SignOutNotificationPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        var text = BuildSignOutNotificationText(payload.Username, payload.MachineName, item.EventTime, payload.MinutesUsedToday, lang);
                        sent = await SendTextMessageAsync(chatId, text);
                    }
                }
                else if (item.NotificationType == "ADMIN_SIGN_IN")
                {
                    var payload = JsonSerializer.Deserialize<AdminSignInPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        var (text, keyboard) = BuildAdminSignInAlert(payload.SessionId, payload.Username, payload.MachineName, item.EventTime, lang);
                        sent = await SendTextMessageWithKeyboardAsync(chatId, text, keyboard);
                    }
                }

                else if (item.NotificationType == "ADMIN_LOGIN_BLOCKED")
                {
                    var payload = JsonSerializer.Deserialize<AdminLoginBlockedPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        sent = await SendTextMessageAsync(chatId, BuildAdminLoginBlockedText(payload));
                    }
                }
                else if (item.NotificationType == "DATA_FOLDER_RELOCATED")
                {
                    var payload = JsonSerializer.Deserialize<DataFolderRelocatedPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        sent = await SendTextMessageAsync(chatId, BuildDataFolderRelocatedText(payload));
                    }
                }
                else if (item.NotificationType == "WEB_PORT_BLOCKED")
                {
                    var payload = JsonSerializer.Deserialize<WebPortBlockedPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        sent = await SendTextMessageAsync(chatId, BuildWebPortBlockedText(payload));
                    }
                }
                else if (item.NotificationType == "DATA_FOLDER_QUARANTINED")
                {
                    var payload = JsonSerializer.Deserialize<DataFolderQuarantinedPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        sent = await SendTextMessageAsync(chatId, BuildDataFolderQuarantinedText(payload));
                    }
                }
                else if (item.NotificationType == "ADMIN_SETUP_NEEDED")
                {
                    var payload = JsonSerializer.Deserialize<AdminSetupNeededPayload>(item.PayloadJson);
                    if (payload != null)
                    {
                        sent = await SendTextMessageAsync(chatId, BuildAdminSetupNeededText(payload));
                    }
                }

                if (sent)
                {
                    PendingNotificationRepository.Delete(item.Id);
                    processed++;
                }
                else
                {
                    PendingNotificationRepository.IncrementRetry(item.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to send queued Telegram notification #{Id}", item.Id);
                PendingNotificationRepository.IncrementRetry(item.Id);
            }
        }
        return processed;
    }


    private static async Task EditMessageTextAndKeyboardAsync(string botToken, string chatId, long messageId, string text, object inlineKeyboard)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{botToken}/editMessageText";
            var payload = new
            {
                chat_id = chatId,
                message_id = messageId,
                text = text,
                parse_mode = "Markdown",
                reply_markup = inlineKeyboard
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await HttpClient.PostAsync(url, content);
        }
        catch { }
    }

    /// <summary>The fixed session texts for the child in the current Language Preset.</summary>
    private static TextSet ChildTexts() => SessionTexts.For(SettingsRepository.GetLanguagePreset());

    private static string GetIndonesianDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Senin",
        DayOfWeek.Tuesday => "Selasa",
        DayOfWeek.Wednesday => "Rabu",
        DayOfWeek.Thursday => "Kamis",
        DayOfWeek.Friday => "Jumat",
        DayOfWeek.Saturday => "Sabtu",
        DayOfWeek.Sunday => "Minggu",
        _ => day.ToString()
    };
    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`");
    }
}
