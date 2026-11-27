using System.Net.Http;

namespace ParentalControl.Agent;

/// <summary>
/// The HTTP clients of the Session Agent. The agent talks only to the local service on <c>127.0.0.1:5050</c>, so it
/// uses no proxy. A standard user can set a proxy for the own account without a script (Internet Options, or the
/// variables <c>HTTP_PROXY</c> and <c>HTTPS_PROXY</c>). With the bypass rule <c>&lt;-loopback&gt;</c>, a proxy also
/// takes loopback requests. Then the agent got no status (no widget, no request item), sent no samples and no session
/// end hold. Found in task 7.6 of request-window on VM 102.
/// </summary>
internal static class AgentHttp
{
    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
}
