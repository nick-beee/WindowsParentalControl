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
    private const int WsPopup = unchecked((int)0x80000000);

    // The handler runs on the UI thread and must answer well within the hung-app timeout of Windows (5 s).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly Func<IReadOnlyList<AgentAppDto>> _suspendedApps;

    public SessionEndWatcher(Func<IReadOnlyList<AgentAppDto>> suspendedApps)
    {
        _suspendedApps = suspendedApps;
        CreateHandle(new CreateParams { Caption = "ParentalControlAgentSessionEnd", Style = WsPopup });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmQueryEndSession)
        {
            var kind = SessionEndQuery.Classify((long)m.LParam);
            AgentLog.Logger.Information("WM_QUERYENDSESSION lParam=0x{LParam:X}: {Kind}", (long)m.LParam, kind);
            if (kind != SessionEndKind.None)
                OnSessionEnding(kind);
            m.Result = (IntPtr)1;
            return;
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

    public void Dispose() => DestroyHandle();
}
