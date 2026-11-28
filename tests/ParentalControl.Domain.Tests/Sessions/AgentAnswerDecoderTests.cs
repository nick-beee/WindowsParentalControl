using System.Buffers.Binary;
using System.Text;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class AgentAnswerDecoderTests
{
    private static byte[] Frame(string json) => AgentChannelCodec.Frame(Encoding.UTF8.GetBytes(json));

    private static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    [Fact]
    public void Answer_in_parts_gives_its_id_and_type()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        var frame = Frame("""{"id":7,"type":"status","status":{"remainingSeconds":60}}""");
        foreach (var b in frame) Assert.True(decoder.Feed(new[] { b }, answers));
        var answer = Assert.Single(answers);
        Assert.Equal(7, answer.Id);
        Assert.Equal("status", answer.Type);
    }

    [Fact]
    public void Two_answers_in_one_read()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        var bytes = Frame("""{"id":1,"type":"status"}""").Concat(Frame("""{"id":2,"type":"promptAnswer","ok":true}""")).ToArray();
        Assert.True(decoder.Feed(bytes, answers));
        Assert.Equal(new long[] { 1, 2 }, answers.Select(a => a.Id));
    }

    [Fact]
    public void Answer_one_byte_over_the_limit_is_refused()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        Assert.False(decoder.Feed(Header(AgentChannelCodec.MaxJsonFrameBytes + 1), answers));
        Assert.NotNull(decoder.Error);
        Assert.Empty(answers);
    }

    [Fact]
    public void Length_of_2_GB_is_refused_without_an_allocation()
    {
        var decoder = new AgentAnswerDecoder();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(decoder.Feed(Header(2u * 1024 * 1024 * 1024), new List<AgentAnswer>()));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void Error_is_final()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        Assert.False(decoder.Feed(Header(0), answers));
        Assert.False(decoder.Feed(Frame("""{"id":1,"type":"status"}"""), answers));
        Assert.Empty(answers);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("""{"type":"status"}""")]
    [InlineData("""{"id":"1","type":"status"}""")]
    [InlineData("not json")]
    public void Bad_answer_fails_without_an_exception(string json)
    {
        var decoder = new AgentAnswerDecoder();
        Assert.False(decoder.Feed(Frame(json), new List<AgentAnswer>()));
        Assert.NotNull(decoder.Error);
    }

    [Fact]
    public void Truncated_answer_gives_nothing()
    {
        var decoder = new AgentAnswerDecoder();
        var answers = new List<AgentAnswer>();
        var frame = Frame("""{"id":3,"type":"status"}""");
        Assert.True(decoder.Feed(frame.AsSpan(0, frame.Length - 1), answers));
        Assert.Empty(answers);
    }
}
