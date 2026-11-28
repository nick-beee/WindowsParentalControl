using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Service;

/// <summary>
/// Starts one Session Agent per session and talks to it over a private Agent Channel (session-probe-and-agent-channel,
/// design D7, D8, D9). The service owns the pipe: the agent inherits only the client end. The identity of each message
/// is the launch record of its channel. Each input on a channel is hostile: a bad frame, a stall, a slow reader or a
/// flood closes only that channel, and no exception of a channel ends the service.
/// </summary>
public sealed class AgentChannelHost : IHostedService, IAgentStarter
{
    /// <summary>A frame (and the JPEG frame of a capture) must arrive within this time after its first byte.</summary>
    internal static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A write of the service must complete within this time.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentRequests _requests;
    private readonly SessionTracker _tracker;
    private readonly Serilog.ILogger _logger;
    private readonly AgentLaunchRegistry _registry = new();
    private readonly ConcurrentDictionary<int, AgentChannel> _channels = new();
    private readonly WarningLimit _launchWarnings = new();
    // ShouldStart, the launch and Record are one step per session (AgentLaunchRegistry). One gate for all sessions is
    // enough: a start takes some 100 ms, and ProcessLaunchLock serializes the starts of the service anyway.
    private readonly object _startGate = new();
    private readonly CancellationTokenSource _stopping = new();

