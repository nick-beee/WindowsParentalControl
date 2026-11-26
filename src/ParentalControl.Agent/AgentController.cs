using System.Net.Http;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Json;
using System.Windows.Threading;
using ParentalControl.Domain.Messages;

namespace ParentalControl.Agent;

public sealed record ActivePromptDto(
    int id,
    string message,
    string urgency,
    int targetDisplay,
    int timeoutSeconds,
    List<string>? options);

public sealed record AgentStatusResponse(
    string? username,
    bool isRestricted,
    int remainingSeconds,
    string? curfew,
    bool isLocked,
    bool captureRequested = false,
    int watchIntervalSeconds = 0,
    bool isCurfewClamped = false,
    int? lastDeclinedId = null,
    string? lastDeclinedReason = null,
    DateTime? lastDeclinedTime = null,
    ActivePromptDto? activePrompt = null,
    string? language = null,
    List<AgentAppDto>? suspendedApps = null,
    List<AgentAppDto>? pendingApps = null);

public sealed class AgentController : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _activityTimer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly WidgetWindow _widgetWindow;
    private DispatcherTimer? _watchTimer;
    private int _currentWatchInterval;
    private bool _disposed;
    private int? _lastShownDeclinedId;
    private int? _lastHandledPromptId;
    private PromptDialogWindow? _activePromptDialog;
    // The Language Preset of the texts. "de" until the first status poll gives the preset of the service.
    private string _language = LanguagePreset.Default;
    private TextSet _texts = AgentTexts.For(LanguagePreset.Default);
    private readonly SessionEndWatcher _sessionEndWatcher;
    // The Suspended Apps of the last status poll, for the resume at the end of the session. Read on the UI thread.
    private IReadOnlyList<AgentAppDto> _suspendedApps = [];

    public AgentController(System.Windows.Application app)
    {
        _app = app;
        _widgetWindow = new WidgetWindow();
        _sessionEndWatcher = new SessionEndWatcher(() => _suspendedApps);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Visible = true,
            Text = "Parental Control Agent"
        };

        BuildContextMenu();

        // 1. Status Polling Timer (every 5 seconds)
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _statusTimer.Tick += async (s, e) => await PollStatusAsync();
        _statusTimer.Start();

        // 2. Foreground Activity Reporting Timer (every 20 seconds)
        _activityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _activityTimer.Tick += async (s, e) => await ActivityTracker.ReportActivityAsync(Environment.UserName);
        _activityTimer.Start();

        // Initial immediate poll
        _ = PollStatusAsync();
    }

    private void BuildContextMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var titleItem = new System.Windows.Forms.ToolStripMenuItem(_texts.Format("tray_user", ("username", Environment.UserName)))
        {
            Enabled = false
        };
        menu.Items.Add(titleItem);

        var toggleWidgetItem = new System.Windows.Forms.ToolStripMenuItem(_texts["tray_toggle_widget"], null, (s, e) =>
        {
            if (_widgetWindow.Visibility == System.Windows.Visibility.Visible)
            {
                _widgetWindow.Hide();
            }
            else
            {
                _widgetWindow.Show();
                _widgetWindow.Activate();
            }
        });
        menu.Items.Add(toggleWidgetItem);
        var settingsItem = new System.Windows.Forms.ToolStripMenuItem(_texts["tray_fullscreen_settings"], null, (s, e) =>
        {
            _widgetWindow.Dispatcher.Invoke(() => _widgetWindow.OpenSettings());
        });
        menu.Items.Add(settingsItem);

        var requestItem = new System.Windows.Forms.ToolStripMenuItem(_texts["tray_request"], null, (s, e) =>
        {
            try
            {
                var username = Uri.EscapeDataString(Environment.UserName);
                Process.Start(new ProcessStartInfo($"http://localhost:5050/request?user={username}") { UseShellExecute = true });
            }
            catch { }
        });
        menu.Items.Add(requestItem);

        var oldMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = menu;
        oldMenu?.Dispose();
    }

    private async Task PollStatusAsync()
    {
        try
        {
            var username = Uri.EscapeDataString(Environment.UserName);
            var status = await _httpClient.GetFromJsonAsync<AgentStatusResponse>(
                $"http://127.0.0.1:5050/api/agent/status?user={username}");

            if (status == null) return;

            ApplyLanguage(status.language);

            // Check for interactive prompt from parent/admin (all accounts)
            if (status.activePrompt != null && status.activePrompt.id != _lastHandledPromptId)
            {
                _lastHandledPromptId = status.activePrompt.id;
                var prompt = status.activePrompt;

                _app.Dispatcher.Invoke(() =>
                {
                    _activePromptDialog?.Close();
                    _activePromptDialog = new PromptDialogWindow(
                        prompt.id,
                        prompt.message,
                        prompt.urgency,
                        prompt.targetDisplay,
                        prompt.timeoutSeconds,
                        prompt.options,
                        _texts,
                        async (resp, reason, elapsed) =>
                        {
                            try
                            {
                                var payload = new
                                {
                                    promptId = prompt.id,
                                    response = resp,
                                    reason,
                                    turnaroundSeconds = elapsed
                                };
                                await _httpClient.PostAsJsonAsync("http://127.0.0.1:5050/api/agent/prompt/respond", payload);
                            }
                            catch { }
                        });
                    _activePromptDialog.Show();
                });
            }

            _suspendedApps = status.suspendedApps ?? [];
            var pendingApps = status.pendingApps ?? [];
            if (pendingApps.Count > 0)
            {
                // Minimize the windows of a Pending Suspension while the process still runs. The service suspends it anyway.
                await Task.Run(() => WindowMinimizer.Minimize(pendingApps));
            }

            if (!status.isRestricted)
            {
                if (_widgetWindow.Visibility == System.Windows.Visibility.Visible)
                {
                    _widgetWindow.Hide();
                }
                _notifyIcon.Text = _texts["tray_unrestricted"];
                return;
            }

            // User is restricted: show widget if not shown
            if (_widgetWindow.Visibility != System.Windows.Visibility.Visible)
            {
                _widgetWindow.Show();
            }

            _widgetWindow.UpdateData(
                status.remainingSeconds,
                status.curfew ?? _texts["widget_curfew_empty"],
                status.isLocked,
                status.username ?? Environment.UserName,
                status.isCurfewClamped);

            if (status.lastDeclinedId != null && status.lastDeclinedId != _lastShownDeclinedId)
            {
                _lastShownDeclinedId = status.lastDeclinedId;
                _widgetWindow.ShowDeclineToast(
                    status.lastDeclinedReason ?? _texts["widget_declined_default"],
                    status.lastDeclinedTime ?? DateTime.Now);
            }

            // Update NotifyIcon tooltip
            string tooltip;
            if (status.isLocked)
            {
                tooltip = _texts["tray_locked"];
            }
            else
            {
                tooltip = AgentTexts.RemainingTooltip(_texts, status.remainingSeconds);
            }

            if (tooltip.Length > AgentTexts.TrayTooltipMaxLength) tooltip = tooltip.Substring(0, AgentTexts.TrayTooltipMaxLength);
            _notifyIcon.Text = tooltip;
            // Check for capture command from service
            if (status.captureRequested)
            {
                _ = CaptureAndUploadAsync("MANUAL");
            }

            // Manage Watch Mode timer
            if (status.watchIntervalSeconds > 0)
            {
                if (_watchTimer == null || _currentWatchInterval != status.watchIntervalSeconds)
                {
                    _watchTimer?.Stop();
                    _currentWatchInterval = status.watchIntervalSeconds;
                    _watchTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(_currentWatchInterval)
                    };
                    _watchTimer.Tick += async (s, e) => await CaptureAndUploadAsync("WATCH");
                    _watchTimer.Start();
                }
            }
            else if (_watchTimer != null)
            {
                _watchTimer.Stop();
                _watchTimer = null;
                _currentWatchInterval = 0;
            }
        }
        catch
        {
            // If service temporarily down or restarting, do not crash or block
        }
    }

    /// <summary>Uses the texts of the Language Preset of the service. A missing preset (an earlier service) gives "de".</summary>
    private void ApplyLanguage(string? language)
    {
        var preset = LanguagePreset.Normalize(language);
        if (preset == _language) return;
        _language = preset;
        _texts = AgentTexts.For(preset);
        _widgetWindow.ApplyTexts(_texts);
        BuildContextMenu();
    }

    private async Task CaptureAndUploadAsync(string trigger)
    {
        try
        {
            var bytes = ScreenCaptureHelper.CaptureScreenJpeg(1920, 70L, out var width, out var height);
            if (bytes == null || bytes.Length == 0) return;

            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Headers.Add("X-User", Environment.UserName);
            content.Headers.Add("X-Trigger", trigger);
            content.Headers.Add("X-Width", width.ToString());
            content.Headers.Add("X-Height", height.ToString());

            await _httpClient.PostAsync("http://127.0.0.1:5050/api/agent/capture", content);
        }
        catch
        {
            // Silent capture failure; do not alert or disturb restricted user
        }
    }

    /// <summary>Called after the WPF message loop ended because of a session end. See <see cref="SessionEndWatcher.WaitForQuery"/>.</summary>
    public void WaitForSessionEndQuery(TimeSpan timeout, long sessionEndingMs) => _sessionEndWatcher.WaitForQuery(timeout, sessionEndingMs);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _statusTimer?.Stop();
        _activityTimer?.Stop();
        _watchTimer?.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _sessionEndWatcher.Dispose();
        _httpClient.Dispose();
        try
        {
            _widgetWindow?.Close();
        }
        catch { }
    }
}
