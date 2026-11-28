using System.Text;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class ProbeLineReaderTests
{
    private const long Now = 1_000_000;

    private static List<object> Read(ProbeLineReader reader, params string[] lines)
    {
        var output = new List<object>();
        foreach (var line in lines) reader.Feed(Encoding.UTF8.GetBytes(line + "\n"), Now, output);
        return output;
    }

    private static ProbeSample One(string line)
    {
        var output = Read(new ProbeLineReader(), line);
        return Assert.IsType<ProbeSample>(Assert.Single(output));
    }

    [Fact]
    public void Window_sample_with_each_entry()
    {
        var sample = One("""
            {"t":100,"fg":{"hwnd":1,"tid":2,"pid":3},"focus":{"hwnd":4,"tid":5,"pid":6},
             "children":[{"hwnd":7,"tid":8,"pid":9}],"cursor":{"noActivate":true,"entries":[{"hwnd":10,"tid":11,"pid":12}]},
             "hung":{"hwnd":13,"tid":14,"pid":15},"title":"Game","desktop":"Default"}
            """.ReplaceLineEndings(""));
        Assert.Equal(ProbeSampleKind.Window, sample.Kind);
        Assert.Equal(100, sample.T);
        Assert.Equal(new ProbeWindow(1, 2, 3), sample.Foreground);
        Assert.Equal(new ProbeWindow(4, 5, 6), sample.Focus);
        Assert.Equal([new ProbeWindow(7, 8, 9)], sample.ChildEntries);
        Assert.True(sample.Cursor!.NoActivate);
        Assert.Equal([new ProbeWindow(10, 11, 12)], sample.Cursor.Entries);
        Assert.Equal(new ProbeWindow(13, 14, 15), sample.Hung);
        Assert.Equal("Game", sample.Title);
        Assert.Equal("Default", sample.Desktop);
    }

    [Fact]
    public void No_window_sample()
    {
        var sample = One("""{"t":5,"window":false}""");
        Assert.Equal(ProbeSampleKind.NoWindow, sample.Kind);
        Assert.Null(sample.Foreground);
    }

    [Fact]
    public void No_window_sample_with_the_chain_under_the_cursor()
    {
        var sample = One("""{"t":5,"dur":3,"window":false,"cursor":{"noActivate":true,"entries":[{"hwnd":10,"tid":11,"pid":12}]}}""");
        Assert.Equal(ProbeSampleKind.NoWindow, sample.Kind);
        Assert.Null(sample.Foreground);
        Assert.True(sample.Cursor!.NoActivate);
        Assert.Equal([new ProbeWindow(10, 11, 12)], sample.Cursor.Entries);
    }

    [Theory]
    [InlineData("desktop", ProbeBlindReason.Desktop)]
    [InlineData("window", ProbeBlindReason.Window)]
    [InlineData("children", ProbeBlindReason.Children)]
    public void Blind_samples(string kind, ProbeBlindReason reason)
    {
        var sample = One($$"""{"t":5,"blind":"{{kind}}"}""");
        Assert.Equal(ProbeSampleKind.Blind, sample.Kind);
        Assert.Equal(reason, sample.BlindReason);
    }

    [Fact]
    public void Log_line()
    {
        var output = Read(new ProbeLineReader(), $$"""{"log":"{{new string('x', 300)}}"}""");
        var log = Assert.IsType<ProbeLogLine>(Assert.Single(output));
        Assert.Equal(256, log.Text.Length);
    }

    [Fact]
    public void Cursor_with_9_entries_drops_only_that_field()
    {
        var entries = string.Join(",", Enumerable.Range(1, 9).Select(i => $$"""{"hwnd":{{i}},"tid":{{i}},"pid":{{i}}}"""));
        var reader = new ProbeLineReader();
        var output = Read(reader, """{"t":5,"fg":{"hwnd":1,"tid":2,"pid":3},"cursor":{"noActivate":true,"entries":[""" + entries + "]}}");
        var sample = Assert.IsType<ProbeSample>(Assert.Single(output));
        Assert.Equal(new ProbeWindow(1, 2, 3), sample.Foreground);
        Assert.Null(sample.Cursor);
        Assert.Equal(1, reader.DroppedFields);
        Assert.Equal(0, reader.DroppedLines);
    }

    [Fact]
    public void Line_of_1_MB_without_a_line_end_is_dropped_and_the_next_line_is_read()
    {
        var reader = new ProbeLineReader();
        var output = new List<object>();
        var big = new byte[1024 * 1024];
        Array.Fill(big, (byte)'a');
        reader.Feed(big, Now, output);
        Assert.Empty(output);
        reader.Feed(Encoding.UTF8.GetBytes("aaa\n{\"t\":5,\"window\":false}\n"), Now, output);
        Assert.IsType<ProbeSample>(Assert.Single(output));
        Assert.Equal(1, reader.DroppedLines);
        Assert.Equal("line over 4 KB", reader.FirstProblem);
    }

    [Fact]
    public void Line_of_exactly_4_KB_is_read()
    {
        var head = """{"t":5,"window":false,"desktop":" """;
        var line = head + new string('d', ProbeLineReader.MaxLineBytes - head.Length - 2) + "\"}";
        Assert.Equal(ProbeLineReader.MaxLineBytes, Encoding.UTF8.GetByteCount(line));
        One(line);
    }

    // Review nit: the 4 KB limit is for the line without its line end.
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Line_of_exactly_4_KB_with_either_line_end_is_read(string end)
    {
        var head = """{"t":5,"window":false,"desktop":" """;
        var line = head + new string('d', ProbeLineReader.MaxLineBytes - head.Length - 2) + "\"}";
        var reader = new ProbeLineReader();
        var output = new List<object>();
        reader.Feed(Encoding.UTF8.GetBytes(line + end), Now, output);
        Assert.Single(output);
        Assert.Equal(0, reader.DroppedLines);
    }

    [Theory]
    [InlineData(" ", "\n")]
    [InlineData(" ", "\r\n")]
    [InlineData("\r", "\n")]
    [InlineData("\r\r", "\n")]
    public void Line_over_4_KB_is_dropped_and_the_next_line_is_read(string extra, string end)
    {
        var head = """{"t":5,"window":false,"desktop":" """;
        var line = head + new string('d', ProbeLineReader.MaxLineBytes - head.Length - 2) + "\"}" + extra;
        var reader = new ProbeLineReader();
        var output = new List<object>();
        reader.Feed(Encoding.UTF8.GetBytes(line + end + "{\"t\":6,\"window\":false}\n"), Now, output);
        if (extra == "\r")
        {
            // 4096 bytes and one \r before the \n: the \r is part of the line end.
            Assert.Equal(2, output.Count);
            return;
        }
        Assert.Equal(6, Assert.IsType<ProbeSample>(Assert.Single(output)).T);
        Assert.Equal(1, reader.DroppedLines);
    }

    // Review S1: a lone surrogate escape never throws.
    [Fact]
    public void Lone_surrogate_in_the_title_drops_only_the_title()
    {
        var reader = new ProbeLineReader();
        var output = Read(reader, """{"t":5,"fg":{"hwnd":1,"tid":2,"pid":3},"title":"a\ud800b","desktop":"\udc00"}""");
        var sample = Assert.IsType<ProbeSample>(Assert.Single(output));
        Assert.Equal(new ProbeWindow(1, 2, 3), sample.Foreground);
        Assert.Null(sample.Title);
        Assert.Null(sample.Desktop);
        Assert.Equal(2, reader.DroppedFields);
        Assert.Equal(5, reader.LastT);
    }

    [Theory]
    [InlineData("""{"log":"\ud800"}""")]
    [InlineData("""{"t":5,"blind":"\ud800"}""")]
    public void Lone_surrogate_in_a_line_drops_only_that_line(string line)
    {
        var reader = new ProbeLineReader();
        var output = Read(reader, line, """{"t":6,"window":false}""");
        Assert.Equal(6, Assert.IsType<ProbeSample>(Assert.Single(output)).T);
        Assert.Equal(1, reader.DroppedLines);
    }

    [Fact]
    public void Wrong_type_drops_only_that_field()
    {
        var reader = new ProbeLineReader();
        var output = Read(reader, """{"t":5,"fg":{"hwnd":1,"tid":2,"pid":3},"focus":"x","title":7,"children":[{"hwnd":"a"}]}""");
        var sample = Assert.IsType<ProbeSample>(Assert.Single(output));
        Assert.Null(sample.Focus);
        Assert.Null(sample.Title);
        Assert.Empty(sample.ChildEntries);
        Assert.Equal(3, reader.DroppedFields);
    }

    [Theory]
    [InlineData("""{"fg":{"hwnd":1,"tid":2,"pid":3}}""")]
    [InlineData("""{"t":"5","fg":{"hwnd":1,"tid":2,"pid":3}}""")]
    [InlineData("""{"t":-1,"window":false}""")]
    [InlineData("""{"t":5,"fg":{"hwnd":1,"tid":2,"pid":0}}""")]
    [InlineData("""{"t":5,"fg":"x"}""")]
    [InlineData("""{"t":5}""")]
    [InlineData("""{"t":5,"blind":"other"}""")]
    [InlineData("""not json""")]
    public void Line_without_a_valid_t_or_kind_is_dropped(string line)
    {
        var reader = new ProbeLineReader();
        Assert.Empty(Read(reader, line));
        Assert.Equal(1, reader.DroppedLines);
        Assert.Null(reader.LastT);
    }

    [Fact]
    public void Line_whose_t_goes_back_or_repeats_is_dropped()
    {
        var reader = new ProbeLineReader();
        var output = Read(reader, """{"t":10,"window":false}""", """{"t":9,"window":false}""", """{"t":10,"window":false}""", """{"t":11,"window":false}""");
        Assert.Equal([10L, 11L], output.Cast<ProbeSample>().Select(s => s.T));
        Assert.Equal(2, reader.DroppedLines);
    }

    [Fact]
    public void Line_whose_t_is_after_the_service_tick_is_dropped()
    {
        var reader = new ProbeLineReader();
        Assert.Empty(Read(reader, $$"""{"t":{{Now + 1}},"window":false}"""));
        Assert.Equal(1, reader.DroppedLines);
    }

    [Fact]
    public void Lines_split_over_many_reads_and_crlf()
    {
        var reader = new ProbeLineReader();
        var output = new List<object>();
        var bytes = Encoding.UTF8.GetBytes("{\"t\":1,\"window\":false}\r\n{\"t\":2,\"blind\":\"desktop\"}\n");
        foreach (var b in bytes) reader.Feed([b], Now, output);
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Clean_cuts_and_replaces_lone_surrogates_and_control_characters()
    {
        var text = "a\u0007b\uD800c" + new string('x', 300);
        var clean = ProbeLineReader.Clean(text, 256);
        Assert.Equal(256, clean.Length);
        Assert.StartsWith("a�b�c", clean);
        Assert.Equal("😀", ProbeLineReader.Clean("😀", 256));
        Assert.Equal("", ProbeLineReader.Clean("😀", 1));
    }

    [Fact]
    public void Line_with_dur_gives_the_duration()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(15000), One("""{"t":5,"window":false,"dur":15000}""").Duration);
    }

    [Fact]
    public void Line_without_dur_counts_0_with_one_problem()
    {
        var reader = new ProbeLineReader();
        var sample = Assert.IsType<ProbeSample>(Assert.Single(Read(reader, """{"t":5,"window":false}""")));
        Assert.Equal(TimeSpan.Zero, sample.Duration);
        Assert.Equal(1, reader.MissingDurations);
        Assert.Equal("sample line without dur", reader.FirstProblem);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("24200.5")]
    [InlineData("-1")]
    public void Dur_with_a_wrong_type_counts_0_and_drops_only_that_field(string dur)
    {
        var reader = new ProbeLineReader();
        var sample = Assert.IsType<ProbeSample>(Assert.Single(Read(reader, "{\"t\":5,\"blind\":\"desktop\",\"dur\":" + dur + "}")));
        Assert.Equal(TimeSpan.Zero, sample.Duration);
        Assert.Equal(ProbeBlindReason.Desktop, sample.BlindReason);
        Assert.Equal(1, reader.DroppedFields);
    }
}