    public AgentChannelHost(AgentRequests requests, SessionTracker tracker, Serilog.ILogger logger)
    {
        _requests = requests;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>Connects the session tracker to this host. Called once after the host is built.</summary>
    public void Attach()
    {
        _tracker.AttachAgentStarter(this);
        _tracker.SessionEnded += OnSessionEnded;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The service stops: close each channel. Each agent exits when its channel breaks (design D9).</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (_startGate)
            {
                _stopping.Cancel();
                foreach (var (sessionId, channel) in _channels)
                {
                    channel.Dispose();
                    _channels.TryRemove(sessionId, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Agent Channel host: error at the service stop");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the Session Agent in the session if the session has no launch record or the process of its record ended
    /// (design D9). A process of the child with the agent name, the agent exe or the old mutex changes nothing.
    /// </summary>
    public void EnsureRunning(int sessionId)
    {
        try
        {
            lock (_startGate)
            {
                if (_stopping.IsCancellationRequested) return;
                if (!_registry.ShouldStart(sessionId, IsRunning)) return;
                Start(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Agent Channel host: error at the start of the agent in session {SessionId}", sessionId);
        }
    }

    /// <summary>True if the agent that the service started in the session still runs (the registry, design D9).</summary>
    public bool IsRunning(int sessionId)
    {
        var record = _registry.Running(sessionId);
        return record is not null && IsRunning(record);
    }

    private bool IsRunning(AgentLaunchRecord record) =>
        _channels.TryGetValue(record.SessionId, out var channel) && channel.Record == record
        && AgentChannelPlatform.IsRunning(channel.Process.Handle);

    // Under _startGate.
    private void Start(int sessionId)
    {
        var agentPath = SessionTracker.GetAgentExecutablePath();
        if (agentPath is null)
        {
            Warn(sessionId, "the agent exe was not found");
            return;
        }

        var (server, client) = AgentChannelPlatform.CreateChannel();
        LaunchedProcess? launched;
        string? error;
        try
        {
            // The client end goes only to the agent (design D7 step 3 and 4): the service closes its copy at once, so
            // the pipe breaks when the agent ends.
            launched = SessionManager.LaunchProcessInSession(sessionId, agentPath, "--channel {handle}",
                client.DangerousGetHandle(), out error);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        finally
        {
            client.Dispose();
        }
        if (launched is null)
        {
            server.Dispose();
            Warn(sessionId, error ?? "unknown error");
            return;
        }

        var record = new AgentLaunchRecord(sessionId, launched.Identity, launched.UserSid);
        AgentChannel channel;
        try
        {
            var stream = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: true, server);
            channel = new AgentChannel(this, record, launched, stream);
        }
        catch
        {
            // The agent exits when its channel breaks.
            server.Dispose();
            launched.Dispose();
            throw;
        }
        if (_channels.TryRemove(sessionId, out var old)) old.Dispose();
        _channels[sessionId] = channel;
        _registry.Record(record);
        _logger.Information("Started the Session Agent in session {SessionId} (pid {Pid}, user {Sid}) with an Agent Channel",
            sessionId, launched.Pid, launched.UserSid);
        channel.Start(_stopping.Token);
    }

    private void Warn(int sessionId, string error)
    {
        // The start stays failed (for example if CreateEnvironmentBlock fails), but the same warning of a session logs
        // at most once in 10 minutes.
        if (_launchWarnings.Take($"{sessionId}:{error}", TimeSpan.FromMilliseconds(Environment.TickCount64), out var suppressed))
            _logger.Warning("Failed to launch the Session Agent in session {SessionId}: {Error} ({Suppressed} same warnings left out)",
                sessionId, error, suppressed);
    }

    private void OnSessionEnded(int sessionId)
    {
        try
        {
            lock (_startGate)
            {
                _registry.SessionEnded(sessionId);
                if (_channels.TryRemove(sessionId, out var channel)) channel.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Agent Channel host: error at the end of session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// The service closed a channel for a fault. The spec wants a new agent within 60 s, also if injected code keeps the
    /// old process alive, so the record counts as ended and the next tick starts a new agent.
    /// </summary>
    private void OnFault(AgentChannel channel)
    {
        lock (_startGate)
        {
            _registry.MarkEnded(channel.Record.SessionId, channel.Record.Process);
        }
    }

    /// <summary>One channel: one sequential loop that reads, handles and answers.</summary>
    private sealed class AgentChannel : IDisposable
    {
        private readonly AgentChannelHost _host;
        private readonly NamedPipeServerStream _stream;
        private readonly AgentChannelCodec _codec = new();
        private readonly ChannelRateLimit _rate = new();
        private readonly CancellationTokenSource _closed = new();
        private int _disposed;
        private bool _loggedHandlerError;
        private bool _captureTaken;

        public AgentChannel(AgentChannelHost host, AgentLaunchRecord record, LaunchedProcess process, NamedPipeServerStream stream)
        {
            _host = host;
            Record = record;
            Process = process;
            _stream = stream;
        }

        public AgentLaunchRecord Record { get; }
        public LaunchedProcess Process { get; }

        private Serilog.ILogger Log => _host._logger;

        public void Start(CancellationToken stopping)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _closed.Token);
            _ = Task.Run(() => RunAsync(linked.Token)).ContinueWith(t =>
            {
                linked.Dispose();
                if (t.Exception is { } ex)
                    Log.Error(ex, "Agent Channel of session {SessionId}: the loop failed", Record.SessionId);
            }, TaskScheduler.Default);
        }

        private async Task RunAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var messages = new List<AgentMessage>();
            long? frameDeadline = null;
            string? fault = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read;
                    if (frameDeadline is { } deadline)
                    {
                        var left = deadline - Environment.TickCount64;
                        if (left <= 0) { fault = "a frame did not arrive within 10 s"; break; }
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(left));
                        try
                        {
                            read = await _stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            fault = "a frame did not arrive within 10 s";
                            break;
                        }
                    }
                    else
                    {
                        // No timeout between frames: an idle agent is legal.
                        read = await _stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    }

                    if (read == 0)
                    {
                        if (!_codec.EndOfStream())
                            Log.Information("Agent Channel of session {SessionId}: the agent ended inside a frame ({Error})",
                                Record.SessionId, _codec.Error);
                        break;
                    }

                    if (!_codec.Feed(buffer.AsSpan(0, read), messages))
                    {
                        fault = "bad frame: " + _codec.Error;
                        break;
                    }
                    if (_codec.IsInsideFrame || _codec.ExpectsCaptureBytes)
                        frameDeadline ??= Environment.TickCount64 + (long)FrameTimeout.TotalMilliseconds;
                    else
                        frameDeadline = null;

                    foreach (var message in messages)
                    {
                        switch (_rate.Offer(TimeSpan.FromMilliseconds(Environment.TickCount64)))
                        {
                            case ChannelRateDecision.Drop:
                                continue;
                            case ChannelRateDecision.Close:
                                fault = "10 dropped messages (rate limit)";
                                break;
                        }
                        if (fault is not null) break;
                        _captureTaken = false;
                        var answer = Handle(message);
                        if (!await WriteAsync(answer, token).ConfigureAwait(false))
                        {
                            if (_captureTaken) _host._requests.RestoreCaptureRequest(Record);
                            fault ??= "a write did not complete within 5 s";
                            break;
                        }
                    }
                    messages.Clear();
                    if (fault is not null) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The service stops or the channel was closed.
            }
            catch (IOException ex)
            {
                // A broken pipe: the agent ended.
                Log.Information("Agent Channel of session {SessionId}: the pipe broke ({Message})", Record.SessionId, ex.Message);
            }
            catch (ObjectDisposedException)
            {
                // Closed by the host.
            }
            catch (Exception ex)
            {
                fault = "error: " + ex.GetType().Name;
                Log.Error(ex, "Agent Channel of session {SessionId}: unexpected error", Record.SessionId);
            }

            if (fault is not null)
                Log.Warning("Agent Channel of session {SessionId} (pid {Pid}) closed: {Fault}", Record.SessionId, Record.Process.Pid, fault);
            // A channel that ended for any reason except a close of the host is dead: the next tick starts a new agent,
            // also if code in the agent closed its end and keeps the process alive.
            if (fault is not null || !token.IsCancellationRequested)
            {
                try { _host.OnFault(this); }
                catch (Exception ex) { Log.Error(ex, "Agent Channel of session {SessionId}: error after the end of the channel", Record.SessionId); }
            }
            CloseStream();
        }

        /// <summary>Handles one message. Never throws: a failed handler gives <c>ok: false</c>.</summary>
        private byte[] Handle(AgentMessage message)
        {
            var type = message switch
            {
                AgentStatusRequest => "status",
                AgentPromptAnswer => "promptAnswer",
                AgentCapture => "capture",
                AgentSessionEnding => "sessionEnding",
                _ => "unknown",
            };
            try
            {
                return message switch
                {
                    AgentStatusRequest => StatusAnswer(message.Id, type),
                    AgentPromptAnswer answer => Answer(new { id = message.Id, type, ok = _host._requests.AnswerPrompt(Record, answer) }),
                    AgentCapture capture => Answer(new { id = message.Id, type, ok = true, result = _host._requests.StoreCapture(Record, capture) }),
                    // The answer does not say whether the deferral or the hold was granted (design D8).
                    AgentSessionEnding ending => SessionEndingAnswer(ending, type),
                    _ => Answer(new { id = message.Id, type, ok = false }),
                };
            }
            catch (Exception ex)
            {
                if (!_loggedHandlerError)
                {
                    _loggedHandlerError = true;
                    Log.Error(ex, "Agent Channel of session {SessionId}: the handler of {Type} failed (later errors of this channel are not logged)",
                        Record.SessionId, type);
                }
                return AgentChannelCodec.Json(new { id = message.Id, type, ok = false });
            }
        }

        // A capture request that the status took goes back if its answer cannot be built; the write is checked in the loop.
        private byte[] StatusAnswer(long id, string type)
        {
            var status = _host._requests.Status(Record, out var captureTaken);
            try
            {
                var frame = Answer(new { id, type, ok = true, status });
                _captureTaken = captureTaken;
                return frame;
            }
            catch
            {
                if (captureTaken) _host._requests.RestoreCaptureRequest(Record);
                throw;
            }
        }

        private byte[] SessionEndingAnswer(AgentSessionEnding ending, string type)
        {
            _host._requests.SessionEnding(Record, ending);
            return Answer(new { id = ending.Id, type, ok = true });
        }

        private static byte[] Answer<T>(T value)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(value);
            if (json.Length > AgentChannelCodec.MaxJsonFrameBytes)
                throw new InvalidOperationException($"the answer has {json.Length} bytes (limit {AgentChannelCodec.MaxJsonFrameBytes})");
            return AgentChannelCodec.Frame(json);
        }

        private async Task<bool> WriteAsync(byte[] frame, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(WriteTimeout);
            try
            {
                await _stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
        }

        private void CloseStream()
        {
            try { _stream.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "Agent Channel of session {SessionId}: error at the close", Record.SessionId); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _closed.Cancel(); } catch (ObjectDisposedException) { }
            CloseStream();
            Process.Dispose();
        }
    }
}

/// <summary>Starts and finds the Session Agent of a session (design D9). <see cref="AgentChannelHost"/> implements it.</summary>
public interface IAgentStarter
{
    void EnsureRunning(int sessionId);
    bool IsRunning(int sessionId);
}
