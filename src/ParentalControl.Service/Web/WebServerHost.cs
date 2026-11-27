using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Core.Platform;
using ParentalControl.Core.Security;
using ParentalControl.Core.Services;
using ParentalControl.Service.Web.Pages;
using ParentalControl.Core;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Web;
using ParentalControl.Domain.Captures;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.AdminAccess;
using ParentalControl.Domain.Breaks;
using ParentalControl.Domain.Time;
using Microsoft.AspNetCore.Routing;

namespace ParentalControl.Service.Web;

public sealed class WebServerHost : BackgroundService
{
    private readonly SessionTracker _sessionTracker;
    private readonly TelegramBotService _telegramBotService;
    private readonly Serilog.ILogger _logger;
    private readonly ForegroundHistory _foregroundHistory;
    private readonly CategoryEnforcer _categoryEnforcer;
    private readonly BreakEnforcer _breakEnforcer;
    private readonly AdminAccessService _adminAccess;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string ProcessName, string WindowTitle, DateTime LastSeen)> _liveActivities = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CaptureCommandState> _captureStates = new(StringComparer.OrdinalIgnoreCase);
    public static event Action<ScreenCaptureRecord>? OnCaptureReceived;
    private readonly Dictionary<int, (int Files, long Bytes)> _captureReservations = new();
    private readonly CaptureRefusalLog _captureRefusals = new();

    /// <summary>Copies at most <paramref name="limit"/> bytes. Returns the number of bytes copied.</summary>
    private static async Task<long> CopyAtMostAsync(Stream source, Stream target, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < limit)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total)));
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read));
            total += read;
        }
        return total;
    }
    private readonly ServiceStartup? _startup;

    /// <summary>The port of the web server. Tests use another port.</summary>
    public int Port { get; init; } = 5050;

    /// <summary>The wait after a failed start of the web server (S3). Tests use a short wait.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = WebServerRetry.Delay;

    /// <summary>The number of starts that failed, for example because another program uses the port.</summary>
    public int FailedStarts => _failedStarts;
    private int _failedStarts;

    /// <summary>Finds the processes that listen on a port (SF3). Tests replace it.</summary>
    public Func<int, IReadOnlyList<int>> FindPortOwners { get; init; } = TcpConnectionOwner.FindListenerPids;

    /// <summary>The process id of the service. A listener of this process is no squat. Tests use another value.</summary>
    public int ServicePid { get; init; } = Environment.ProcessId;

    /// <summary>
    /// Writes the program that holds the port to the admin-only registry key, or clears it with null (SF3). The Admin
    /// app shows it as a banner. Tests replace it, so a test does not change the state of an installed service.
    /// </summary>
    public Action<string?> RecordPortBlocked { get; init; } = owner => DataFolderStatusStore.TryWriteWebPortBlocked(owner, DateTimeOffset.UtcNow);

    /// <summary>Sends the Telegram alert about the program that holds the port (SF3). Tests replace it.</summary>
    public Func<string, Task>? SendPortAlert { get; init; }

    /// <summary>The program that held the port at the last failed bind, or null. For the tests.</summary>
    public string? LastPortOwner { get; private set; }

    private readonly PortSquatAlert _squatAlert = new();

    /// <param name="startup">The start gate of the service, or null in a test (the database is ready then).</param>
    public WebServerHost(SessionTracker sessionTracker, TelegramBotService telegramBotService, Serilog.ILogger logger,
        ForegroundHistory foregroundHistory, CategoryEnforcer categoryEnforcer, BreakEnforcer breakEnforcer, ServiceStartup? startup = null)
    {
        _startup = startup;
        _sessionTracker = sessionTracker;
        _telegramBotService = telegramBotService;
        _logger = logger;
        _foregroundHistory = foregroundHistory;
        _categoryEnforcer = categoryEnforcer;
        _breakEnforcer = breakEnforcer;
        _adminAccess = new AdminAccessService(telegramBotService, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Third review, SF3: bind the port at once, before the data folder step, so a program of the child cannot take
        // it while the step runs. Until the start gate opens, each route answers 503 (StartGateAsync): the routes read
        // the database, which is not safe before the gate.
        await RunServerAsync(stoppingToken);
    }

    /// <summary>
    /// Runs the web server until <paramref name="stoppingToken"/> ends. If the start fails, for example because a
    /// program of the child took the port first, it logs the error and tries again after <see cref="RetryDelay"/>
    /// (require-admin-sign-in, S3). It never ends the service: the limits do not depend on the web server.
    /// </summary>
    public async Task RunServerAsync(CancellationToken stoppingToken)
    {
        // Setup Telegram Bot command hooks
        TelegramBotService.OnCaptureRequested = async (userArg) =>
        {
            string targetUsername;
            if (!string.IsNullOrWhiteSpace(userArg))
            {
                targetUsername = userArg;
            }
            else
            {
                var activeSessions = _sessionTracker.ActiveSessions.Values.Where(s => !s.IsLocked).ToList();
                var firstRestricted = activeSessions.Select(s => UserRepository.GetBySid(s.UserSid)).FirstOrDefault(u => u != null && u.IsRestricted);
                if (firstRestricted == null) return null;
                targetUsername = firstRestricted.Username;
            }
            return await TriggerCaptureAndWaitAsync(targetUsername, "TELEGRAM", 15);
        };

        TelegramBotService.OnStatusRequested = () =>
        {
            var restrictedUsers = UserRepository.GetAll().Where(u => u.IsRestricted).ToList();
            if (restrictedUsers.Count == 0) return Task.FromResult("ℹ️ Belum ada akun pengguna terbatas yang terdaftar.");

            var sb = new StringBuilder();
            sb.AppendLine("📊 *Status Screen Time Hari Ini:*\n");
            var moment = ControlTime.Current.Read(); // control-clock: the day of the Control Time Zone
            var today = moment.Today;
            var dayOfWeek = moment.DayOfWeek;

            foreach (var u in restrictedUsers)
            {
                var limit = ScheduleRepository.GetEffectiveLimit(u.Id, dayOfWeek, today);
                var usage = UsageRepository.GetUsage(u.Id, today);
                var totalAllowed = (limit?.DailyMinutes ?? 120) + (usage?.BonusMinutes ?? 0);
                var used = UsageLedger.Shared.AllowanceUsed(u.Id, today, usage);
                var remaining = Math.Max(0, totalAllowed - used);

                var session = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == u.Sid);
                var isOnline = session != null;
                var isLocked = session?.IsLocked ?? false;

                string stateText;
                if (!isOnline) stateText = "⚫ Offline";
                else if (isLocked) stateText = "🔒 Terkunci (Dijeda)";
                else stateText = "🟢 Aktif di desktop";

                sb.AppendLine($"👤 *{u.Username}* ({stateText})");
                sb.AppendLine($"   ⏳ Sisa: *{remaining}m* / {totalAllowed}m (Terpakai: {used}m)");
                if (limit != null)
                {
                    sb.AppendLine($"   🌙 Jam Malam: `{limit.ScheduleStart:HH:mm} – {limit.ScheduleEnd:HH:mm}`");
                }

                if (_liveActivities.TryGetValue(u.Username, out var act) && (DateTime.Now - act.LastSeen).TotalMinutes < 2)
                {
                    sb.AppendLine($"   🎮 Sedang membuka: `{act.ProcessName}`");
                }
                sb.AppendLine();
            }
            return Task.FromResult(sb.ToString());
        };

        TelegramBotService.OnPromptDispatchRequested = (targetUsername, message, urgency, targetDisplay, customOptions) =>
        {
            var user = UserRepository.GetByUsername(targetUsername)
                ?? UserRepository.GetAll().FirstOrDefault(u => u.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
            if (user == null)
            {
                return Task.FromResult((false, $"Pengguna '{targetUsername}' tidak ditemukan."));
            }

            var promptId = SessionPromptRepository.Create(user.Sid, user.Username, message, urgency, targetDisplay, customOptions);
            EventRepository.LogEvent(user.Sid, EventType.PROMPT_SENT, $"Pesan interaktif dikirim ke {user.Username}: \"{message}\" (Prioritas: {urgency})");

            var activeSession = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == user.Sid && !s.IsLocked);
            if (activeSession != null)
            {
                _sessionTracker.EnsureAgentRunning(activeSession.SessionId);
            }

            return Task.FromResult((true, string.Empty));
        };

        TelegramBotService.OnLockSessionRequested = (sessionId) =>
        {
            if (sessionId > 0)
            {
                SessionManager.LockSession(sessionId);
            }
            var consoleId = SessionManager.GetActiveConsoleSessionId();
            if (consoleId >= 0 && consoleId != sessionId)
            {
                SessionManager.LockSession(consoleId);
            }
            return Task.FromResult(true);
        };

        TelegramBotService.OnGrantBonusMinutesRequested = (targetUsername, minutes) =>
        {
            var user = UserRepository.GetByUsername(targetUsername)
                ?? UserRepository.GetAll().FirstOrDefault(u => u.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
            if (user != null)
            {
                var today = ControlTime.Current.Today;
                UsageRepository.AddBonusMinutes(user.Id, today, minutes);
                EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Admin menambahkan +{minutes}m via Telegram");
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        };

        TelegramBotService.OnGetActiveSessionsRequested = () =>
        {
            var list = _sessionTracker.ActiveSessions.Values
                .Where(s => !s.IsLocked)
                .Select(s => (s.SessionId, s.Username, s.UserSid))
                .ToList();
            return Task.FromResult(list);
        };

        await RunWithRetryAsync(async ct =>
        {
            var builder = WebApplication.CreateBuilder();
            // SF3: one listen socket on [::] (dual mode) with SO_EXCLUSIVEADDRUSE. No fallback to 0.0.0.0, so a program
            // that holds [::1]:5050 cannot keep the IPv6 loopback, and no program can bind 127.0.0.1:5050 after us.
            builder.WebHost.UseSockets(o => o.CreateBoundListenSocket = CreateExclusiveListenSocket);
            builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.IPv6Any, Port));
            builder.Services.AddRouting();
            await using var app = builder.Build();
            ConfigureApp(app);
            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await OnBindFailedAsync(ex);
                throw;
            }
            _startup?.MarkWebBindTried();
            await OnBoundAsync();
            _logger.Information("Starting embedded WebServer on http://[::]:{Port}", Port);
            await app.WaitForShutdownAsync(ct);
        }, stoppingToken);
    }

    /// <summary>
    /// A listen socket with SO_EXCLUSIVEADDRUSE on Windows (SF3): the bind fails while another socket uses the port,
    /// and no other socket can bind the port (also not a specific address such as 127.0.0.1) while it is open.
    /// </summary>
    private static System.Net.Sockets.Socket CreateExclusiveListenSocket(System.Net.EndPoint endPoint)
    {
        if (endPoint is not System.Net.IPEndPoint ip)
            return Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions.CreateDefaultBoundListenSocket(endPoint);
        var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            if (ip.Address.Equals(System.Net.IPAddress.IPv6Any))
                socket.DualMode = true;
            if (OperatingSystem.IsWindows())
                socket.ExclusiveAddressUse = true;
            socket.Bind(ip);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A failed bind (SF3): find the program that holds the port, log it, show it in the Admin app (registry) and, after
    /// the start gate opened, send one Telegram alert. The service never closes that program (the hard rule).
    /// </summary>
    private async Task OnBindFailedAsync(Exception error)
    {
        _startup?.MarkWebBindTried();
        IReadOnlyList<int> owners;
        try { owners = FindPortOwners(Port); }
        catch { owners = Array.Empty<int>(); }
        var foreign = WebServerRetry.IsForeignOwner(owners, ServicePid);
        if (!foreign)
        {
            _logger.Warning("The web server could not bind port {Port} ({Error}), and no other program listens on it", Port, error.Message);
            return;
        }
        await ReportSquatAsync(owners);
    }

    /// <summary>After a successful bind: a program that still listens on the port is a squat too, else the squat ended.</summary>
    private async Task OnBoundAsync()
    {
        IReadOnlyList<int> owners;
        try { owners = FindPortOwners(Port); }
        catch { owners = Array.Empty<int>(); }
        if (WebServerRetry.IsForeignOwner(owners, ServicePid))
        {
            await ReportSquatAsync(owners);
            return;
        }
        if (_squatAlert.OnBound())
        {
            _logger.Information("The web server listens on port {Port} again. No other program holds the port.", Port);
            LastPortOwner = null;
        }
        RecordPortBlockedSafe(null);
    }

    private async Task ReportSquatAsync(IReadOnlyList<int> owners)
    {
        var owner = string.Join("; ", owners.Where(pid => pid != ServicePid).Select(DescribeProcess));
        LastPortOwner = owner;
        // The owner text comes from an exe: it is untrusted data, only logged and shown.
        _logger.Warning("Another program holds port {Port} of the web server: {Owner}. Parental Control does not close it. " +
                        "The limits stay on, but /api/agent/activity of the Session Agent does not reach the service.", Port, owner);
        RecordPortBlockedSafe(owner);
        if (!_squatAlert.OnBindFailed(foreignOwner: true, gateOpen: _startup?.IsReady ?? true))
            return;
        try
        {
            await (SendPortAlert is { } send ? send(owner) : _telegramBotService.SendWebPortBlockedAlertAsync(owner, DateTime.Now));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not send the Telegram alert about the program on port {Port}", Port);
        }
    }

    private void RecordPortBlockedSafe(string? owner)
    {
        try { RecordPortBlocked(owner); }
        catch (Exception ex) { _logger.Warning(ex, "Could not record the state of port {Port} in the registry", Port); }
    }

    /// <summary>"name (process 123, user PC\Kid)". Each part that Windows does not give is left out.</summary>
    private static string DescribeProcess(int pid)
    {
        string name;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); name = process.ProcessName; }
        catch { name = "unknown program"; }
        string? user = null;
        try
        {
            var sid = TcpConnectionOwner.ReadProcess(pid).UserSid;
            if (sid is not null)
            {
                try { user = new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value; }
                catch { user = sid; }
            }
        }
        catch { }
        return user is null ? $"{name} (process {pid})" : $"{name} (process {pid}, user {user})";
    }

    /// <summary>
    /// Runs <paramref name="runOnce"/> until <paramref name="stoppingToken"/> ends. After an error, and also when
    /// <paramref name="runOnce"/> ends without an error while the service still runs (third review, N-f), it waits
    /// <see cref="RetryDelay"/> and runs it again. Public for the tests.
    /// </summary>
    public async Task RunWithRetryAsync(Func<CancellationToken, Task> runOnce, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Exception? error = null;
            try
            {
                await runOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            if (stoppingToken.IsCancellationRequested)
                return;
            {
                var failed = Interlocked.Increment(ref _failedStarts);
                var delay = RetryDelay(failed);
                if (error is null)
                    _logger.Error("The web server on port {Port} stopped while the service runs (try {Try}). It starts again in {Delay}.",
                        Port, failed, delay);
                else
                    _logger.Error(error, "The web server could not start on port {Port} (try {Try}). It tries again in {Delay}. The limits are still enforced.",
                        Port, failed, delay);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Adds the admin gate and maps all routes. A test can call it on its own <see cref="WebApplication"/>.
    /// </summary>
    public void ConfigureApp(WebApplication app)
    {
        // SF3: until the start gate opens, each route answers 503 and reads nothing from the database.
        app.Use(StartGateAsync);
        // The gate runs after routing, so it sees the selected endpoint, and before the endpoints.
        app.UseRouting();
        app.Use(AdminGateAsync);

        app.MapGet("/", () => Results.Redirect("/request"));

        // User Grace Request Portal
        app.MapGet("/request", async (HttpContext ctx) =>
        {
            var moment = ControlTime.Current.Read(); // control-clock: the day of the Control Time Zone
            var today = moment.Today;
            var dayOfWeek = moment.DayOfWeek;

            var allUsers = UserRepository.GetAll();
            var restrictedUsers = allUsers.Where(u => u.IsRestricted).ToList();

            // Check if specific restricted user was requested via query parameter ?user=...
            var queryUser = ctx.Request.Query["user"].ToString();
            User? targetUser = null;
            if (!string.IsNullOrWhiteSpace(queryUser))
            {
                targetUser = restrictedUsers.FirstOrDefault(u => string.Equals(u.Username, queryUser, StringComparison.OrdinalIgnoreCase));
            }

            var consoleSession = _sessionTracker.GetConsoleSession();
            User? consoleUser = consoleSession != null ? UserRepository.GetBySid(consoleSession.UserSid) : null;

            // If no explicit ?user= query:
            if (targetUser == null)
            {
                // If console user is Administrator or unrestricted, redirect directly to /admin
                if (consoleUser != null && !consoleUser.IsRestricted)
                {
                    return Results.Redirect("/admin");
                }

                // If console user is restricted, target them
                if (consoleUser != null && consoleUser.IsRestricted)
                {
                    targetUser = consoleUser;
                }
            }

            bool isAdminTesting = false;
            if (targetUser == null)
            {
                // Fallback for query testing or preview
                if (restrictedUsers.Count > 0)
                {
                    targetUser = restrictedUsers.FirstOrDefault();
                    isAdminTesting = true;
                }
                else
                {
                    return Results.Redirect("/admin");
                }
            }

            var limit = ScheduleRepository.GetEffectiveLimit(targetUser!.Id, dayOfWeek, today);
            var usage = UsageRepository.GetUsage(targetUser.Id, today);
            var totalAllowed = (limit?.DailyMinutes ?? 120) + (usage?.BonusMinutes ?? 0);
            var used = UsageLedger.Shared.AllowanceUsed(targetUser.Id, today, usage);
            var dailyRemaining = Math.Max(0, totalAllowed - used);

            var sessions = _sessionTracker.ActiveSessions.Values.ToList();
            var activeTargetSession = sessions.FirstOrDefault(s => s.UserSid == targetUser.Sid);
            bool isSessionActive = activeTargetSession != null;
            bool isSessionLocked = activeTargetSession?.IsLocked ?? false;

            var elapsedSec = activeTargetSession != null && !isSessionLocked ? ElapsedTime.Seconds(activeTargetSession.LastTickMono, Environment.TickCount64) : 0;
            var dailySec = Math.Max(0, (dailyRemaining * 60) - Math.Min(59, Math.Max(0, elapsedSec)));

            var nowTime = moment.TimeOfDay.ToTimeSpan();
            var curfewSec = int.MaxValue;
            if (limit != null)
            {
                var startSpan = limit.ScheduleStart.ToTimeSpan();
                var endSpan = limit.ScheduleEnd.ToTimeSpan();
                if (nowTime < startSpan || nowTime >= endSpan)
                {
                    curfewSec = 0;
                }
                else
                {
                    curfewSec = Math.Max(0, (int)(endSpan - nowTime).TotalSeconds);
                }
            }

            var remainingSeconds = Math.Min(dailySec, curfewSec);
            var curfew = limit != null ? $"{limit.ScheduleStart:HH:mm} – {limit.ScheduleEnd:HH:mm}" : "08:00 – 22:00";

            var isOffline = !await TelegramBotService.CheckConnectivityAsync();
            var maxRequests = SettingsRepository.GetMaxDailyRequests();
            var countToday = GraceRequestRepository.GetTodayRequestCount(targetUser.Id, today);
            var hasPending = GraceRequestRepository.HasPendingRequest(targetUser.Id, today);
            var latestReq = GraceRequestRepository.GetTodayRequest(targetUser.Id, today);
            var lastOverallReq = GraceRequestRepository.GetLatestRequest(targetUser.Id);
            string? latestDeclineReason = null;
            DateTime? latestResolvedAt = null;
            if (lastOverallReq != null && string.Equals(lastOverallReq.Status, "DECLINED", StringComparison.OrdinalIgnoreCase))
            {
                latestDeclineReason = lastOverallReq.DeclineReason;
                latestResolvedAt = lastOverallReq.ResolvedAt;
            }

            var availableUsernames = restrictedUsers.Select(u => u.Username).ToList();

            var baseLimit = LimitRepository.GetByUserId(targetUser.Id);
            var customSchedules = ScheduleRepository.GetWeeklySchedule(targetUser.Id);
            var upcomingExceptions = ScheduleExceptionRepository.GetUpcomingForUser(targetUser.Id, today);

            var weeklySchedule = new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
            }.Select(day =>
            {
                var custom = customSchedules.FirstOrDefault(s => s.DayOfWeek == day);
                int mins = custom != null ? custom.DailyMinutes : (baseLimit?.DailyMinutes ?? 120);
                var start = custom != null ? custom.ScheduleStart : (baseLimit?.ScheduleStart ?? new TimeOnly(8, 0));
                var end = custom != null ? custom.ScheduleEnd : (baseLimit?.ScheduleEnd ?? new TimeOnly(22, 0));

                bool hasExc = false;
                if (day == dayOfWeek)
                {
                    var todayExc = upcomingExceptions.FirstOrDefault(e => e.ExceptionDate == today);
                    if (todayExc != null)
                    {
                        hasExc = true;
                        mins = todayExc.DailyMinutes;
                        start = todayExc.ScheduleStart;
                        end = todayExc.ScheduleEnd;
                    }
                }

                return new RequestPageDay(day, mins, $"{start:HH:mm} – {end:HH:mm}", custom != null, day == dayOfWeek, hasExc);
            }).ToList();

            // The page (web/admin/request.html, ADR 0009) shows its texts in the Language Preset. The preset comes
            // only from app_settings; the service gives the page all of its texts.
            var model = RequestPageModel.Build(new RequestPageInput(
                Preset: SettingsRepository.GetLanguagePreset(),
                Username: targetUser.Username,
                RemainingSeconds: remainingSeconds,
                Curfew: curfew,
                IsOffline: isOffline,
                RequestsToday: countToday,
                MaxRequests: maxRequests,
                HasPending: hasPending,
                LatestStatus: latestReq?.Status ?? lastOverallReq?.Status,
                DeclineReason: latestDeclineReason,
                DeclineResolvedAt: latestResolvedAt,
                IsAdminTesting: isAdminTesting,
                AvailableUsers: availableUsernames,
                SessionActive: isSessionActive,
                SessionLocked: isSessionLocked,
                Week: weeklySchedule,
                DefaultMinutes: baseLimit?.DailyMinutes ?? 120,
                DefaultStart: baseLimit?.ScheduleStart ?? new TimeOnly(8, 0),
                DefaultEnd: baseLimit?.ScheduleEnd ?? new TimeOnly(22, 0),
                Today: today));
            return RequestSpa.Page(ctx, model);
        });

        // User Grace Request Submission API
        app.MapPost("/api/request", async (HttpContext ctx) =>
        {
            // The child sees these messages on the request page, so they follow the Language Preset.
            var texts = RequestPageTexts.For(SettingsRepository.GetLanguagePreset());
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                var requestType = doc.RootElement.TryGetProperty("requestType", out var typeProp) ? typeProp.GetString() : "extension";
                var minutes = doc.RootElement.GetProperty("minutes").GetInt32();
                var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";
                var requestedUser = doc.RootElement.TryGetProperty("username", out var uProp) ? uProp.GetString() : null;

                if (string.Equals(requestType, "schedule_change", StringComparison.OrdinalIgnoreCase))
                {
                    if (minutes <= 0 || minutes > 480 || string.IsNullOrWhiteSpace(reason))
                    {
                        return Results.BadRequest(new { error = texts["api_invalid_schedule"] });
                    }
                }
                else
                {
                    if (minutes <= 0 || minutes > 120 || string.IsNullOrWhiteSpace(reason))
                    {
                        return Results.BadRequest(new { error = texts["api_invalid_extension"] });
                    }
                }

                var today = ControlTime.Current.Today;

                var restrictedUsers = UserRepository.GetAll().Where(u => u.IsRestricted).ToList();
                User? user = null;

                if (!string.IsNullOrWhiteSpace(requestedUser))
                {
                    user = restrictedUsers.FirstOrDefault(u => string.Equals(u.Username, requestedUser, StringComparison.OrdinalIgnoreCase));
                }

                if (user == null)
                {
                    var sessions = _sessionTracker.ActiveSessions.Values.ToList();
                    foreach (var s in sessions)
                    {
                        var u = UserRepository.GetBySid(s.UserSid);
                        if (u != null && u.IsRestricted) { user = u; break; }
                    }
                }

                user ??= restrictedUsers.FirstOrDefault();

                if (user == null)
                {
                    return Results.BadRequest(new { error = texts["api_no_user"] });
                }

                // Check daily request limits and pending requests
                var maxRequests = SettingsRepository.GetMaxDailyRequests();
                if (maxRequests <= 0)
                {
                    return Results.BadRequest(new { error = texts["api_disabled"] });
                }

                if (GraceRequestRepository.HasPendingRequest(user.Id, today))
                {
                    return Results.BadRequest(new { error = texts["api_pending"] });
                }

                var countToday = GraceRequestRepository.GetTodayRequestCount(user.Id, today);
                if (countToday >= maxRequests)
                {
                    return Results.BadRequest(new { error = texts.Format("api_max_reached", ("max", maxRequests)) });
                }

                // Check internet
                var isOnline = await TelegramBotService.CheckConnectivityAsync();
                if (!isOnline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                if (string.Equals(requestType, "schedule_change", StringComparison.OrdinalIgnoreCase))
                {
                    var targetDateStr = doc.RootElement.GetProperty("targetDate").GetString();
                    if (!DateOnly.TryParse(targetDateStr, out var targetDate))
                    {
                        return Results.BadRequest(new { error = texts["api_invalid_date"] });
                    }

                    var startStr = doc.RootElement.TryGetProperty("requestedStart", out var sProp) ? sProp.GetString() : null;
                    var endStr = doc.RootElement.TryGetProperty("requestedEnd", out var eProp) ? eProp.GetString() : null;
                    TimeOnly? start = !string.IsNullOrEmpty(startStr) && TimeOnly.TryParse(startStr, out var sVal) ? sVal : null;
                    TimeOnly? end = !string.IsNullOrEmpty(endStr) && TimeOnly.TryParse(endStr, out var eVal) ? eVal : null;

                    var req = GraceRequestRepository.CreateScheduleChange(user.Id, today, targetDate, minutes, start, end, reason);
                    _ = _telegramBotService.SendGraceRequestAlertAsync(req, user.Username);

                    return Results.Ok(new { success = true, message = texts.Format("api_schedule_sent",
                        ("date", SessionTexts.FormatDate(SessionTexts.For(texts.Preset), targetDate)), ("minutes", minutes)) });
                }
                else
                {
                    var req = GraceRequestRepository.Create(user.Id, today, minutes, reason);
                    _ = _telegramBotService.SendGraceRequestAlertAsync(req, user.Username);

                    return Results.Ok(new { success = true, message = texts.Format("api_extension_sent", ("minutes", minutes)) });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process grace request submission");
                return Results.BadRequest(new { error = texts["api_invalid"] });
            }
        });

        // Agent Status API (polled by ParentalControl.Agent running in standard user session)
        app.MapGet("/api/agent/status", (HttpContext ctx) =>
        {
            var username = ctx.Request.Query["user"].ToString();
            if (string.IsNullOrWhiteSpace(username))
            {
                return Results.BadRequest(new { error = "Parameter 'user' harus diisi." });
            }

            var user = UserRepository.GetByUsername(username);
            // The Session Agent shows its texts in this Language Preset. The preset comes only from app_settings.
            var language = SettingsRepository.GetLanguagePreset();

            object? activePromptObj = null;
            var activePrompt = SessionPromptRepository.GetActivePrompt(username);
            if (activePrompt != null)
            {
                List<string> optionsList;
                if (!string.IsNullOrWhiteSpace(activePrompt.CustomOptions))
                {
                    optionsList = activePrompt.CustomOptions.Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                }
                else
                {
                    optionsList = SettingsRepository.GetPromptResponsePresets();
                }

                activePromptObj = new
                {
                    id = activePrompt.Id,
                    message = activePrompt.Message,
                    urgency = activePrompt.Urgency,
                    targetDisplay = activePrompt.TargetDisplay,
                    timeoutSeconds = 120,
                    options = optionsList
                };
            }

            if (user == null || !user.IsRestricted)
            {
                return Results.Ok(new
                {
                    username,
                    isRestricted = false,
                    remainingSeconds = 0,
                    curfew = AgentTexts.For(language)["status_unrestricted"],
                    isLocked = false,
                    language,
                    activePrompt = activePromptObj
                });
            }

            var moment = ControlTime.Current.Read(); // control-clock: the day of the Control Time Zone
            var today = moment.Today;
            var dayOfWeek = moment.DayOfWeek;
            var limit = ScheduleRepository.GetEffectiveLimit(user.Id, dayOfWeek, today);
            var usage = UsageRepository.GetUsage(user.Id, today);
            var totalAllowed = (limit?.DailyMinutes ?? 120) + (usage?.BonusMinutes ?? 0);
            var used = UsageLedger.Shared.AllowanceUsed(user.Id, today, usage);
            var remMinutes = Math.Max(0, totalAllowed - used);

            var sessions = _sessionTracker.ActiveSessions.Values.ToList();
            var session = sessions.FirstOrDefault(s => s.UserSid == user.Sid);
            var isLocked = session?.IsLocked ?? false;
            var elapsedSec = session != null && !isLocked ? ElapsedTime.Seconds(session.LastTickMono, Environment.TickCount64) : 0;
            var dailySec = Math.Max(0, (remMinutes * 60) - Math.Min(59, Math.Max(0, elapsedSec)));

            var nowTime = moment.TimeOfDay.ToTimeSpan();
            var curfewSec = int.MaxValue;
            if (limit != null)
            {
                var startSpan = limit.ScheduleStart.ToTimeSpan();
                var endSpan = limit.ScheduleEnd.ToTimeSpan();
                if (nowTime < startSpan || nowTime >= endSpan)
                {
                    curfewSec = 0;
                }
                else
                {
                    curfewSec = Math.Max(0, (int)(endSpan - nowTime).TotalSeconds);
                }
            }

            var remainingSeconds = Math.Min(dailySec, curfewSec);
            var isCurfewClamped = curfewSec < dailySec;

            var latestReq = GraceRequestRepository.GetLatestRequest(user.Id);
            int? lastDeclinedId = null;
            string? lastDeclinedReason = null;
            DateTime? lastDeclinedTime = null;
            if (latestReq != null && string.Equals(latestReq.Status, "DECLINED", StringComparison.OrdinalIgnoreCase))
            {
                lastDeclinedId = latestReq.Id;
                lastDeclinedReason = latestReq.DeclineReason;
                lastDeclinedTime = latestReq.ResolvedAt;
            }

            var curfew = limit != null ? $"{limit.ScheduleStart:HH:mm} – {limit.ScheduleEnd:HH:mm}" : "08:00 – 22:00";

            var captureRequested = false;
            var watchInterval = 0;
            if (_captureStates.TryGetValue(user.Username, out var capState))
            {
                if (capState.WatchMode && DateTime.Now < capState.WatchExpiresAt)
                {
                    watchInterval = 10;
                }
                if (capState.CaptureRequested)
                {
                    captureRequested = true;
                    capState.CaptureRequested = false;
                }
            }

            // The Suspended Apps (for the resume at the end of the session) and the Pending Suspensions (the agent
            // minimizes their windows). Both name only processes of the child.
            var suspendedApps = SuspensionRepository.GetForUser(user.Id)
                .Select(r => new { pid = r.Pid, startTimeUtc = DateTime.SpecifyKind(r.StartTimeUtc, DateTimeKind.Utc) })
                .ToArray();
            var pendingApps = _categoryEnforcer.GetPendingApps(user.Id)
                .Select(p => new { pid = p.Pid, startTimeUtc = DateTime.SpecifyKind(p.StartTimeUtc, DateTimeKind.Utc) })
                .ToArray();

            return Results.Ok(new
            {
                username = user.Username,
                isRestricted = true,
                remainingSeconds,
                suspendedApps,
                pendingApps,
                curfew,
                isLocked,
                isCurfewClamped,
                lastDeclinedId,
                lastDeclinedReason,
                lastDeclinedTime,
                captureRequested,
                watchIntervalSeconds = watchInterval,
                language,
                activePrompt = activePromptObj
            });
        });

        // Agent Interactive Prompt Response API
        app.MapPost("/api/agent/prompt/respond", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                var promptId = doc.RootElement.GetProperty("promptId").GetInt32();
                var response = doc.RootElement.GetProperty("response").GetString() ?? "YES";
                var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var turnaroundSeconds = doc.RootElement.TryGetProperty("turnaroundSeconds", out var ts) ? ts.GetInt32() : 0;

                var prompt = SessionPromptRepository.GetById(promptId);
                if (prompt == null)
                {
                    return Results.NotFound(new { error = "Pesan prompt tidak ditemukan." });
                }

                var resolved = SessionPromptRepository.ResolvePrompt(promptId, response, reason, turnaroundSeconds);
                if (!resolved)
                {
                    return Results.BadRequest(new { error = "Pesan prompt sudah pernah dijawab." });
                }

                if (response.Equals("YES", StringComparison.OrdinalIgnoreCase))
                {
                    EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_ANSWERED, $"Prompt #{promptId} dijawab (YA) dalam {turnaroundSeconds}s");
                }
                else if (response.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase))
                {
                    EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_TIMEOUT, $"Prompt #{promptId} tidak dijawab (Timeout {turnaroundSeconds}s)");
                }
                else
                {
                    var reasonDesc = !string.IsNullOrWhiteSpace(reason) ? $": \"{reason}\"" : "";
                    EventRepository.LogEvent(prompt.UserSid, EventType.PROMPT_ANSWERED, $"Prompt #{promptId} dijawab (TIDAK){reasonDesc} dalam {turnaroundSeconds}s");
                }

                prompt.Response = response;
                prompt.ResponseReason = reason;
                prompt.TurnaroundSeconds = turnaroundSeconds;
                prompt.Status = response.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase) ? "TIMEOUT" : "ANSWERED";

                var session = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == prompt.UserSid);
                var sessionId = session?.SessionId;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _telegramBotService.SendPromptResponseAlertAsync(prompt, sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to send prompt response alert to Telegram");
                    }
                });

                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing prompt response");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Agent Activity Reporting API (posted by ParentalControl.Agent to record foreground window)
        app.MapPost("/api/agent/activity", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                var username = doc.RootElement.GetProperty("username").GetString();
                var processName = doc.RootElement.GetProperty("processName").GetString();
                var windowTitle = doc.RootElement.TryGetProperty("windowTitle", out var wt) ? wt.GetString() ?? "" : "";
                // Full exe path from a new agent. An old agent sends only the process name. Untrusted text.
                var exePath = doc.RootElement.TryGetProperty("exePath", out var ep) && ep.ValueKind == JsonValueKind.String
                    ? ep.GetString() ?? ""
                    : "";
                var durationSeconds = 20;
                if (doc.RootElement.TryGetProperty("durationSeconds", out var dsProp) && dsProp.TryGetInt32(out var dsVal) && dsVal > 0)
                {
                    durationSeconds = Math.Clamp(dsVal, 1, 300);
                }

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(processName))
                {
                    return Results.BadRequest();
                }

                var user = UserRepository.GetByUsername(username);
                if (user != null && user.IsRestricted)
                {
                    var isSessionLocked = _sessionTracker.ActiveSessions.Values
                        .Any(s => string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase) && s.IsLocked);

                    if (isSessionLocked)
                    {
                        return Results.Ok(new { success = true, skipped = "session_locked" });
                    }

                    _liveActivities[user.Username] = (processName, windowTitle, DateTime.Now);
                    var today = ControlTime.Current.Today;
                    // A failed write of App Usage (the Activity Timeline) is lost; it has no effect on a limit (storage-failure D6).
                    StorageHealth.Shared.TryWrite(() => AppUsageRepository.AddSeconds(user.Id, today, processName, windowTitle, durationSeconds),
                        "app usage", _logger);

                    // Category Usage. The repository never counts System.
                    var resolver = CategoryConfigCache.Current.Resolver;
                    var resolution = string.IsNullOrWhiteSpace(exePath)
                        ? resolver.ResolveFileName(processName)
                        : resolver.Resolve(exePath);
                    _foregroundHistory.Record(user.Id, today, resolution.CanonicalPath);

                    // A Suspended App in front is not use: its frozen window must not take time from the category.
                    var suspended = SuspensionRepository.GetForUser(user.Id);
                    var isSuspendedExe = resolution.CanonicalPath is not null && suspended.Any(r =>
                        string.Equals(WindowsPath.Canonicalize(r.ExePath), resolution.CanonicalPath, StringComparison.OrdinalIgnoreCase));
                    if (!isSuspendedExe)
                    {
                        // The whole minutes go through the ledger: a failed write keeps them in the Pending Usage.
                        var categoryMinutes = CategoryUsageRepository.CollectMinutes(user.Id, today, resolution.Category, durationSeconds);
                        if (categoryMinutes > 0)
                            UsageLedger.Shared.AddCategory(user.Id, today, resolution.Category, categoryMinutes);
                    }

                    // The agent mutes the audio of these processes.
                    var suspendedPids = suspended.Select(r => r.Pid).Distinct().ToArray();
                    return Results.Ok(new { success = true, suspendedPids });
                }

                return Results.Ok(new { success = true, suspendedPids = Array.Empty<int>() });
            }
            catch
            {
                return Results.BadRequest();
            }
        });

        // Session end (posted by the Session Agent from WM_QUERYENDSESSION). The request has no authentication (issue #6):
        // the session must belong to a Restricted User, and the enforcer limits the holds of each user.
        app.MapPost("/api/agent/session-ending", async (HttpContext ctx) =>
        {
            int sessionId;
            string reason;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
                reason = doc.RootElement.TryGetProperty("reason", out var r) && r.GetString() is "shutdown" ? "shutdown" : "logoff";
            }
            catch
            {
                return Results.BadRequest();
            }

            var user = _sessionTracker.ActiveSessions.TryGetValue(sessionId, out var session) ? UserRepository.GetBySid(session.UserSid) : null;
            if (user is null || !user.IsRestricted)
            {
                _logger.Warning("Session end request for session {SessionId} ignored: no session of a Restricted User", sessionId);
                return Results.Ok(new { result = "ignored" });
            }

            var result = _categoryEnforcer.ResumeSessionForEnd(sessionId, user, trusted: false, $"agent {reason}");
            return Results.Ok(new { result = result.ToString() });
        });

        // Agent Silent Screen Capture Upload API
        app.MapPost("/api/agent/capture", async (HttpContext ctx) =>
        {
            var username = ctx.Request.Headers["X-User"].ToString();
            var trigger = ctx.Request.Headers["X-Trigger"].ToString();
            if (string.IsNullOrWhiteSpace(trigger)) trigger = "MANUAL";
            int.TryParse(ctx.Request.Headers["X-Width"], out var width);
            int.TryParse(ctx.Request.Headers["X-Height"], out var height);

            if (string.IsNullOrWhiteSpace(username))
            {
                return Results.BadRequest(new { error = "Header X-User is required." });
            }

            var user = UserRepository.GetByUsername(username);
            if (user == null)
            {
                return Results.NotFound(new { error = "User not found." });
            }

            // CB1 d: a daily quota per user. The route needs no sign-in (#6), so the child can send many files: each
            // file makes the walk at the service start longer, and each byte fills the disk. An upload reserves one
            // file and its largest size under a lock, so parallel uploads cannot pass the quota. A slow upload does
            // not hold the lock.
            CaptureQuotaDecision quota;
            int filesToday;
            long bytesToday;
            lock (_captureReservations)
            {
                (filesToday, bytesToday) = ScreenCaptureRepository.GetDayTotals(user.Id, ControlTime.Current.StartOfToday);
                _captureReservations.TryGetValue(user.Id, out var reserved);
                quota = CaptureQuota.Check(filesToday + reserved.Files, bytesToday + reserved.Bytes);
                if (quota.Allowed)
                    _captureReservations[user.Id] = (reserved.Files + 1, reserved.Bytes + quota.MaxBytes);
            }
            try
            {
                if (!quota.Allowed)
                {
                    if (_captureRefusals.Count(user.Id, ControlTime.Current.Today))
                        _logger.Warning("Capture refused for {Username}: the daily quota is used ({Outcome}: {Files} files, {Bytes} bytes). " +
                                        "Later refusals today are not logged.", user.Username, quota.Outcome, filesToday, bytesToday);
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                var userCapturesDir = Path.Combine(DatabaseManager.CapturesDirectory, user.Id.ToString());
                if (!Directory.Exists(userCapturesDir))
                {
                    Directory.CreateDirectory(userCapturesDir);
                }

                var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg";
                var filePath = Path.Combine(userCapturesDir, fileName);

                long written;
                using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    written = await CopyAtMostAsync(ctx.Request.Body, fs, quota.MaxBytes + 1);
                }
                if (written > quota.MaxBytes || written == 0)
                {
                    try { File.Delete(filePath); } catch { /* the cleanup removes it later */ }
                    if (written == 0)
                        return Results.BadRequest(new { error = "Empty capture." });
                    if (_captureRefusals.Count(user.Id, ControlTime.Current.Today))
                        _logger.Warning("Capture refused for {Username}: the body is larger than {Max} bytes. Later refusals today are not logged.",
                            user.Username, quota.MaxBytes);
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var fi = new FileInfo(filePath);
                var record = new ScreenCaptureRecord
                {
                    UserId = user.Id,
                    Timestamp = ControlTime.Current.Now, // the capture quota counts from midnight of the Control Time Zone
                    FilePath = filePath,
                    Width = width > 0 ? width : 1920,
                    Height = height > 0 ? height : 1080,
                    FileSizeBytes = fi.Length,
                    TriggerType = trigger,
                    AdminIp = ctx.Connection.RemoteIpAddress?.ToString()
                };

                record = ScreenCaptureRepository.Add(record);
                _logger.Information("Screen capture recorded for {Username} ({Width}x{Height}, {Bytes} bytes, Trigger: {Trigger})",
                    user.Username, record.Width, record.Height, record.FileSizeBytes, record.TriggerType);

                OnCaptureReceived?.Invoke(record);

                return Results.Ok(new { id = record.Id, path = record.FilePath });
            }
            finally
            {
                if (quota.Allowed)
                {
                    lock (_captureReservations)
                    {
                        var reserved = _captureReservations[user.Id];
                        reserved = (reserved.Files - 1, reserved.Bytes - quota.MaxBytes);
                        if (reserved.Files <= 0)
                            _captureReservations.Remove(user.Id);
                        else
                            _captureReservations[user.Id] = reserved;
                    }
                }
            }
        });

        // Web Admin Page: the single-page app of web/admin (ADR 0009). The service selects its view: the setup text
        // before the setup, the refused text for a process of a Restricted User, else the login form or the dashboard.
        app.MapGet("/admin", (HttpContext ctx) =>
        {
            if (!_adminAccess.IsSetUp)
                return AdminSpa.Page(ctx, AdminSpa.ViewSetup);
            if (_adminAccess.CheckClient(ctx.Connection) != LocalClientDecision.Allow)
                return AdminSpa.Page(ctx, AdminSpa.ViewRefused, StatusCodes.Status403Forbidden);
            if (!_adminAccess.IsSessionValid(ctx))
                return AdminSpa.Page(ctx, AdminSpa.ViewLogin);
            return AdminSpa.Page(ctx, AdminSpa.ViewApp);
        });
        app.MapGet("/admin/assets/{file}", (HttpContext ctx, string file) => AdminSpa.Asset(ctx, file));

        // Web Admin Login API. The gate has checked the Origin.
        app.MapPost("/api/admin/login", async (HttpContext ctx) =>
        {
            var code = await ReadLoginCodeAsync(ctx);
            var result = _adminAccess.TryLogin(code, ctx.Connection);
            switch (result.Outcome)
            {
                case AdminLoginOutcome.Success:
                    // A session cookie (no Expires): the browser drops it when it closes. The server ends the Admin
                    // Session after 12 hours in any case (AdminSessionStore).
                    ctx.Response.Cookies.Append(AdminAccessService.CookieName, result.Token!, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict
                    });
                    return Results.Redirect("/admin");
                case AdminLoginOutcome.NotSetUp:
                    return LoginRefusal(ctx, StatusCodes.Status401Unauthorized, "notSetUp", AdminAccessTexts.SetupText,
                        () => AdminPage.RenderSetupNeeded());
                case AdminLoginOutcome.RefusedClient:
                    return LoginRefusal(ctx, StatusCodes.Status403Forbidden, "refused", AdminAccessTexts.LocalClientRefused);
                case AdminLoginOutcome.Blocked:
                    var until = (result.Limit!.BlockedUntilUtc ?? DateTimeOffset.UtcNow).ToLocalTime().DateTime;
                    return LoginRefusal(ctx, StatusCodes.Status429TooManyRequests, "blocked", AdminAccessTexts.Blocked(result.Limit.Scope, until),
                        blockedUntil: AdminAccessTexts.FormatTime(until));
                default:
                    return LoginRefusal(ctx, StatusCodes.Status401Unauthorized, "invalid", AdminAccessTexts.InvalidCode);
            }
        });

        // Logout is a POST (a GET gets 405). The gate has checked the Origin.
        app.MapPost("/api/admin/logout", (HttpContext ctx) =>
        {
            _adminAccess.Logout(ctx);
            ctx.Response.Cookies.Delete(AdminAccessService.CookieName);
            return Results.Redirect("/admin");
        });

        // Web Admin Dashboard JSON API
        app.MapGet("/api/admin/dashboard", (HttpContext ctx) =>
        {
            var moment = ControlTime.Current.Read(); // control-clock: the day of the Control Time Zone
            var today = moment.Today;
            var dayOfWeek = moment.DayOfWeek;
            var users = UserRepository.GetAll().ToDictionary(u => u.Id, u => u);
            var restrictedUsers = users.Values.Where(u => u.IsRestricted).ToList();

            // Query parameters for ActivityWatch timeline
            var filterUser = ctx.Request.Query["user"].ToString();
            var filterDateStr = ctx.Request.Query["date"].ToString();
            var filterRange = ctx.Request.Query["range"].ToString(); // "day" or "week"

            var targetDate = today;
            if (!string.IsNullOrWhiteSpace(filterDateStr) && DateOnly.TryParse(filterDateStr, out var parsedDate))
            {
                targetDate = parsedDate;
            }

            var fromDate = string.Equals(filterRange, "week", StringComparison.OrdinalIgnoreCase)
                ? targetDate.AddDays(-6)
                : targetDate;

            int? targetUserId = null;
            if (!string.IsNullOrWhiteSpace(filterUser) && !string.Equals(filterUser, "all", StringComparison.OrdinalIgnoreCase))
            {
                var matched = restrictedUsers.FirstOrDefault(u => string.Equals(u.Username, filterUser, StringComparison.OrdinalIgnoreCase));
                if (matched != null) targetUserId = matched.Id;
            }

            var sessionList = new List<object>();
            foreach (var (sessionId, activeSession) in _sessionTracker.ActiveSessions)
            {
                var user = UserRepository.GetBySid(activeSession.UserSid);
                if (user == null) continue;
                var isRestricted = user.IsRestricted;
                var limit = isRestricted ? ScheduleRepository.GetEffectiveLimit(user.Id, dayOfWeek, today) : null;
                var usage = isRestricted ? UsageRepository.GetUsage(user.Id, today) : null;
                var totalAllowed = limit != null ? (limit.DailyMinutes + (usage?.BonusMinutes ?? 0)) : 0;
                var used = isRestricted ? UsageLedger.Shared.AllowanceUsed(user.Id, today, usage) : 0;
                var remaining = limit != null ? Math.Max(0, totalAllowed - used) : 0;

                string currentApp = "Inactive / Idle";
                bool isAppLive = false;
                if (_liveActivities.TryGetValue(activeSession.Username, out var live))
                {
                    if (DateTime.Now - live.LastSeen < TimeSpan.FromMinutes(2))
                    {
                        currentApp = string.IsNullOrWhiteSpace(live.WindowTitle)
                            ? live.ProcessName
                            : $"{live.ProcessName} — {live.WindowTitle}";
                        isAppLive = true;
                    }
                }

                sessionList.Add(new
                {
                    sessionId,
                    username = activeSession.Username,
                    userId = user.Id,
                    isRestricted,
                    isLocked = activeSession.IsLocked,
                    remainingMinutes = remaining,
                    totalAllowed,
                    curfew = limit != null ? $"{limit.ScheduleStart:HH:mm}–{limit.ScheduleEnd:HH:mm}" : "None",
                    currentApp,
                    isAppLive
                });
            }

            var recentRequests = GraceRequestRepository.GetRecentRequests(10)
                .Select(r => new
                {
                    r.Id,
                    username = users.TryGetValue(r.UserId, out var u) ? u.Username : $"User #{r.UserId}",
                    r.RequestedMinutes,
                    r.Reason,
                    r.Status,
                    createdAt = r.CreatedAt.ToString("HH:mm:ss")
                });

            // Hourly distribution for 24-hour timeline bar
            var hourlyDistribution = AppUsageRepository.GetHourlyDistribution(targetUserId, fromDate, targetDate);

            // Detailed app usage for selected user & date range
            var usageRecords = AppUsageRepository.GetUsageForRange(targetUserId, fromDate, targetDate);
            var totalMinutes = usageRecords.Sum(r => r.Minutes);

            var categoryResolver = CategoryConfigCache.Current.Resolver;
            var appUsageList = usageRecords.Select(r => new
            {
                // The table has only the process name: show the category of a file-name rule, else null.
                fileRuleCategory = categoryResolver.ResolveFileName(r.ProcessName) is { Rule.MatchType: RuleMatchType.File } res
                    ? res.Category.ToString()
                    : null,
                userId = r.UserId,
                username = users.TryGetValue(r.UserId, out var u) ? u.Username : $"User #{r.UserId}",
                processName = r.ProcessName,
                windowTitle = r.WindowTitle,
                minutes = r.Minutes,
                percentage = totalMinutes > 0 ? (int)Math.Round((double)r.Minutes / totalMinutes * 100) : 0
            }).ToList();

            var availableUsers = restrictedUsers.Select(u => u.Username).ToList();
            var managedUsers = restrictedUsers.Select(u =>
            {
                var baseLim = LimitRepository.GetByUserId(u.Id);
                var customList = ScheduleRepository.GetWeeklySchedule(u.Id);
                return new
                {
                    id = u.Id,
                    username = u.Username,
                    baseMinutes = baseLim?.DailyMinutes ?? 120,
                    baseStart = baseLim != null ? baseLim.ScheduleStart.ToString("HH:mm") : "08:00",
                    baseEnd = baseLim != null ? baseLim.ScheduleEnd.ToString("HH:mm") : "22:00",
                    customCount = customList.Count
                };
            }).ToList();

            return Results.Ok(new
            {
                sessions = sessionList,
                requests = recentRequests,
                managedUsers,
                appUsage = appUsageList,
                hourlyTimeline = hourlyDistribution,
                totalMinutes,
                availableUsers,
                selectedUser = string.IsNullOrWhiteSpace(filterUser) ? "all" : filterUser,
                selectedDate = targetDate.ToString("yyyy-MM-dd"),
                selectedRange = string.Equals(filterRange, "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day"
            });
        });

        // Web Admin Grant Time API
        app.MapPost("/api/admin/grant", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var userId = doc.RootElement.GetProperty("userId").GetInt32();
            var minutes = doc.RootElement.GetProperty("minutes").GetInt32();

            var today = ControlTime.Current.Today;
            UsageRepository.AddBonusMinutes(userId, today, minutes);
            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == userId);
            if (user != null)
            {
                EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Web Admin granted +{minutes}m bonus");
                var active = SessionManager.GetActiveSessions().FirstOrDefault(s => s.Sid == user.Sid);
                if (active.Sid != null)
                {
                    var msg = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBonusGranted, new Dictionary<string, string>
                    {
                        ["minutes"] = minutes.ToString()
                    });
                    NotificationManager.SendMessage(active.SessionId, "Parental Control", msg, false, 15);
                }
            }

            return Results.Ok(new { success = true });
        });

        // Web Admin Force Logoff API
        app.MapPost("/api/admin/force-logoff", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();

            NotificationManager.SendMessage(sessionId, "Parental Control", SessionTexts.For(SettingsRepository.GetLanguagePreset())["session_ended"], true, 5);

            // A Suspended App cannot answer the end-session query: resume it first (trusted hold, no rate limit).
            var logoffUser = _sessionTracker.ActiveSessions.TryGetValue(sessionId, out var logoffSession)
                ? UserRepository.GetBySid(logoffSession.UserSid) : null;
            if (logoffUser is { IsRestricted: true })
                _categoryEnforcer.ResumeSessionForEnd(sessionId, logoffUser, trusted: true, "forced logoff");

            SessionManager.ForceLogoff(sessionId);
            _sessionTracker.RemoveSession(sessionId);

            return Results.Ok(new { success = true });
        });

        // Web Admin Lock Session with Pre-Warning API
        app.MapPost("/api/admin/lock-session", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
                var message = doc.RootElement.TryGetProperty("message", out var mProp) ? mProp.GetString() : "";
                var seconds = doc.RootElement.TryGetProperty("seconds", out var sProp) ? sProp.GetInt32() : 15;

                if (seconds < 5) seconds = 5;
                if (seconds > 60) seconds = 60;

                if (string.IsNullOrWhiteSpace(message))
                {
                    message = SettingsRepository.GetMessage(SettingsRepository.KeyMsgRemoteLock);
                }

                var sessionUser = _sessionTracker.ActiveSessions.TryGetValue(sessionId, out var s) ? s.Username : $"Session {sessionId}";

                _logger.Information("Lock requested for session {SessionId} ({Username}) with {Seconds}s countdown: {Message}",
                    sessionId, sessionUser, seconds, message);

                // 1. Send unskippable native Win32 popup into session with countdown duration
                var lockTexts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
                var popupText = $"{message}\n\n{lockTexts.Format("lock_countdown", ("seconds", seconds))}";
                NotificationManager.SendMessage(sessionId, lockTexts["lock_title"], popupText, isWarning: true, timeoutSeconds: seconds);

                // 2. Schedule lock after countdown so user has 10-15s to view/save
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    SessionManager.LockSession(sessionId);
                    _sessionTracker.OnSessionLock(sessionId, $"Remote lock from Web Admin ({seconds}s delay): {message}");
                });

                return Results.Ok(new { success = true, message = $"Peringatan dikirim ke {sessionUser}. Layar akan dikunci dalam {seconds} detik." });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute lock session request");
                return Results.BadRequest(new { error = "Invalid lock request payload." });
            }
        });

        // Web Admin Dispatch Interactive Prompt API
        app.MapPost("/api/admin/prompt/send", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var username = doc.RootElement.GetProperty("username").GetString() ?? "";
                var message = doc.RootElement.GetProperty("message").GetString() ?? "";
                var urgency = doc.RootElement.TryGetProperty("urgency", out var uProp) ? uProp.GetString() ?? "NORMAL" : "NORMAL";
                var targetDisplay = doc.RootElement.TryGetProperty("targetDisplay", out var dProp) ? dProp.GetInt32() : -1;
                var customOptions = doc.RootElement.TryGetProperty("customOptions", out var cProp) ? cProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(message))
                {
                    return Results.BadRequest(new { error = "Pengguna dan pesan harus diisi." });
                }

                var user = UserRepository.GetByUsername(username)
                    ?? UserRepository.GetAll().FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    return Results.NotFound(new { error = $"Pengguna '{username}' tidak ditemukan." });
                }

                var promptId = SessionPromptRepository.Create(user.Sid, user.Username, message, urgency, targetDisplay, customOptions);
                EventRepository.LogEvent(user.Sid, EventType.PROMPT_SENT, $"Pesan interaktif dikirim ke {user.Username}: \"{message}\" (Prioritas: {urgency})");

                var activeSession = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == user.Sid && !s.IsLocked);
                if (activeSession != null)
                {
                    _sessionTracker.EnsureAgentRunning(activeSession.SessionId);

                    if (!_sessionTracker.IsAgentRunning(activeSession.SessionId))
                    {
                        var popupTexts = SessionTexts.For(SettingsRepository.GetLanguagePreset());
                        var popupTitle = urgency == "URGENT" ? popupTexts["message_urgent_title"] : popupTexts["message_title"];
                        NotificationManager.SendMessage(activeSession.SessionId, popupTitle, message, urgency == "URGENT", timeoutSeconds: 30);
                    }
                }

                return Results.Ok(new { success = true, promptId });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to send prompt to user");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Web Admin Prompt History API
        app.MapGet("/api/admin/prompts", (HttpContext ctx) =>
        {
            return Results.Ok(SessionPromptRepository.GetHistory(20));
        });
        // Web Admin Resolve Request API
        app.MapPost("/api/admin/resolve-request", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var requestId = doc.RootElement.GetProperty("requestId").GetInt32();
            var action = doc.RootElement.GetProperty("action").GetString();
            var minutes = doc.RootElement.TryGetProperty("minutes", out var m) ? m.GetInt32() : 0;

            var req = GraceRequestRepository.GetById(requestId);
            if (req == null) return Results.NotFound();

            if (action == "approve")
            {
                GraceRequestRepository.Resolve(requestId, "APPROVED");
                UsageRepository.AddBonusMinutes(req.UserId, req.Date, minutes);
                var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
                if (user != null)
                {
                    EventRepository.LogEvent(user.Sid, EventType.LOGIN, $"Web Admin approved Request #{requestId} (+{minutes}m)");
                    var active = SessionManager.GetActiveSessions().FirstOrDefault(s => s.Sid == user.Sid);
                    if (active.Sid != null)
                    {
                        var msg = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBonusGranted, new Dictionary<string, string>
                        {
                            ["minutes"] = minutes.ToString()
                        });
                        NotificationManager.SendMessage(active.SessionId, "Parental Control", msg, false, 15);
                    }
                }
            }
            else
            {
                GraceRequestRepository.Resolve(requestId, "DECLINED");
                var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == req.UserId);
                if (user != null)
                {
                    EventRepository.LogEvent(user.Sid, EventType.LOGIN_DENIED, $"Web Admin declined Request #{requestId}");
                }
            }

            return Results.Ok(new { success = true });
        });

        // Admin Trigger Capture API (Single-shot or Watch mode)
        app.MapPost("/api/admin/capture/request", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(body);
            var username = doc.RootElement.GetProperty("username").GetString();
            var mode = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "single" : "single";
            var enableWatch = doc.RootElement.TryGetProperty("enableWatch", out var ew) && ew.GetBoolean();

            if (string.IsNullOrWhiteSpace(username)) return Results.BadRequest(new { error = "Parameter 'username' harus diisi." });

            var user = UserRepository.GetByUsername(username);
            if (user == null) return Results.NotFound(new { error = "User tidak ditemukan." });

            var session = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == user.Sid);
            if (session == null || session.IsLocked)
            {
                return Results.BadRequest(new { error = $"Sesi {username} sedang terkunci atau tidak aktif di desktop." });
            }

            if (mode == "watch")
            {
                var state = _captureStates.GetOrAdd(user.Username, _ => new CaptureCommandState());
                state.WatchMode = enableWatch;
                state.CaptureRequested = enableWatch;
                state.WatchExpiresAt = enableWatch ? DateTime.Now.AddMinutes(10) : DateTime.MinValue;
                state.Trigger = "WATCH";
                return Results.Ok(new { success = true, watchMode = enableWatch });
            }
            else
            {
                var state = _captureStates.GetOrAdd(user.Username, _ => new CaptureCommandState());
                state.CaptureRequested = true;
                state.Trigger = "MANUAL";
                return Results.Ok(new { success = true, captureRequested = true });
            }
        });

        // Admin Heartbeat for Watch Mode (extends timeout)
        app.MapPost("/api/admin/capture/heartbeat", (HttpContext ctx) =>
        {
            var username = ctx.Request.Query["user"].ToString();
            if (!string.IsNullOrWhiteSpace(username) && _captureStates.TryGetValue(username, out var cs) && cs.WatchMode)
            {
                cs.WatchExpiresAt = DateTime.Now.AddMinutes(10);
            }
            return Results.Ok(new { success = true });
        });

        // Admin Get Latest Capture API
        app.MapGet("/api/admin/captures/latest", (HttpContext ctx) =>
        {
            var username = ctx.Request.Query["user"].ToString();
            if (string.IsNullOrWhiteSpace(username) || username == "all")
            {
                var firstRestricted = UserRepository.GetAll().FirstOrDefault(u => u.IsRestricted);
                if (firstRestricted == null) return Results.Ok(new { capture = (object?)null });
                username = firstRestricted.Username;
            }

            var user = UserRepository.GetByUsername(username);
            if (user == null) return Results.NotFound();

            var latest = ScreenCaptureRepository.GetLatestByUser(user.Id);
            if (latest == null) return Results.Ok(new { capture = (object?)null });

            return Results.Ok(new
            {
                capture = new
                {
                    id = latest.Id,
                    userId = latest.UserId,
                    username = user.Username,
                    timestamp = latest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    width = latest.Width,
                    height = latest.Height,
                    fileSizeBytes = latest.FileSizeBytes,
                    triggerType = latest.TriggerType,
                    url = $"/api/admin/captures/{latest.Id}"
                }
            });
        });

        // Admin Get Recent Captures API
        app.MapGet("/api/admin/captures/recent", (HttpContext ctx) =>
        {
            var username = ctx.Request.Query["user"].ToString();
            int.TryParse(ctx.Request.Query["limit"], out var limit);
            if (limit <= 0) limit = 10;

            if (string.IsNullOrWhiteSpace(username) || username == "all")
            {
                var firstRestricted = UserRepository.GetAll().FirstOrDefault(u => u.IsRestricted);
                if (firstRestricted == null) return Results.Ok(new { captures = Array.Empty<object>() });
                username = firstRestricted.Username;
            }

            var user = UserRepository.GetByUsername(username);
            if (user == null) return Results.NotFound();

            var recent = ScreenCaptureRepository.GetRecentByUser(user.Id, limit);
            var list = recent.Select(r => new
            {
                id = r.Id,
                userId = r.UserId,
                username = user.Username,
                timestamp = r.Timestamp.ToString("HH:mm:ss"),
                date = r.Timestamp.ToString("yyyy-MM-dd"),
                width = r.Width,
                height = r.Height,
                fileSizeBytes = r.FileSizeBytes,
                triggerType = r.TriggerType,
                url = $"/api/admin/captures/{r.Id}"
            }).ToList();

            return Results.Ok(new { captures = list });
        });

        // Admin Serve Capture JPEG Image
        app.MapGet("/api/admin/captures/{id:int}", (HttpContext ctx, int id) =>
        {
            var record = ScreenCaptureRepository.GetById(id);
            if (record == null || !File.Exists(record.FilePath))
            {
                return Results.NotFound();
            }

            return Results.File(record.FilePath, "image/jpeg");
        });

        // Admin Push Capture to Telegram
        app.MapPost("/api/admin/capture/telegram", async (HttpContext ctx) =>
        {
            int.TryParse(ctx.Request.Query["id"], out var id);
            var record = ScreenCaptureRepository.GetById(id);
            if (record == null || !File.Exists(record.FilePath))
            {
                return Results.NotFound(new { error = "Tangkapan layar tidak ditemukan." });
            }

            var chatId = SettingsRepository.Get(SettingsRepository.KeyTelegramChatId);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                return Results.BadRequest(new { error = "Telegram Chat ID belum dikonfigurasi di Settings." });
            }

            var user = UserRepository.GetAll().FirstOrDefault(u => u.Id == record.UserId);
            var photoBytes = await File.ReadAllBytesAsync(record.FilePath);
            var caption = $"📸 *Tangkapan Layar Desktop* ({user?.Username ?? "User"})\n⏰ Waktu: `{record.Timestamp:yyyy-MM-dd HH:mm:ss}`\n📏 Resolusi: {record.Width}x{record.Height}\n🏷️ Trigger: {record.TriggerType}";

            var success = await _telegramBotService.SendPhotoAsync(chatId, photoBytes, caption);
            return Results.Ok(new { success });
        });

        // Admin Schedule Management GET API
        app.MapGet("/api/admin/schedule", (HttpContext ctx) =>
        {
            var userIdStr = ctx.Request.Query["userId"].ToString();
            if (!int.TryParse(userIdStr, out var userId))
            {
                return Results.BadRequest(new { error = "Invalid userId" });
            }

            var user = UserRepository.GetById(userId);
            if (user == null)
            {
                return Results.NotFound(new { error = "User not found" });
            }

            var baseLimit = LimitRepository.GetByUserId(userId);
            var customSchedules = ScheduleRepository.GetWeeklySchedule(userId);

            var dayNames = new (DayOfWeek Day, string Name)[]
            {
                (DayOfWeek.Monday, "Senin"),
                (DayOfWeek.Tuesday, "Selasa"),
                (DayOfWeek.Wednesday, "Rabu"),
                (DayOfWeek.Thursday, "Kamis"),
                (DayOfWeek.Friday, "Jumat"),
                (DayOfWeek.Saturday, "Sabtu"),
                (DayOfWeek.Sunday, "Minggu")
            };

            var baseMinutes = baseLimit?.DailyMinutes ?? 120;
            var baseStart = baseLimit != null ? baseLimit.ScheduleStart.ToString("HH:mm") : "08:00";
            var baseEnd = baseLimit != null ? baseLimit.ScheduleEnd.ToString("HH:mm") : "22:00";

            var days = dayNames.Select(dn =>
            {
                var custom = customSchedules.FirstOrDefault(s => s.DayOfWeek == dn.Day);
                return new
                {
                    dayOfWeek = (int)dn.Day,
                    dayName = dn.Name,
                    isCustom = custom != null,
                    dailyMinutes = custom?.DailyMinutes ?? baseMinutes,
                    scheduleStart = custom != null ? custom.ScheduleStart.ToString("HH:mm") : baseStart,
                    scheduleEnd = custom != null ? custom.ScheduleEnd.ToString("HH:mm") : baseEnd
                };
            }).ToList();

            var today = ControlTime.Current.Today;
            var upcomingExceptions = ScheduleExceptionRepository.GetUpcomingForUser(userId, today)
                .Select(e => new
                {
                    id = e.Id,
                    date = e.ExceptionDate.ToString("yyyy-MM-dd"),
                    dailyMinutes = e.DailyMinutes,
                    scheduleStart = e.ScheduleStart.ToString("HH:mm"),
                    scheduleEnd = e.ScheduleEnd.ToString("HH:mm")
                }).ToList();

            return Results.Ok(new
            {
                userId = user.Id,
                username = user.Username,
                baseDailyMinutes = baseMinutes,
                baseScheduleStart = baseStart,
                baseScheduleEnd = baseEnd,
                days,
                upcomingExceptions
            });
        });
        // Admin Schedule Exception Add/Upsert POST API
        app.MapPost("/api/admin/schedule-exception", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var userId = root.GetProperty("userId").GetInt32();
                var dateStr = root.GetProperty("date").GetString();
                if (!DateOnly.TryParse(dateStr, out var date))
                    return Results.BadRequest(new { error = "Tanggal tidak valid." });

                var minutes = root.GetProperty("dailyMinutes").GetInt32();
                var startStr = root.GetProperty("scheduleStart").GetString() ?? "08:00";
                var endStr = root.GetProperty("scheduleEnd").GetString() ?? "22:00";
                if (!TimeOnly.TryParse(startStr, out var start) || !TimeOnly.TryParse(endStr, out var end) || start >= end)
                    return Results.BadRequest(new { error = "Jam batas tidak valid." });
                if (minutes < 1 || minutes > 1440)
                    return Results.BadRequest(new { error = "Menit harian harus antara 1 dan 1440." });

                ScheduleExceptionRepository.Upsert(new ScheduleException
                {
                    UserId = userId,
                    ExceptionDate = date,
                    DailyMinutes = minutes,
                    ScheduleStart = start,
                    ScheduleEnd = end,
                    CreatedAt = DateTime.Now
                });

                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Admin Schedule Exception Delete API
        app.MapDelete("/api/admin/schedule-exception", (HttpContext ctx) =>
        {
            var idStr = ctx.Request.Query["id"].ToString();
            if (!int.TryParse(idStr, out var id))
                return Results.BadRequest(new { error = "ID tidak valid." });

            ScheduleExceptionRepository.Delete(id);
            return Results.Ok(new { success = true });
        });


        // Admin Schedule Management POST API
        app.MapPost("/api/admin/schedule", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var userId = root.GetProperty("userId").GetInt32();
                var baseMinutes = root.GetProperty("baseDailyMinutes").GetInt32();
                var baseStartStr = root.GetProperty("baseScheduleStart").GetString() ?? "08:00";
                var baseEndStr = root.GetProperty("baseScheduleEnd").GetString() ?? "22:00";

                if (!TimeOnly.TryParse(baseStartStr, out var baseStart) || !TimeOnly.TryParse(baseEndStr, out var baseEnd) || baseStart >= baseEnd)
                {
                    return Results.BadRequest(new { error = "Jam batas standar tidak valid (Format HH:mm, jam mulai harus lebih awal dari jam selesai)." });
                }
                if (baseMinutes < 1 || baseMinutes > 1440)
                {
                    return Results.BadRequest(new { error = "Menit harian harus antara 1 dan 1440." });
                }

                // Save base limits
                LimitRepository.Upsert(new LimitConfig
                {
                    UserId = userId,
                    DailyMinutes = baseMinutes,
                    ScheduleStart = baseStart,
                    ScheduleEnd = baseEnd
                });

                // Process 7 days
                if (root.TryGetProperty("days", out var daysElement) && daysElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in daysElement.EnumerateArray())
                    {
                        var dayOfWeek = (DayOfWeek)d.GetProperty("dayOfWeek").GetInt32();
                        var isCustom = d.GetProperty("isCustom").GetBoolean();
                        if (isCustom)
                        {
                            var dMinutes = d.GetProperty("dailyMinutes").GetInt32();
                            var dStartStr = d.GetProperty("scheduleStart").GetString() ?? "08:00";
                            var dEndStr = d.GetProperty("scheduleEnd").GetString() ?? "22:00";
                            if (TimeOnly.TryParse(dStartStr, out var dStart) && TimeOnly.TryParse(dEndStr, out var dEnd) && dStart < dEnd)
                            {
                                ScheduleRepository.SaveDaySchedule(new DaySchedule
                                {
                                    UserId = userId,
                                    DayOfWeek = dayOfWeek,
                                    DailyMinutes = Math.Clamp(dMinutes, 1, 1440),
                                    ScheduleStart = dStart,
                                    ScheduleEnd = dEnd
                                });
                            }
                        }
                        else
                        {
                            ScheduleRepository.DeleteDaySchedule(userId, dayOfWeek);
                        }
                    }
                }

                return Results.Ok(new { success = true, message = "Jadwal berhasil disimpan." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Admin Check Updates API
        app.MapGet("/api/admin/update/check", async (HttpContext ctx) =>
        {
            var res = await UpdateService.CheckForUpdatesAsync();
            return Results.Ok(new
            {
                currentVersion = AppVersion.DisplayName,
                latestVersion = res.LatestVersion,
                hasUpdate = res.HasUpdate,
                releaseTitle = res.ReleaseTitle,
                releaseNotes = res.ReleaseNotes,
                downloadUrl = res.DownloadUrl,
                error = res.Error
            });
        });

        // Admin Apply Update API
        // The service ignores the request body (old clients send a downloadUrl). It reads the
        // latest release of AppVersion.GitHubRepo itself and runs only a checked installer (issue #8).
        app.MapPost("/api/admin/update/apply", async (HttpContext ctx) =>
        {
            var (success, msg) = await UpdateService.ApplyLatestUpdateAsync();
            return Results.Ok(new { success, message = msg });
        });

        // Admin Settings GET API
        app.MapGet("/api/admin/settings", (HttpContext ctx) =>
        {
            return Results.Ok(new
            {
                telegramConfigured = !string.IsNullOrWhiteSpace(SettingsRepository.Get(SettingsRepository.KeyTelegramBotToken)) &&
                                     !string.IsNullOrWhiteSpace(SettingsRepository.Get(SettingsRepository.KeyTelegramChatId)),
                telegramNotifySignIn = SettingsRepository.IsTelegramNotifySignInEnabled(),
                telegramNotifySignOut = SettingsRepository.IsTelegramNotifySignOutEnabled(),
                telegramNotifyAdminLogon = SettingsRepository.IsTelegramNotifyAdminLogonEnabled(),
                promptResponsePresets = SettingsRepository.GetMessage(SettingsRepository.KeyPromptResponsePresets),
                msgCategoryWarn = SettingsRepository.GetMessage(SettingsRepository.KeyMsgCategoryWarn),
                msgCategorySuspended = SettingsRepository.GetMessage(SettingsRepository.KeyMsgCategorySuspended),
                telegramNotifyBreak = SettingsRepository.IsTelegramNotifyBreakEnabled(),
                msgBreakWarn = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBreakWarn),
                msgBreakStart = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBreakStart),
                msgBreakRunning = SettingsRepository.GetMessage(SettingsRepository.KeyMsgBreakRunning),
                msgProgramBlocked = SettingsRepository.GetMessage(SettingsRepository.KeyMsgProgramBlocked),
                notifyBlockedTelegram = SettingsRepository.IsNotifyBlockedTelegramEnabled()
            });
        });

        // Admin Settings POST API
        app.MapPost("/api/admin/settings", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("telegramNotifySignIn", out var si))
                {
                    SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignIn, si.GetBoolean() ? "true" : "false");
                }
                if (doc.RootElement.TryGetProperty("telegramNotifySignOut", out var so))
                {
                    SettingsRepository.Set(SettingsRepository.KeyTelegramNotifySignOut, so.GetBoolean() ? "true" : "false");
                }
                if (doc.RootElement.TryGetProperty("telegramNotifyAdminLogon", out var al))
                {
                    SettingsRepository.Set(SettingsRepository.KeyTelegramNotifyAdminLogon, al.GetBoolean() ? "true" : "false");
                }
                if (doc.RootElement.TryGetProperty("promptResponsePresets", out var pr))
                {
                    SettingsRepository.Set(SettingsRepository.KeyPromptResponsePresets, pr.GetString() ?? string.Empty); // An empty list gives the default reasons of the preset.
                }
                // An empty text keeps the old one.
                if (doc.RootElement.TryGetProperty("msgCategoryWarn", out var mcw) && !string.IsNullOrWhiteSpace(mcw.GetString()))
                {
                    SettingsRepository.Set(SettingsRepository.KeyMsgCategoryWarn, mcw.GetString()!.Trim());
                }
                if (doc.RootElement.TryGetProperty("msgCategorySuspended", out var mcs) && !string.IsNullOrWhiteSpace(mcs.GetString()))
                {
                    SettingsRepository.Set(SettingsRepository.KeyMsgCategorySuspended, mcs.GetString()!.Trim());
                }
                if (doc.RootElement.TryGetProperty("telegramNotifyBreak", out var nb))
                {
                    SettingsRepository.Set(SettingsRepository.KeyTelegramNotifyBreak, nb.GetBoolean() ? "true" : "false");
                }
                foreach (var (field, key) in new[]
                {
                    ("msgBreakWarn", SettingsRepository.KeyMsgBreakWarn),
                    ("msgBreakStart", SettingsRepository.KeyMsgBreakStart),
                    ("msgBreakRunning", SettingsRepository.KeyMsgBreakRunning),
                })
                {
                    if (doc.RootElement.TryGetProperty(field, out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                        SettingsRepository.Set(key, text.GetString()!.Trim());
                }
                if (doc.RootElement.TryGetProperty("msgProgramBlocked", out var mpb) && !string.IsNullOrWhiteSpace(mpb.GetString()))
                {
                    SettingsRepository.Set(SettingsRepository.KeyMsgProgramBlocked, mpb.GetString()!.Trim());
                }
                if (doc.RootElement.TryGetProperty("notifyBlockedTelegram", out var nbt))
                {
                    SettingsRepository.Set(SettingsRepository.KeyNotifyBlockedTelegram, nbt.GetBoolean() ? "1" : "0");
                }

                return Results.Ok(new { success = true });
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid payload" });
            }
        });

        // ═══ App Categories: Category Limits and App Rules (admin only) ═══
        app.MapGet("/api/admin/categories", (HttpContext ctx) =>
        {
            var limits = CategoryRepository.GetLimits();
            var today = ControlTime.Current.Today;
            var usageToday = UserRepository.GetAll().Where(u => u.IsRestricted).SelectMany(u =>
                UsageLedger.Shared.CategoryUsage(u.Id, today).Select(kv => new
                {
                    username = u.Username,
                    category = kv.Key.ToString(),
                    minutes = kv.Value,
                })).ToList();
            return Results.Ok(new
            {
                categories = AppCategories.All.Select(c => new
                {
                    category = c.ToString(),
                    weekdayMinutes = limits[c].WeekdayMinutes,
                    weekendMinutes = limits[c].WeekendMinutes,
                    editable = c != AppCategory.System,
                }),
                usageToday,
                maxMinutes = DailyLimit.MaxMinutes,
            });
        });

        app.MapPut("/api/admin/categories", async (HttpContext ctx) =>
        {
            JsonElement root;
            try { root = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement.Clone(); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON." }); }

            if (!root.TryGetProperty("category", out var catProp) || catProp.ValueKind != JsonValueKind.String ||
                !AppCategories.TryParse(catProp.GetString(), out var category))
                return Results.BadRequest(new { error = "Unknown category." });
            if (!TryReadMinutes(root, "weekdayMinutes", out var weekday) || !TryReadMinutes(root, "weekendMinutes", out var weekend))
                return Results.BadRequest(new { error = $"A limit must be a whole number from 0 to {DailyLimit.MaxMinutes}, or empty for no limit." });

            try
            {
                CategoryRepository.SetLimit(category, new DailyLimit(weekday, weekend));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            _logger.Information("Web Admin set the {Category} limit to {Weekday}/{Weekend}", category, weekday?.ToString() ?? "none", weekend?.ToString() ?? "none");
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/admin/app-rules", (HttpContext ctx) =>
        {
            return Results.Ok(new
            {
                rules = AppRuleRepository.GetAllRows().Select(r => new
                {
                    id = r.Rule.Id,
                    matchType = r.Rule.MatchType == RuleMatchType.File ? "file" : "folder",
                    pattern = r.Rule.Pattern,
                    category = r.Rule.Category.ToString(),
                    weekdayMinutes = r.Rule.AppLimit?.WeekdayMinutes,
                    weekendMinutes = r.Rule.AppLimit?.WeekendMinutes,
                    isDefault = r.IsDefault,
                    blocked = r.Rule.IsBlocked,
                    defaultBrowser = CategoryDefaults.IsBlockedBrowser(r.Rule.MatchType, r.Rule.Pattern),
                }),
            });
        });

        // The blocked programs of one day (default: today), per Restricted User and program. The file name and the
        // path are untrusted text: a client shows them as text.
        // The Admin Session gate in front of each /api/admin/ route covers it (AdminRoutePolicy, deny by default).
        app.MapGet("/api/admin/blocked-starts", (HttpContext ctx) =>
        {
            var dateText = ctx.Request.Query["date"].ToString();
            DateOnly date;
            if (string.IsNullOrWhiteSpace(dateText))
                date = ControlTime.Current.Today;
            else if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out date))
                return Results.BadRequest(new { error = "date must be yyyy-MM-dd." });

            var names = new Dictionary<int, string>();
            string NameOf(int userId)
            {
                if (!names.TryGetValue(userId, out var name))
                    names[userId] = name = UserRepository.GetById(userId)?.Username ?? userId.ToString();
                return name;
            }

            return Results.Ok(new
            {
                date = date.ToString("yyyy-MM-dd"),
                starts = BlockedStartRepository.GetForDate(date).Select(r => new
                {
                    username = NameOf(r.UserId),
                    fileName = r.FileName,
                    exePath = r.ExePath,
                    closedCount = r.ClosedCount,
                    suspendedCount = r.SuspendedCount,
                    firstAt = r.FirstAtUtc,
                    lastAt = r.LastAtUtc,
                }),
            });
        });

        app.MapPost("/api/admin/app-rules", async (HttpContext ctx) =>
        {
            var (rule, error) = await ReadRuleAsync(ctx);
            if (rule is null) return Results.BadRequest(new { error });
            try
            {
                var id = AppRuleRepository.Insert(rule.Value.MatchType, rule.Value.Pattern, rule.Value.Category, rule.Value.AppLimit,
                    rule.Value.Blocked ?? false);
                _logger.Information("Web Admin added App Rule {Id}: {Type} {Pattern} -> {Category}, blocked {Blocked}",
                    id, rule.Value.MatchType, rule.Value.Pattern, rule.Value.Category, rule.Value.Blocked ?? false);
                return Results.Ok(new { success = true, id });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPut("/api/admin/app-rules/{id:long}", async (HttpContext ctx, long id) =>
        {
            var (rule, error) = await ReadRuleAsync(ctx);
            if (rule is null) return Results.BadRequest(new { error });
            try
            {
                // A missing "blocked" keeps the stored block, so an old client does not remove a block.
                if (!AppRuleRepository.Update(id, rule.Value.MatchType, rule.Value.Pattern, rule.Value.Category, rule.Value.AppLimit, rule.Value.Blocked))
                    return Results.NotFound(new { error = "Rule not found." });
                _logger.Information("Web Admin changed App Rule {Id}: {Type} {Pattern} -> {Category}, blocked {Blocked}",
                    id, rule.Value.MatchType, rule.Value.Pattern, rule.Value.Category, rule.Value.Blocked?.ToString() ?? "unchanged");
                return Results.Ok(new { success = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapDelete("/api/admin/app-rules/{id:long}", (HttpContext ctx, long id) =>
        {
            if (!AppRuleRepository.Delete(id))
                return Results.NotFound(new { error = "Rule not found." });
            _logger.Information("Web Admin deleted App Rule {Id}", id);
            return Results.Ok(new { success = true });
        });

        // ═══ Breaks (add-usage-breaks, design D14). The admin gate covers each route below /api/admin/. ═══
        app.MapGet("/api/admin/breaks", (HttpContext ctx) =>
        {
            var now = DateTime.UtcNow;
            return Results.Ok(_breakEnforcer.GetStatus().Select(s => BreakItem(s, now)).ToList());
        });

        app.MapPut("/api/admin/breaks/{userId:int}", async (HttpContext ctx, int userId) =>
        {
            var user = UserRepository.GetById(userId);
            if (user is null || !user.IsRestricted)
                return Results.NotFound(new { error = "No Restricted User with this id." });

            JsonElement root;
            try { root = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement.Clone(); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON." }); }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("enabled", out var enabledProp)
                || enabledProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !TryReadWholeNumber(root, "useMinutes", out var useMinutes)
                || !TryReadWholeNumber(root, "breakMinutes", out var breakMinutes))
                return Results.BadRequest(new { error = "The fields enabled (true or false), useMinutes and breakMinutes (whole numbers) are required." });

            var rule = new BreakRule(enabledProp.GetBoolean(), useMinutes, breakMinutes);
            if (!rule.IsValid)
                return Results.BadRequest(new
                {
                    error = $"useMinutes must be from {BreakRule.MinUseMinutes} to {BreakRule.MaxUseMinutes}, " +
                            $"breakMinutes from {BreakRule.MinBreakMinutes} to {BreakRule.MaxBreakMinutes}."
                });

            _breakEnforcer.ChangeRule(user, rule);
            _logger.Information("Web Admin set the Break Rule of {Username} to {Rule}", user.Username, rule);
            var status = _breakEnforcer.GetStatus().First(s => s.UserId == user.Id);
            return Results.Ok(BreakItem(status, DateTime.UtcNow));
        });

        app.MapPost("/api/admin/breaks/{userId:int}/end", (HttpContext ctx, int userId) =>
        {
            var user = UserRepository.GetById(userId);
            if (user is null || !user.IsRestricted)
                return Results.NotFound(new { error = "No Restricted User with this id." });

            // The session stays locked: the child unlocks it (hard rule, no program closes).
            return _breakEnforcer.EndByParent(user) == ParentEndOutcome.Ended
                ? Results.Ok(new { ended = true })
                : Results.Conflict(new { error = "No Break of this user runs." });
        });

    }

    /// <summary>One item of <c>GET /api/admin/breaks</c>. <c>breakEndsAt</c> is a UTC time, or null if no Break runs.</summary>
    private static object BreakItem(BreakStatus s, DateTime nowUtc) => new
    {
        userId = s.UserId,
        username = s.Username,
        enabled = s.Rule.Enabled,
        useMinutes = s.Rule.UseMinutes,
        breakMinutes = s.Rule.BreakMinutes,
        continuousUseMinutes = s.ContinuousUseMinutes,
        breakEndsAt = s.BreakEndsUtc is { } end && end > nowUtc
            ? DateTime.SpecifyKind(end, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : null,
    };

    /// <summary>Reads a required whole number. Returns false if the field is missing, not a number or not whole.</summary>
    private static bool TryReadWholeNumber(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value);
    }

    /// <summary>
    /// Reads an optional minute value: missing, null or "" = no value; else a whole number from 0 to 1440.
    /// Returns false for any other value.
    /// </summary>
    private static bool TryReadMinutes(JsonElement root, string name, out int? minutes)
    {
        minutes = null;
        if (!root.TryGetProperty(name, out var prop))
            return true;
        switch (prop.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String when string.IsNullOrWhiteSpace(prop.GetString()):
                return true;
            case JsonValueKind.String when int.TryParse(prop.GetString(), out var fromText):
                minutes = fromText;
                break;
            case JsonValueKind.Number when prop.TryGetInt32(out var number):
                minutes = number;
                break;
            default:
                return false;
        }
        return DailyLimit.IsValidMinutes(minutes);
    }

    /// <summary>
    /// Reads an App Rule from the request body. <c>blocked</c> is optional: missing or null gives a null value.
    /// The block validation of a rule change runs in the repository against the stored block.
    /// </summary>
    private static async Task<((RuleMatchType MatchType, string Pattern, AppCategory Category, DailyLimit? AppLimit, bool? Blocked)? Rule, string? Error)> ReadRuleAsync(HttpContext ctx)
    {
        JsonElement root;
        try { root = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement.Clone(); }
        catch (JsonException) { return (null, "Invalid JSON."); }

        var typeText = root.TryGetProperty("matchType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        RuleMatchType? matchType = typeText?.ToLowerInvariant() switch { "file" => RuleMatchType.File, "folder" => RuleMatchType.Folder, _ => null };
        if (matchType is null)
            return (null, "matchType must be \"file\" or \"folder\".");

        var pattern = root.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        if (!root.TryGetProperty("category", out var c) || c.ValueKind != JsonValueKind.String || !AppCategories.TryParse(c.GetString(), out var category))
            return (null, "Unknown category.");
        if (!TryReadMinutes(root, "weekdayMinutes", out var weekday) || !TryReadMinutes(root, "weekendMinutes", out var weekend))
            return (null, $"An App Limit must be a whole number from 0 to {DailyLimit.MaxMinutes}, or empty.");

        bool? blocked = null;
        if (root.TryGetProperty("blocked", out var b))
        {
            switch (b.ValueKind)
            {
                case JsonValueKind.True: blocked = true; break;
                case JsonValueKind.False: blocked = false; break;
                case JsonValueKind.Null: break;
                default: return (null, "blocked must be true or false.");
            }
        }

        DailyLimit? appLimit = weekday is null && weekend is null ? null : new DailyLimit(weekday, weekend);
        var error = AppRule.Validate(matchType.Value, pattern, category, appLimit, blocked ?? false, CategoryRepository.LocalProtectedPrograms());
        return error is null ? ((matchType.Value, pattern, category, appLimit, blocked), null) : (null, error);
    }

    /// <summary>
    /// The one gate for all routes below <c>/api/admin/</c> (require-admin-sign-in D4). Deny by default: each admin
    /// route needs an Admin Session, and a state-changing request from another origin is refused (D8).
    /// </summary>
    /// <summary>
    /// Answers 503 on each route until the start gate opens (third review, SF3). The web server binds its port before
    /// the data folder step, but the database is not safe to use before the gate. Without a gate (a test) it passes.
    /// </summary>
    private async Task StartGateAsync(HttpContext ctx, Func<Task> next)
    {
        if (_startup is null || _startup.IsReady)
        {
            await next();
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "10";
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Parental Control startet noch. Bitte versuche es gleich noch einmal.");
    }

    private async Task AdminGateAsync(HttpContext ctx, Func<Task> next)
    {
        var template = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var routeClass = AdminRoutePolicy.Classify(ctx.Request.Path.Value, template);
        if (routeClass == AdminRouteClass.Open)
        {
            await next();
            return;
        }

        var sessionValid = routeClass == AdminRouteClass.Admin && _adminAccess.IsSessionValid(ctx);
        var result = AdminRoutePolicy.Decide(routeClass, ctx.Request.Method, ctx.Request.Headers.Origin.ToString(), ctx.Request.Host.Value, sessionValid);
        switch (result)
        {
            case AdminGateResult.Forbidden:
                _logger.Warning("Admin gate refused {Method} {Path} from origin {Origin}", ctx.Request.Method, ctx.Request.Path.Value, ctx.Request.Headers.Origin.ToString());
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            case AdminGateResult.Unauthorized:
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            default:
                await next();
                return;
        }
    }

    /// <summary>
    /// The answer to a refused login. A form post gets the HTML page as before. A JSON request (the web admin, ADR 0009)
    /// gets <c>{ error, message, blockedUntil }</c>: the German text of <see cref="AdminAccessTexts"/>, so the page
    /// shows the text of the service and, for a block, the time when the block ends.
    /// </summary>
    private static IResult LoginRefusal(HttpContext ctx, int statusCode, string error, string message,
        Func<string>? formPage = null, string? blockedUntil = null)
    {
        if (!ctx.Request.HasFormContentType)
            return Results.Json(new { error, message, blockedUntil }, statusCode: statusCode);
        var html = formPage?.Invoke() ?? AdminPage.RenderLogin(message);
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    /// <summary>Reads the code of a login from a form or a JSON body. A body without a code gives null.</summary>
    private static async Task<string?> ReadLoginCodeAsync(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync();
                return form["code"].ToString();
            }

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or BadHttpRequestException)
        {
            return null;
        }
    }

    private async Task<ScreenCaptureRecord?> TriggerCaptureAndWaitAsync(string username, string trigger, int timeoutSeconds = 15)
    {
        var user = UserRepository.GetByUsername(username);
        if (user == null) return null;

        var session = _sessionTracker.ActiveSessions.Values.FirstOrDefault(s => s.UserSid == user.Sid);
        if (session == null || session.IsLocked) return null;

        var tcs = new TaskCompletionSource<ScreenCaptureRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ScreenCaptureRecord record)
        {
            if (record.UserId == user.Id)
            {
                tcs.TrySetResult(record);
            }
        }

        OnCaptureReceived += Handler;
        try
        {
            var state = _captureStates.GetOrAdd(user.Username, _ => new CaptureCommandState());
            state.CaptureRequested = true;
            state.Trigger = trigger;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            OnCaptureReceived -= Handler;
        }
    }

public sealed class CaptureCommandState
{
    public bool CaptureRequested { get; set; }
    public bool WatchMode { get; set; }
    public DateTime WatchExpiresAt { get; set; }
    public string Trigger { get; set; } = "MANUAL";
}
}
