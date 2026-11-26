using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Http;
using ParentalControl.Core.Data;
using ParentalControl.Core.Services;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Service.Web;

public enum AdminLoginOutcome
{
    Success,
    NotSetUp,
    InvalidCode,
    Blocked,
}

/// <param name="Token">The token of the new Admin Session, for <see cref="AdminLoginOutcome.Success"/> only.</param>
/// <param name="Limit">The login limit that applies, for <see cref="AdminLoginOutcome.Blocked"/> only.</param>
public sealed record AdminLoginResult(AdminLoginOutcome Outcome, string? Token = null, LoginLimitStatus? Limit = null);

/// <summary>
/// The admin sign-in of the Remote Admin Interface: the setup state, the Admin Sessions, the TOTP login with the
/// replay guard and the login limit. The policy is in ParentalControl.Domain; this class connects it to SQLite,
/// the network interfaces and Telegram.
/// </summary>
public sealed class AdminAccessService
{
    public const string CookieName = "pc_admin_session";

    private static readonly TimeSpan LocalAddressCacheTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly AdminSessionStore _sessions = new(TimeProvider.System);
    private readonly TelegramBotService _telegram;
    private readonly Serilog.ILogger _logger;
    private readonly object _loginGate = new();
    private readonly object _addressGate = new();
    private IPAddress[] _localAddresses = Array.Empty<IPAddress>();
    private DateTime _localAddressesReadAt = DateTime.MinValue;

    public AdminAccessService(TelegramBotService telegram, Serilog.ILogger logger)
    {
        _telegram = telegram;
        _logger = logger;
    }

    /// <summary>
    /// True when the setup is complete (TOTP on with a valid secret) and the data folder is secured.
    /// Read from SQLite at each call, because the Admin app changes the settings.
    /// </summary>
    public bool IsSetUp => DatabaseManager.DataFolderSecured && AdminAccessRepository.IsCredentialSetUp();

    /// <summary>True if the request has a valid Admin Session of the current session epoch.</summary>
    public bool IsSessionValid(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
            return false;
        if (!IsSetUp)
            return false;
        var epoch = AdminAccessRepository.GetSessionEpoch();
        return epoch is not null && _sessions.IsValid(token, epoch.Value);
    }

    public void Logout(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var token))
            _sessions.End(token);
    }

    public AdminLoginResult TryLogin(string? code, IPAddress? remoteAddress)
    {
        if (!IsSetUp)
            return new AdminLoginResult(AdminLoginOutcome.NotSetUp);

        var source = SourceOf(remoteAddress);
        LoginLimitStatus reached;
        lock (_loginGate)
        {
            var now = DateTimeOffset.UtcNow;
            AdminAccessRepository.DeleteFailuresOlderThan24Hours(now);
            var failures = AdminAccessRepository.GetRecentFailures(now);

            // While a limit applies, the code is not checked and the refused login is not a new failure.
            var status = LoginThrottle.Check(failures, source, now);
            if (status.Blocked)
                return new AdminLoginResult(AdminLoginOutcome.Blocked, Limit: status);

            var secret = SettingsRepository.Get(SettingsRepository.KeyTotpSecret);
            if (Totp.TryVerify(secret, code, now, AdminAccessRepository.GetLastUsedStep(), out var step))
            {
                AdminAccessRepository.SetLastUsedStep(step);
                AdminAccessRepository.DeleteFailuresOfSource(source);
                var epoch = AdminAccessRepository.GetSessionEpoch();
                if (epoch is null)
                {
                    // A stored epoch that is not a number: a new epoch repairs it.
                    AdminAccessRepository.SignOutAllSessions();
                    epoch = AdminAccessRepository.GetSessionEpoch() ?? 0;
                }
                _logger.Information("Admin login from source {Source} succeeded", source);
                return new AdminLoginResult(AdminLoginOutcome.Success, Token: _sessions.Issue(epoch.Value));
            }

            AdminAccessRepository.AddFailure(source, now);
            reached = LoginThrottle.AfterFailure(failures, source, now);
        }

        _logger.Warning("Admin login from source {Source} failed: invalid code", source);
        if (reached.Blocked)
            AlertBlocked(source, reached);
        return new AdminLoginResult(AdminLoginOutcome.InvalidCode);
    }

    /// <summary>The source of a login. An address that is not in the cache is checked again against fresh addresses.</summary>
    public string SourceOf(IPAddress? remoteAddress)
    {
        var source = LoginSource.Of(remoteAddress, GetLocalAddresses(forceRefresh: false));
        if (source != LoginSource.Local && source != LoginSource.Unknown)
            source = LoginSource.Of(remoteAddress, GetLocalAddresses(forceRefresh: true));
        return source;
    }

    private IPAddress[] GetLocalAddresses(bool forceRefresh)
    {
        lock (_addressGate)
        {
            var age = DateTime.UtcNow - _localAddressesReadAt;
            if (age > LocalAddressCacheTime || (forceRefresh && age > MinRefreshInterval))
            {
                try
                {
                    _localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                        .Select(u => u.Address)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Could not read the addresses of this PC");
                }
                _localAddressesReadAt = DateTime.UtcNow;
            }
            return _localAddresses;
        }
    }

    private void AlertBlocked(string source, LoginLimitStatus status)
    {
        var allSources = status.Scope == LoginLimitScope.AllSources;
        var until = (status.BlockedUntilUtc ?? DateTimeOffset.UtcNow).ToLocalTime().DateTime;
        _logger.Warning("Admin login limit reached ({Scope}) by source {Source}. Logins are blocked until {Until}",
            status.Scope, source, until);

        // The alert must not delay or break the login response.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await _telegram.SendAdminLoginBlockedAlertAsync(source, until, allSources))
                    _logger.Information("No Telegram configuration: the admin login block is in the log only");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Could not send the Telegram alert about the admin login block");
            }
        });
    }
}
