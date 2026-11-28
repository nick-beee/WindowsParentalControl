using System.Buffers.Binary;
using System.Text.Json;
using ParentalControl.Domain.Captures;

namespace ParentalControl.Domain.Sessions;

/// <summary>
/// A request of the Session Agent on its Agent Channel (session-probe-and-agent-channel, design D7). No message has a
/// user or a session: the service takes both from the launch record of the channel.
/// </summary>
public abstract record AgentMessage(long Id);

/// <summary>The status poll (<c>status</c>).</summary>
public sealed record AgentStatusRequest(long Id) : AgentMessage(Id);

/// <summary>The answer of the child to an Interactive Session Prompt (<c>promptAnswer</c>).</summary>
public sealed record AgentPromptAnswer(long Id, int PromptId, string Response, string? Reason, int TurnaroundSeconds)
    : AgentMessage(Id);

/// <summary>
/// A screen capture (<c>capture</c>): a JSON frame, then the JPEG bytes as a second frame. If the service refused the
/// capture before the JPEG frame (<see cref="CaptureAdmission"/>), <see cref="Refused"/> has the reason and
/// <see cref="Jpeg"/> is empty: the codec skipped the JPEG bytes without a buffer.
/// </summary>
public sealed record AgentCapture(long Id, string Trigger, int Width, int Height, byte[] Jpeg, string? Refused = null)
    : AgentMessage(Id);

/// <summary>
/// The decision of the service on a <c>capture</c> JSON frame, before the codec allocates the buffer of its JPEG frame
/// (review of task groups 5 and 6): the rate limit and the daily quota of the captures.
/// </summary>
public sealed record CaptureAdmission
{
    private CaptureAdmission(long maxBytes, string? refused, bool dropped)
    {
        MaxBytes = maxBytes;
        Refused = refused;
        Dropped = dropped;
    }

    /// <summary>The largest JPEG frame that the service takes now. A larger frame is skipped and refused as "tooLarge".</summary>
    public long MaxBytes { get; }

    /// <summary>The reason of a refusal (for example "quota"); the service answers it.</summary>
    public string? Refused { get; }

    /// <summary>Over the rate limit: the codec skips the JPEG frame and gives no message, so the service gives no answer.</summary>
    public bool Dropped { get; }

    public static CaptureAdmission Allow(long maxBytes) => new(Math.Min(maxBytes, CaptureQuota.MaxBytesPerCapture), null, false);
    public static CaptureAdmission Refuse(string reason) => new(0, reason, false);
    public static CaptureAdmission Drop { get; } = new(0, null, true);
}

/// <summary>A session-end request (<c>sessionEnding</c>). <see cref="Reason"/> is "shutdown" or "logoff".</summary>
public sealed record AgentSessionEnding(long Id, string Reason) : AgentMessage(Id);

/// <summary>
/// The framing of the Agent Channel (design D7, S7): each frame is a 4-byte little-endian length and the frame bytes.
/// A JSON frame has at most <see cref="MaxJsonFrameBytes"/> bytes. The JPEG frame after a <c>capture</c> frame has at
/// most <see cref="CaptureQuota.MaxBytesPerCapture"/> bytes. The decoder checks the length before it allocates the
/// frame buffer, so a length of 2 GB costs no memory.
/// <para>
/// The child controls the client end, so each input is hostile. An error is final: the decoder takes no more bytes,
/// and the service closes the channel. A field <c>user</c> or <c>sessionId</c> in a message is ignored. Not
/// thread-safe: one codec per channel.
/// </para>
/// </summary>
public sealed class AgentChannelCodec
{
    public const int HeaderBytes = 4;
    public const int MaxJsonFrameBytes = 64 * 1024;
    public const int MaxJsonDepth = 8;

    private readonly byte[] _header = new byte[HeaderBytes];
    private readonly Func<CaptureAdmission>? _admitCapture;
    private int _headerFill;
    private byte[]? _body;
    private int _bodyFill;
    // The bytes of a refused JPEG frame that the codec still skips (no buffer), or -1.
    private long _skipLeft = -1;
    private (long Id, string Trigger, int Width, int Height, CaptureAdmission Admission)? _pendingCapture;
    private (long Id, string Trigger, int Width, int Height, string? Refused)? _skipping;

