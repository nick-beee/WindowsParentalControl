using System.Buffers.Binary;
using System.Text.Json;

namespace ParentalControl.Domain.Sessions;

/// <summary>An answer of the service on the Agent Channel: the id of the request and the whole JSON object.</summary>
public sealed record AgentAnswer(long Id, string Type, byte[] Json);

/// <summary>
/// The framing of the answers of the service, on the side of the Session Agent (session-probe-and-agent-channel,
/// design D7): a 4-byte little-endian length, then a UTF-8 JSON object with <c>id</c> and <c>type</c>, at most
/// <see cref="AgentChannelCodec.MaxJsonFrameBytes"/> bytes. The length is checked before the allocation. An error is
/// final: the agent closes the channel and exits. Not thread-safe: one decoder per channel.
/// </summary>
public sealed class AgentAnswerDecoder
{
    private readonly byte[] _header = new byte[AgentChannelCodec.HeaderBytes];
    private int _headerFill;
    private byte[]? _body;
    private int _bodyFill;

    /// <summary>The reason of the first error, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Takes the next bytes and adds each complete answer. Returns false at the first error.</summary>
    public bool Feed(ReadOnlySpan<byte> data, List<AgentAnswer> answers)
    {
        if (Error is not null) return false;
        while (!data.IsEmpty)
        {
            if (_body is null)
            {
                var take = Math.Min(_header.Length - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill < _header.Length) break;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
                if (length == 0 || length > AgentChannelCodec.MaxJsonFrameBytes)
                    return Fail($"answer length {length} is outside 1..{AgentChannelCodec.MaxJsonFrameBytes}");
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
            if (!Complete(frame, answers)) return false;
        }
        return true;
    }

    private bool Complete(byte[] frame, List<AgentAnswer> answers)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("answer is not a JSON object");
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id))
                return Fail("answer has no id");
            var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? ""
                : "";
            answers.Add(new AgentAnswer(id, type, frame));
            return true;
        }
        catch (JsonException)
        {
            return Fail("answer is not valid JSON");
        }
        catch (InvalidOperationException)
        {
            return Fail("answer has a string that is not valid UTF-16");
        }
    }

    private bool Fail(string reason)
    {
        Error ??= reason;
        _body = null;
        return false;
    }
}
