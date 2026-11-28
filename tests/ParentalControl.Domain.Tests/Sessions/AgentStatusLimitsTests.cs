using System.Text.Json;
using ParentalControl.Domain.Sessions;
using Xunit;

namespace ParentalControl.Domain.Tests.Sessions;

// Review of task groups 5 and 6, item 1: the status answer must fit in one JSON frame of the Agent Channel, also with
// 1000 Suspended Apps and 1000 Pending Suspensions.
public class AgentStatusLimitsTests
{
    private static readonly DateTime Start = new DateTime(2026, 11, 28, 1, 23, 45, DateTimeKind.Utc).AddTicks(1234567);

    private static IEnumerable<(int, DateTime)> Processes(int count) =>
        Enumerable.Range(0, count).Select(i => (int.MaxValue - i, Start));

    // A character that the default JSON writer escapes as \u0001 (6 bytes).
    private static string Worst(int length) => new('\u0001', length + 50);

    [Fact]
    public void Lists_keep_at_most_200_entries_and_the_total()
    {
        var (apps, total) = AgentStatusLimits.Apps(Processes(1000));
        Assert.Equal(AgentStatusLimits.MaxApps, apps.Count);
        Assert.Equal(1000, total);
        Assert.Equal(int.MaxValue, apps[0].pid);
        Assert.Equal(DateTimeKind.Utc, apps[0].startTimeUtc.Kind);
    }

    [Fact]
    public void Short_list_keeps_each_entry()
    {
        var (apps, total) = AgentStatusLimits.Apps(Processes(3));
        Assert.Equal(3, apps.Count);
        Assert.Equal(3, total);
    }

    [Fact]
    public void Largest_status_answer_fits_in_one_frame()
    {
        var suspended = AgentStatusLimits.Apps(Processes(1000));
        var pending = AgentStatusLimits.Apps(Processes(1000));
        var prompt = AgentStatusLimits.Prompt(int.MaxValue, Worst(AgentStatusLimits.MaxPromptChars), Worst(AgentStatusLimits.MaxShortChars),
            int.MinValue, 120, Enumerable.Range(0, 50).Select(_ => Worst(AgentStatusLimits.MaxOptionChars)));
        var status = new AgentStatus(
            username: AgentStatusLimits.Text(Worst(AgentStatusLimits.MaxShortChars), AgentStatusLimits.MaxShortChars),
            isRestricted: true,
            remainingSeconds: int.MinValue,
            curfew: AgentStatusLimits.Text(Worst(AgentStatusLimits.MaxShortChars), AgentStatusLimits.MaxShortChars),
            isLocked: true,
            isCurfewClamped: true,
            lastDeclinedId: int.MinValue,
            lastDeclinedReason: AgentStatusLimits.Text(Worst(AgentStatusLimits.MaxReasonChars), AgentStatusLimits.MaxReasonChars),
            lastDeclinedTime: DateTime.MaxValue,
            captureRequested: true,
            watchIntervalSeconds: int.MinValue,
            language: AgentStatusLimits.Text(Worst(AgentStatusLimits.MaxShortChars), AgentStatusLimits.MaxShortChars),
            activePrompt: prompt,
            suspendedApps: suspended.Apps,
            suspendedAppsTotal: suspended.Total,
            pendingApps: pending.Apps,
            pendingAppsTotal: pending.Total);

        // The same envelope and serializer as AgentChannelHost.StatusAnswer.
        var json = JsonSerializer.SerializeToUtf8Bytes(new { id = long.MaxValue, type = "status", ok = true, status });
        Assert.True(json.Length <= AgentChannelCodec.MaxJsonFrameBytes, $"{json.Length} bytes");
        Assert.Equal(AgentChannelCodec.HeaderBytes + json.Length, AgentChannelCodec.Frame(json).Length);
        Assert.Contains("\"suspendedAppsTotal\":1000", System.Text.Encoding.UTF8.GetString(json));
        Assert.Equal(AgentStatusLimits.MaxOptions, prompt.options.Count);
    }

    [Fact]
    public void Text_cut_keeps_surrogate_pairs_whole()
    {
        var text = new string('a', 9) + "\U0001F600";
        Assert.Equal(new string('a', 9), AgentStatusLimits.Text(text, 10));
        Assert.Equal("abc", AgentStatusLimits.Text("abc", 10));
        Assert.Null(AgentStatusLimits.Text(null, 10));
    }

    [Fact]
    public void Agent_reads_the_capped_answer()
    {
        // The agent reads the answer with its own records and ignores the total fields.
        var status = new AgentStatus("Kid", true, 60, "08:00 – 20:00", false, false, null, null, null, false, 0, "de", null,
            AgentStatusLimits.Apps(Processes(2)).Apps, 2, [], 0);
        var json = JsonSerializer.Serialize(new { id = 1L, type = "status", ok = true, status });
        using var doc = JsonDocument.Parse(json);
        var apps = doc.RootElement.GetProperty("status").GetProperty("suspendedApps");
        Assert.Equal(2, apps.GetArrayLength());
        Assert.Equal(int.MaxValue, apps[0].GetProperty("pid").GetInt32());
        Assert.Equal("08:00 – 20:00", doc.RootElement.GetProperty("status").GetProperty("curfew").GetString());
    }
}