    /// <param name="admitCapture">Called for each <c>capture</c> JSON frame, before the JPEG frame: the rate limit and
    /// the quota. Null takes each capture up to <see cref="CaptureQuota.MaxBytesPerCapture"/>.</param>
    public AgentChannelCodec(Func<CaptureAdmission>? admitCapture = null)
    {
        _admitCapture = admitCapture;
    }

    /// <summary>The reason of the first error, or null. After an error the channel must close.</summary>
    public string? Error { get; private set; }

    /// <summary>True between the first byte of a frame and its last byte. The service closes a channel whose frame
    /// does not arrive within 10 seconds.</summary>
    public bool IsInsideFrame => _headerFill > 0 || _body is not null || _skipLeft >= 0;

    /// <summary>The number of frames whose first byte arrived. The service starts the frame deadline again for each
    /// new frame.</summary>
    public long FramesStarted { get; private set; }

    /// <summary>True if the next frame must be the JPEG of a <c>capture</c>.</summary>
    public bool ExpectsCaptureBytes => _pendingCapture is not null;

    /// <summary>The bytes of the frame buffer that the decoder holds now (0 between frames).</summary>
    public int BufferedCapacity => _body?.Length ?? 0;

    /// <summary>
    /// Takes the next bytes of the channel and adds each complete message to <paramref name="messages"/>.
    /// Returns false at the first error (see <see cref="Error"/>).
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> data, List<AgentMessage> messages)
    {
        if (Error is not null) return false;
        while (!data.IsEmpty)
        {
            if (_skipLeft >= 0)
            {
                // A refused JPEG frame: skip its bytes without a buffer.
                var skip = (int)Math.Min(_skipLeft, data.Length);
                _skipLeft -= skip;
                data = data[skip..];
                if (_skipLeft > 0) break;
                _skipLeft = -1;
                var refused = _skipping!.Value;
                _skipping = null;
                if (refused.Refused is not null)
                    messages.Add(new AgentCapture(refused.Id, refused.Trigger, refused.Width, refused.Height, [], refused.Refused));
                continue;
            }
            if (_body is null)
            {
                if (_headerFill == 0) FramesStarted++;
                var take = Math.Min(HeaderBytes - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill < HeaderBytes) break;

                var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
                var limit = _pendingCapture is null ? MaxJsonFrameBytes : CaptureQuota.MaxBytesPerCapture;
                if (length == 0 || length > limit)
                    return Fail($"frame length {length} is outside 1..{limit}");
                if (_pendingCapture is { } capture
                    && (capture.Admission.Dropped || capture.Admission.Refused is not null || length > capture.Admission.MaxBytes))
                {
                    // The service refused the capture before its JPEG: no buffer for it.
                    _pendingCapture = null;
                    _headerFill = 0;
                    _skipLeft = length;
                    _skipping = (capture.Id, capture.Trigger, capture.Width, capture.Height,
                        capture.Admission.Dropped ? null : capture.Admission.Refused ?? "tooLarge");
                    continue;
                }
                _body = new byte[length];
                _bodyFill = 0;
            }

            var copy = Math.Min(_body.Length - _bodyFill, data.Length);
            data[..copy].CopyTo(_body.AsSpan(_bodyFill));
            _bodyFill += copy;
            data = data[copy..];
            if (_bodyFill < _body.Length) break;

            var frame = _body;
            _body = null;
            _headerFill = 0;
            if (!Complete(frame, messages)) return false;
        }
        return true;
    }

    /// <summary>The channel ended. A frame that is not complete, or a capture without its JPEG, is an error.</summary>
    public bool EndOfStream()
    {
        if (Error is not null) return false;
        if (IsInsideFrame) return Fail("truncated frame");
        if (_pendingCapture is not null) return Fail("capture without its JPEG frame");
        return true;
    }

    private bool Complete(byte[] frame, List<AgentMessage> messages)
    {
        if (_pendingCapture is { } capture)
        {
            _pendingCapture = null;
            messages.Add(new AgentCapture(capture.Id, capture.Trigger, capture.Width, capture.Height, frame));
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("frame is not a JSON object");
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return Fail("frame has no type");
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id) || id < 0)
                return Fail("frame has no id");

            switch (typeElement.GetString())
            {
                case "status":
                    messages.Add(new AgentStatusRequest(id));
                    return true;
                case "promptAnswer":
                    if (!TryInt(root, "promptId", out var promptId)) return Fail("promptAnswer has no promptId");
                    if (!TryString(root, "response", 16, out var response) || response is null)
                        return Fail("promptAnswer has no response");
                    if (!TryString(root, "reason", 1000, out var reason)) return Fail("promptAnswer reason is not valid");
                    var turnaround = TryInt(root, "turnaroundSeconds", out var seconds) ? Math.Max(0, seconds) : 0;
                    messages.Add(new AgentPromptAnswer(id, promptId, response, reason, turnaround));
                    return true;
                case "capture":
                    if (!TryString(root, "trigger", 32, out var trigger)) return Fail("capture trigger is not valid");
                    TryInt(root, "width", out var width);
                    TryInt(root, "height", out var height);
                    var admission = _admitCapture?.Invoke() ?? CaptureAdmission.Allow(CaptureQuota.MaxBytesPerCapture);
                    _pendingCapture = (id, string.IsNullOrWhiteSpace(trigger) ? "MANUAL" : trigger!,
                        Math.Max(0, width), Math.Max(0, height), admission);
                    return true;
                case "sessionEnding":
                    TryString(root, "reason", 32, out var endReason);
                    messages.Add(new AgentSessionEnding(id, endReason == "shutdown" ? "shutdown" : "logoff"));
                    return true;
                default:
                    return Fail("unknown frame type");
            }
        }
        catch (JsonException)
        {
            return Fail("frame is not valid JSON");
        }
        catch (InvalidOperationException)
        {
            // GetString of a string with a lone surrogate (for example "\ud800") throws this exception.
            return Fail("frame has a string that is not valid UTF-16");
        }
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    // True if the field is missing (value null) or is a string of at most maxLength characters.
    private static bool TryString(JsonElement root, string name, int maxLength, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is not null && value.Length <= maxLength;
    }

    /// <summary>A frame with a JSON answer of the service. The JSON must have at most
    /// <see cref="MaxJsonFrameBytes"/> bytes.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaxJsonFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(utf8Json), "A JSON frame has 1 to 64 KB.");
        var frame = new byte[HeaderBytes + utf8Json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)utf8Json.Length);
        utf8Json.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>A frame with the JSON form of <paramref name="value"/>.</summary>
    public static byte[] Json<T>(T value) => Frame(JsonSerializer.SerializeToUtf8Bytes(value));

    private bool Fail(string reason)
    {
        Error ??= reason;
        _body = null;
        _skipLeft = -1;
        return false;
    }
}

