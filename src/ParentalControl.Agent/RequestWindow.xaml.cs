using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.Requests;

namespace ParentalControl.Agent;

/// <summary>
/// The Request Window (request-window, ADR 0014): the child request page of the local service in an embedded
/// WebView2. It shows only <c>http://127.0.0.1:5050/request</c>, opens no other window, file or program, and has no
/// browser tools. It fails closed: the first navigation starts only after each setting, each event handler and the
/// resource filter are in place (design D6a), and after the Request Token is set as a cookie
/// (request-identity-from-launch). Each failure shows the text <c>request_window_unavailable</c>.
/// The agent never starts a browser.
/// </summary>
public partial class RequestWindow : Window
{
    private readonly Func<Task<string?>> _getRequestToken;
    private readonly RequestWindowNavigations _navigations = new();
    private TextSet _texts;
    private CoreWebView2Environment? _environment;
    private bool _navigateCalled;
    private bool _fallbackShown;
    private bool _closed;
    private bool _webViewDisposed;

    /// <param name="getRequestToken">Gets a new Request Token from the service through the Agent Channel, or null
    /// (request-identity-from-launch, design D7).</param>
    public RequestWindow(TextSet texts, Func<Task<string?>> getRequestToken)
    {
        InitializeComponent();
        PlaceInWorkArea();
        _texts = texts;
        _getRequestToken = getRequestToken;
        ApplyTexts(texts);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Fits the window into the work area of the primary screen and puts it in the centre. On a small screen the
    /// preferred height of the XAML is higher than the work area, and the title bar goes above the screen.
    /// </summary>
    private void PlaceInWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var rect = RequestWindowPlacement.Fit(
            new WindowRect(area.Left, area.Top, area.Width, area.Height), Width, Height, MinWidth, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
    }

    /// <summary>The title and the fallback text of the Language Preset.</summary>
    public void ApplyTexts(TextSet texts)
    {
        _texts = texts;
        Title = texts["request_window_title"];
        if (_fallbackShown) StatusText.Text = texts["request_window_unavailable"];
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // One try block for the whole setup (design D6a): each exception shows the fallback, and no navigation starts.
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            AgentLog.Logger.Warning(ex, "Request Window: the setup of the web view failed");
            ShowFallback();
        }
    }

    private async Task StartAsync()
    {
        // Design D4: no override of the user reaches the WebView2 loader, and only the machine-wide runtime is used.
        var removed = WebView2Runtime.RemoveEnvironmentOverrides();
        if (removed.Count > 0)
            AgentLog.Logger.Warning("Request Window: removed the environment variables {Names}", removed);

        var runtimeFolder = WebView2Runtime.FindFolder();
        if (runtimeFolder is null)
        {
            AgentLog.Logger.Warning("Request Window: no machine-wide WebView2 Runtime");
            ShowFallback();
            return;
        }

        var dataFolder = PrepareDataFolder();
        var options = new CoreWebView2EnvironmentOptions
        {
            // Review 1 N8: the child can set a proxy for its account. The page needs none.
            AdditionalBrowserArguments = "--no-proxy-server",
        };
        var environment = await CoreWebView2Environment.CreateAsync(runtimeFolder, dataFolder, options);
        if (_closed) return;

        var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
        controllerOptions.IsInPrivateModeEnabled = true;
        await WebView.EnsureCoreWebView2Async(environment, controllerOptions);
        if (_closed || _fallbackShown) return;
        _environment = environment;

        var core = WebView.CoreWebView2;

        // Design D3.
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        WebView.AllowExternalDrop = false;

        // Design D2 and D6.
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebResourceRequested += OnWebResourceRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
#if REQUEST_WINDOW_FAULT_TEST
        // Task 7.10: a test build proves that a failed subscription shows the fallback and loads no page.
        FailForFaultTest();
#endif
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.PermissionRequested += OnPermissionRequested;
        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        core.ProcessFailed += OnProcessFailed;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);

        if (_closed || _fallbackShown) return;

