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

/// <summary>A screen capture (<c>capture</c>): a JSON frame, then the JPEG bytes as a second frame.</summary>
public sealed record AgentCapture(long Id, string Trigger, int Width, int Height, byte[] Jpeg) : AgentMessage(Id);

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
    private int _headerFill;
    private byte[]? _body;
    private int _bodyFill;
    private (long Id, string Trigger, int Width, int Height)? _pendingCapture;

    /// <summary>The reason of the first error, or null. After an error the channel must close.</summary>
    public string? Error { get; private set; }

    /// <summary>True between the first byte of a frame and its last byte. The service closes a channel whose frame
    /// does not arrive within 10 seconds.</summary>
    public bool IsInsideFrame => _headerFill > 0 || _body is not null;

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
            if (_body is null)
            {
                var take = Math.Min(HeaderBytes - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill < HeaderBytes) break;

                var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
                var limit = _pendingCapture is null ? MaxJsonFrameBytes : CaptureQuota.MaxBytesPerCapture;
                if (length == 0 || length > limit)
                    return Fail($"frame length {length} is outside 1..{limit}");
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
                    _pendingCapture = (id, string.IsNullOrWhiteSpace(trigger) ? "MANUAL" : trigger!,
                        Math.Max(0, width), Math.Max(0, height));
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
/// limit the service drops the message; at the 10th dropped message it closes the channel. Monotonic time only.
/// Not thread-safe: one limit per channel.
/// </summary>
public sealed class ChannelRateLimit
{
    public const int MaxPerSecond = 5;
    public const int MaxPerTenSeconds = 30;
    public const int MaxDrops = 10;

    private readonly Queue<TimeSpan> _accepted = new();

    public int Drops { get; private set; }

    public ChannelRateDecision Offer(TimeSpan monotonicNow)
    {
        if (Drops >= MaxDrops) return ChannelRateDecision.Close;
        while (_accepted.Count > 0 && monotonicNow - _accepted.Peek() >= TimeSpan.FromSeconds(10))
            _accepted.Dequeue();
        var lastSecond = _accepted.Count(t => monotonicNow - t < TimeSpan.FromSeconds(1));
        if (_accepted.Count >= MaxPerTenSeconds || lastSecond >= MaxPerSecond)
        {
            Drops++;
            return Drops >= MaxDrops ? ChannelRateDecision.Close : ChannelRateDecision.Drop;
        }
        _accepted.Enqueue(monotonicNow);
        return ChannelRateDecision.Accept;
    }
}
