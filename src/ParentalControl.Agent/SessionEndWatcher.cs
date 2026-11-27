using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Agent;

/// <summary>
/// A hidden top-level window that gets WM_QUERYENDSESSION. At a sign-out, a restart or a shutdown, it resumes the
/// Suspended Apps of the last status poll and tells the service that the session ends. Then each program answers the
/// query of Windows as if no limit applied. The window always answers TRUE: the agent never blocks or cancels the end
/// of the session. A query with ENDSESSION_CLOSEAPP (an installer closes only the agent) changes nothing.
/// </summary>
internal sealed class SessionEndWatcher : NativeWindow, IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WsPopup = unchecked((int)0x80000000);

    // The handler runs on the UI thread and must answer well within the hung-app timeout of Windows (5 s).
    private static readonly HttpClient Http = AgentHttp.Create(TimeSpan.FromSeconds(2));

    private readonly Func<IReadOnlyList<AgentAppDto>> _suspendedApps;
    // The time of the last WM_QUERYENDSESSION (Environment.TickCount64), for WaitForQuery.
    private long? _lastQueryMs;

    public SessionEndWatcher(Func<IReadOnlyList<AgentAppDto>> suspendedApps)
    {
        _suspendedApps = suspendedApps;
        CreateHandle(new CreateParams { Caption = "ParentalControlAgentSessionEnd", Style = WsPopup });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmQueryEndSession)
        {
            _lastQueryMs = Environment.TickCount64;
            var kind = SessionEndQuery.Classify((long)m.LParam);
            AgentLog.Logger.Information("WM_QUERYENDSESSION lParam=0x{LParam:X}: {Kind}", (long)m.LParam, kind);
            if (kind != SessionEndKind.None)
                OnSessionEnding(kind);
            m.Result = (IntPtr)1;
            return;
        }
        if (m.Msg == WmEndSession)
        {
            // Diagnosis only: after this message Windows can end the process at any time.
            AgentLog.Logger.Information("WM_ENDSESSION wParam=0x{WParam:X} lParam=0x{LParam:X}", (long)m.WParam, (long)m.LParam);
        }
        base.WndProc(ref m);
    }

    private void OnSessionEnding(SessionEndKind kind)
    {
        // 1. Local resume: milliseconds, and no service needed. The child owns these processes.
        foreach (var app in _suspendedApps())
        {
            try
            {
                var result = ProcessControl.Resume(app.pid, app.startTimeUtc, out var detail);
                AgentLog.Logger.Information("Resumed pid {Pid} for the session end: {Result} {Detail}", app.pid, result, detail);
            }
            catch (Exception ex)
            {
                AgentLog.Logger.Warning(ex, "Could not resume pid {Pid} for the session end", app.pid);
            }
        }

        // 2. The service starts a Session End Hold and resumes the processes that the agent does not know or cannot open.
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                sessionId = Process.GetCurrentProcess().SessionId,
                reason = kind == SessionEndKind.Logoff ? "logoff" : "shutdown",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5050/api/agent/session-ending")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = Http.Send(request);
            AgentLog.Logger.Information("Session end request: HTTP {Status}", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            AgentLog.Logger.Warning(ex, "Session end request failed");
        }
    }

    /// <summary>
    /// WPF calls <c>Application.Shutdown</c> when its own window gets WM_QUERYENDSESSION, and the message loop of WPF
    /// can end before Windows sends the query to this window. Then this method pumps the messages of the thread
    /// until this window got the query of this end of the session, or until the timeout. Windows sends the query
    /// during the pump. An older query (a CLOSEAPP query or a fake query) does not count.
    /// </summary>
    /// <param name="sessionEndingMs">The time of the SessionEnding event of WPF (Environment.TickCount64).</param>
    public void WaitForQuery(TimeSpan timeout, long sessionEndingMs)
    {
        // Log before the pump: Windows can end the process during the pump, right after the query and WM_ENDSESSION.
        AgentLog.Logger.Information("WPF message loop ended after a session end: wait for the query, handled already {Handled}",
            IsQueryOfThisEnd(sessionEndingMs));
        var watch = Stopwatch.StartNew();
        while (!SessionEndQuery.IsQueryOfThisEnd(_lastQueryMs, sessionEndingMs) && watch.Elapsed < timeout)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        AgentLog.Logger.Information("End of the agent after a session end: query handled {Handled} after {Ms} ms",
            SessionEndQuery.IsQueryOfThisEnd(_lastQueryMs, sessionEndingMs), watch.ElapsedMilliseconds);
    }

    /// <summary>Tells if this window already got the query of the end of the session that WPF reported.</summary>
    public bool IsQueryOfThisEnd(long sessionEndingMs) => SessionEndQuery.IsQueryOfThisEnd(_lastQueryMs, sessionEndingMs);

    public void Dispose() => DestroyHandle();
}