        // request-identity-from-launch, design D2 and D7: the service takes the user of the page only from the Request
        // Token. It goes as an HttpOnly cookie, so the page load and each fetch of the page send it, and the page code
        // cannot read it. Without a token the window shows the fallback and does not navigate.
        var token = await _getRequestToken();
        if (_closed || _fallbackShown) return;
        if (token is null)
        {
            AgentLog.Logger.Warning("Request Window: the service gave no Request Token");
            ShowFallback();
            return;
        }
        var cookies = core.CookieManager;
        var cookie = cookies.CreateCookie(RequestTokenStore.CookieName, token, RequestWindowUrlPolicy.Host, "/");
        cookie.IsHttpOnly = true;
        cookie.IsSecure = false; // the page is http://127.0.0.1
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        cookies.AddOrUpdateCookie(cookie);

        _navigateCalled = true;
        core.Navigate(RequestWindowUrlPolicy.PageUrl);
    }

#if REQUEST_WINDOW_FAULT_TEST
    private static void FailForFaultTest() =>
        throw new NotSupportedException("Test build: the subscription of LaunchingExternalUriScheme fails.");
#endif

    /// <summary>
    /// A new, empty data folder below <c>%LOCALAPPDATA%\ParentalControl\WebView2</c> (design D5). Each old folder that
    /// can be deleted goes first; a folder that a browser process still holds stays until a later open.
    /// </summary>
    private static string PrepareDataFolder()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            RequestWindowDataFolders.RelativeRoot);
        Directory.CreateDirectory(root);

        var name = RequestWindowDataFolders.NewName();
        var existing = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToList();
        foreach (var old in RequestWindowDataFolders.ToDelete(existing, name))
        {
            try
            {
                Directory.Delete(Path.Combine(root, old), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use by a browser process of an earlier open.
            }
        }

        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var allowed = RequestWindowUrlPolicy.IsAllowedDocument(e.Uri);
        if (!allowed) e.Cancel = true;
        if (_navigateCalled) _navigations.Started(e.NavigationId, cancelledByAgent: !allowed);
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!RequestWindowUrlPolicy.IsAllowedDocument(e.Uri)) e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_navigateCalled) return;
        if (!e.IsSuccess)
            AgentLog.Logger.Information("Request Window: navigation {Id} ended with {Status}", e.NavigationId, e.WebErrorStatus);
        if (_navigations.Completed(e.NavigationId, e.IsSuccess) == NavigationOutcome.ShowFallback)
            ShowFallback();
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        var allowed = e.ResourceContext == CoreWebView2WebResourceContext.Document
            ? RequestWindowUrlPolicy.IsAllowedDocument(uri)
            : RequestWindowUrlPolicy.IsAllowedResource(uri);
        if (allowed)
            return;
        if (_environment is { } environment)
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        else
            ShowFallback(); // Fail closed: without an environment the request cannot be refused.
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) => e.Cancel = true;

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e) => e.Cancel = true;

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        AgentLog.Logger.Warning("Request Window: process failed: {Kind}", e.ProcessFailedKind);
        // The runtime starts a new GPU or utility process itself; the page stays usable.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.GpuProcessExited or CoreWebView2ProcessFailedKind.UtilityProcessExited)
            return;
        ShowFallback();
    }

    /// <summary>Shows <c>request_window_unavailable</c> and closes the web view. The window stays open.</summary>
    private void ShowFallback()
    {
        if (_fallbackShown) return;
        _fallbackShown = true;
        StatusText.Text = _texts["request_window_unavailable"];
        StatusText.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Collapsed;
        // Not inside a handler of the web view itself.
        Dispatcher.BeginInvoke(DisposeWebView);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        DisposeWebView();
    }

    private void DisposeWebView()
    {
        if (_webViewDisposed) return;
        _webViewDisposed = true;
        try
        {
            WebView.Dispose();
        }
        catch (Exception ex)
        {
            AgentLog.Logger.Warning(ex, "Request Window: the web view did not close cleanly");
        }
    }
}