public enum ChannelRateDecision
{
    Accept,
    /// <summary>Over the limit: the service drops the message.</summary>
    Drop,
    /// <summary>The 10th dropped message: the service closes the channel.</summary>
    Close,
}

/// <summary>
/// The rate limit of one Agent Channel (design D7, S7): at most 5 messages per second and 30 per 10 seconds. Over the
/// limit the service drops the message; at the 10th dropped message within <see cref="DropWindow"/> it closes the
/// channel. Old drops leave the window, so rare bursts of an honest agent never add up to a close. Monotonic time
/// only. Not thread-safe: one limit per channel.
/// </summary>
public sealed class ChannelRateLimit
{
    public const int MaxPerSecond = 5;
    public const int MaxPerTenSeconds = 30;
    public const int MaxDrops = 10;
    public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(60);

    private readonly Queue<TimeSpan> _accepted = new();
    private readonly Queue<TimeSpan> _drops = new();
    private bool _closed;

    /// <summary>The dropped messages in the window of the last offer.</summary>
    public int Drops => _drops.Count;

    public ChannelRateDecision Offer(TimeSpan monotonicNow)
    {
        if (_closed) return ChannelRateDecision.Close;
        while (_accepted.Count > 0 && monotonicNow - _accepted.Peek() >= TimeSpan.FromSeconds(10))
            _accepted.Dequeue();
        while (_drops.Count > 0 && monotonicNow - _drops.Peek() >= DropWindow)
            _drops.Dequeue();
        var lastSecond = _accepted.Count(t => monotonicNow - t < TimeSpan.FromSeconds(1));
        if (_accepted.Count >= MaxPerTenSeconds || lastSecond >= MaxPerSecond)
        {
            _drops.Enqueue(monotonicNow);
            if (_drops.Count < MaxDrops) return ChannelRateDecision.Drop;
            _closed = true;
            return ChannelRateDecision.Close;
        }
        _accepted.Enqueue(monotonicNow);
        return ChannelRateDecision.Accept;
    }
}
