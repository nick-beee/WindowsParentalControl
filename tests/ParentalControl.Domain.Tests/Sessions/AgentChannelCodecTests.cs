using System.Buffers.Binary;
using System.Text;
using ParentalControl.Domain.Captures;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class AgentChannelCodecTests
{
    private static byte[] Json(string json) => AgentChannelCodec.Frame(Encoding.UTF8.GetBytes(json));

    private static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    private static List<AgentMessage> Decode(AgentChannelCodec codec, params byte[][] chunks)
    {
        var messages = new List<AgentMessage>();
        foreach (var chunk in chunks) codec.Feed(chunk, messages);
        return messages;
    }

    // Review B1: a hostile frame never throws. The raw literals hold the six characters \ud800 (a JSON escape of a
    // lone surrogate), not the char itself.
    [Theory]
    [InlineData("""{"type":"status","id":"x"}""")]
    [InlineData("""{"type":"status","id":true}""")]
    [InlineData("""{"type":"status","id":[1]}""")]
    [InlineData("""{"type":"\ud800","id":1}""")]
    [InlineData("""{"type":"promptAnswer","id":2,"promptId":7,"response":"YES","reason":"\ud800"}""")]
    [InlineData("""{"type":"promptAnswer","id":2,"promptId":7,"response":"\udc00"}""")]
    [InlineData("""{"type":"capture","id":3,"trigger":"\ud800"}""")]
    [InlineData("""{"type":"sessionEnding","id":4,"reason":"\ud800"}""")]
    public void Hostile_frame_fails_without_an_exception(string json)
    {
        var codec = new AgentChannelCodec();
        var messages = new List<AgentMessage>();
        Assert.False(codec.Feed(Json(json), messages));
        Assert.NotNull(codec.Error);
        Assert.Empty(messages);
        Assert.False(codec.Feed(Json("""{"type":"status","id":1}"""), messages));
    }

    [Fact]
    public void Valid_frame_of_each_type()
    {
        var codec = new AgentChannelCodec();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var messages = Decode(codec,
            Json("""{"type":"status","id":1}"""),
            Json("""{"type":"promptAnswer","id":2,"promptId":7,"response":"YES","reason":"ok","turnaroundSeconds":12}"""),
            Json("""{"type":"capture","id":3,"trigger":"WATCH","width":1920,"height":1080}"""),
            AgentChannelCodec.Frame(jpeg),
            Json("""{"type":"sessionEnding","id":4,"reason":"shutdown"}"""));

        Assert.Null(codec.Error);
        Assert.Equal(new AgentStatusRequest(1), messages[0]);
        Assert.Equal(new AgentPromptAnswer(2, 7, "YES", "ok", 12), messages[1]);
        var capture = Assert.IsType<AgentCapture>(messages[2]);
        Assert.Equal((3L, "WATCH", 1920, 1080), (capture.Id, capture.Trigger, capture.Width, capture.Height));
        Assert.Equal(jpeg, capture.Jpeg);
        Assert.Equal(new AgentSessionEnding(4, "shutdown"), messages[3]);
        Assert.False(codec.IsInsideFrame);
        Assert.True(codec.EndOfStream());
    }

    [Fact]
    public void Frame_in_single_bytes_gives_the_same_message()
    {
        var codec = new AgentChannelCodec();
        var frame = Json("""{"type":"status","id":9}""");
        var messages = Decode(codec, frame.Select(b => new[] { b }).ToArray());
        Assert.Equal([new AgentStatusRequest(9)], messages);
    }

    [Fact]
    public void Json_frame_one_byte_over_the_limit_is_refused()
    {
        var atLimit = new AgentChannelCodec();
        atLimit.Feed(Header(AgentChannelCodec.MaxJsonFrameBytes), []);
        Assert.Null(atLimit.Error);

        var codec = new AgentChannelCodec();
        Assert.False(codec.Feed(Header(AgentChannelCodec.MaxJsonFrameBytes + 1), []));
        Assert.NotNull(codec.Error);
        Assert.Equal(0, codec.BufferedCapacity);
    }

    [Fact]
    public void Jpeg_frame_one_byte_over_the_capture_limit_is_refused()
    {
        var codec = new AgentChannelCodec();
        codec.Feed(Json("""{"type":"capture","id":1}"""), []);
        Assert.True(codec.ExpectsCaptureBytes);
        Assert.False(codec.Feed(Header((uint)CaptureQuota.MaxBytesPerCapture + 1), []));
        Assert.NotNull(codec.Error);
    }

    [Fact]
    public void Length_of_2_GB_is_refused_without_an_allocation()
    {
        var codec = new AgentChannelCodec();
        Assert.False(codec.Feed(Header(2u * 1024 * 1024 * 1024), []));
        Assert.NotNull(codec.Error);
        Assert.Equal(0, codec.BufferedCapacity);
        Assert.False(codec.Feed(Json("""{"type":"status","id":1}"""), []));
    }

    [Fact]
    public void Truncated_frame_is_refused()
    {
        var codec = new AgentChannelCodec();
        var frame = Json("""{"type":"status","id":1}""");
        var messages = Decode(codec, frame[..^3]);
        Assert.Empty(messages);
        Assert.True(codec.IsInsideFrame);
        Assert.False(codec.EndOfStream());
        Assert.NotNull(codec.Error);
    }

    [Fact]
    public void Capture_without_its_jpeg_is_refused_at_the_end()
    {
        var codec = new AgentChannelCodec();
        codec.Feed(Json("""{"type":"capture","id":1}"""), []);
        Assert.False(codec.EndOfStream());
    }

    [Fact]
    public void Frame_with_user_and_session_gives_a_message_without_them()
    {
        var codec = new AgentChannelCodec();
        var messages = Decode(codec,
            Json("""{"type":"status","id":1,"user":"Mama"}"""),
            Json("""{"type":"sessionEnding","id":2,"sessionId":0,"user":"Mama","reason":"logoff"}"""));
        Assert.Equal([new AgentStatusRequest(1), new AgentSessionEnding(2, "logoff")], messages);
    }

    [Theory]
    [InlineData("""{"type":"status"}""")]
    [InlineData("""{"type":"other","id":1}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    [InlineData("""{"type":"promptAnswer","id":1,"promptId":"x","response":"YES"}""")]
    [InlineData("""{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}},"type":"status","id":1}""")]
    public void Bad_frame_is_refused(string json)
    {
        var codec = new AgentChannelCodec();
        Assert.False(codec.Feed(Json(json), []));
        Assert.NotNull(codec.Error);
    }

    [Fact]
    public void Zero_length_is_refused()
    {
        var codec = new AgentChannelCodec();
        Assert.False(codec.Feed(Header(0), []));
    }

    [Fact]
    public void Encoder_refuses_a_json_frame_over_the_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentChannelCodec.Frame(new byte[AgentChannelCodec.MaxJsonFrameBytes + 1]));
    }
}

public class ChannelRateLimitTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Five_per_second_then_drop()
    {
        var limit = new ChannelRateLimit();
        for (var i = 0; i < 5; i++) Assert.Equal(ChannelRateDecision.Accept, limit.Offer(Ms(i * 10)));
        Assert.Equal(ChannelRateDecision.Drop, limit.Offer(Ms(100)));
        Assert.Equal(ChannelRateDecision.Accept, limit.Offer(Ms(1000)));
    }

    [Fact]
    public void Thirty_per_ten_seconds_then_drop()
    {
        var limit = new ChannelRateLimit();
        for (var i = 0; i < 30; i++) Assert.Equal(ChannelRateDecision.Accept, limit.Offer(Ms(i * 300)));
        Assert.Equal(ChannelRateDecision.Drop, limit.Offer(Ms(9100)));
        Assert.Equal(ChannelRateDecision.Accept, limit.Offer(Ms(10_000)));
    }

    [Fact]
    public void Status_poll_every_5_seconds_is_never_dropped()
    {
        var limit = new ChannelRateLimit();
        for (var i = 0; i < 1000; i++) Assert.Equal(ChannelRateDecision.Accept, limit.Offer(Ms(i * 5000)));
    }

    [Fact]
    public void Tenth_drop_closes_the_channel()
    {
        var limit = new ChannelRateLimit();
        var decisions = Enumerable.Range(0, 15).Select(_ => limit.Offer(Ms(0))).ToList();
        Assert.Equal(5, decisions.Count(d => d == ChannelRateDecision.Accept));
        Assert.Equal(9, decisions.Count(d => d == ChannelRateDecision.Drop));
        Assert.Equal(ChannelRateDecision.Close, decisions[14]);
        Assert.Equal(ChannelRateDecision.Close, limit.Offer(Ms(60_000)));
    }
}
