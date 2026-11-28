using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using ParentalControl.Domain.Captures;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Agent;

/// <summary>
/// The client end of the Agent Channel (session-probe-and-agent-channel, design D7): the pipe handle that the agent
/// inherited from the service. The agent sends requests with an id; one reader loop matches each answer to its request.
/// If the channel breaks, <see cref="Broken"/> fires once and the agent exits. No request has a user or a session: the
/// service takes both from its launch record.
/// <para>
/// All waits use <c>ConfigureAwait(false)</c>, so a caller on the UI thread never deadlocks with the reader loop.
/// </para>
/// </summary>
internal sealed class AgentChannelClient : IDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AgentAnswer>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private long _nextId;
    private int _broken;

    public AgentChannelClient(NamedPipeClientStream stream) => _stream = stream;

    /// <summary>Fires once when the channel breaks or sends a bad frame. It fires on a thread-pool thread.</summary>
    public event Action<string>? Broken;

    public bool IsBroken => Volatile.Read(ref _broken) != 0;

    public void Start() => _ = Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        var buffer = new byte[16 * 1024];
        string reason;
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, _closed.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    reason = "the service closed the channel";
                    break;
                }
                if (!decoder.Feed(buffer.AsSpan(0, read), answers))
                {
                    reason = "bad answer: " + decoder.Error;
                    break;
                }
                foreach (var answer in answers)
                {
                    if (_pending.TryRemove(answer.Id, out var waiter))
                        waiter.TrySetResult(answer);
                }
                answers.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            reason = "closed";
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        OnBroken(reason);
    }

    private void OnBroken(string reason)
    {
        if (Interlocked.Exchange(ref _broken, 1) != 0) return;
        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _)) waiter.TrySetCanceled();
        }
        try { Broken?.Invoke(reason); }
        catch (Exception ex) { AgentLog.Logger.Warning(ex, "Error in the handler of a broken channel"); }
    }

    /// <summary>
    /// Sends a request and waits for its answer. Returns null on a timeout, a broken channel or a dropped request
    /// (the rate limit of the service drops a request without an answer).
    /// </summary>
    /// <param name="build">Builds the JSON object of the request from its id.</param>
    public async Task<AgentAnswer?> RequestAsync(Func<long, object> build, TimeSpan timeout, byte[]? jpeg = null)
    {
        if (IsBroken) return null;
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<AgentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            if (!await WriteAsync(build(id), jpeg, timeout).ConfigureAwait(false))
                return null;
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);
            return finished == waiter.Task && waiter.Task.IsCompletedSuccessfully ? waiter.Task.Result : null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a request and does not wait for its answer. For the UI thread (the query of the end of the session): it
    /// blocks at most <paramref name="timeout"/> and never needs the UI thread to complete.
    /// </summary>
    public bool Send(Func<long, object> build, TimeSpan timeout)
    {
        if (IsBroken) return false;
        var id = Interlocked.Increment(ref _nextId);
        try
        {
            var write = Task.Run(() => WriteAsync(build(id), null, timeout));
            return write.Wait(timeout) && write.Result;
        }
        catch (Exception ex)
        {
            AgentLog.Logger.Warning(ex, "The channel write failed");
            return false;
        }
    }

    private async Task<bool> WriteAsync(object request, byte[]? jpeg, TimeSpan timeout)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request);
        if (json.Length > AgentChannelCodec.MaxJsonFrameBytes) return false;
        if (jpeg is not null && (jpeg.Length == 0 || jpeg.Length > CaptureQuota.MaxBytesPerCapture)) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        cts.CancelAfter(timeout);
        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            // The JSON frame and the JPEG frame of a capture go together: no other frame between them.
            await _stream.WriteAsync(AgentChannelCodec.Frame(json), cts.Token).ConfigureAwait(false);
            if (jpeg is not null)
            {
                var header = new byte[AgentChannelCodec.HeaderBytes];
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)jpeg.Length);
                await _stream.WriteAsync(header, cts.Token).ConfigureAwait(false);
                await _stream.WriteAsync(jpeg, cts.Token).ConfigureAwait(false);
            }
            await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // A part of a frame may have gone: the channel is not usable any more.
            OnBroken($"write failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch { /* the process ends */ }
    }
}
