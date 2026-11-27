using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class ForegroundSampleCounterTests
{
    private static readonly DateTime T0 = new(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CountedProcess Game = new(new ProcessIdentity(10, T0), @"C:\Games\game.exe", AppCategory.Games, ProbeRole.Foreground, false);
    private static readonly CountedProcess Study = new(new ProcessIdentity(20, T0), @"C:\Study\study.exe", AppCategory.Study, ProbeRole.Foreground, false);
    private static readonly CountedProcess[] None = [];

    // A driver that keeps the receipt clocks of the service in step with t, as on a PC that does not sleep.
    private sealed class Driver
    {
        public readonly ForegroundSampleCounter Counter = new();
        private TimeSpan _sleep = TimeSpan.Zero;

        public void Sleep(TimeSpan time) => _sleep += time;

        public SampleCredit At(double seconds, IReadOnlyCollection<CountedProcess> counted, bool blind = false, int session = 1,
            double? duration = null, double? receiptSeconds = null)
        {
            var t = (long)(seconds * 1000);
            var receipt = (long)((receiptSeconds ?? seconds) * 1000) + 100_000;
            return Counter.Accept(session, t, duration is { } d ? TimeSpan.FromSeconds(d) : null, receipt,
                TimeSpan.FromMilliseconds(receipt) - _sleep, blind, counted);
        }
    }

    private static double S(SampleCredit c) => c.Time.TotalSeconds;

    [Fact]
    public void Three_samples_5_s_apart_give_15_s_after_a_first_sample()
    {
        var d = new Driver();
        Assert.Equal(0, S(d.At(100, [Game])));
        Assert.Equal(15, S(d.At(105, [Game])) + S(d.At(110, [Game])) + S(d.At(115, [Game])));
    }

    [Fact]
    public void Two_minute_gap_with_another_program_gives_12_s_and_one_gap_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(220, [Study]);
        Assert.Equal(12, S(c));
        Assert.True(c.GapItem);
        Assert.True(c.LongGap);
    }

    [Fact]
    public void Gap_of_34_s_with_the_same_process_gives_34_s_and_no_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(134, [Game]);
        Assert.Equal(34, S(c));
        Assert.False(c.GapItem);
        Assert.True(c.LongGap);
    }

    [Fact]
    public void Two_minute_gap_with_the_same_process_gives_60_s_and_one_gap_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(220, [Game]);
        Assert.Equal(60, S(c));
        Assert.True(c.GapItem);
    }

    [Fact]
    public void Gap_of_34_s_with_a_blind_sample_at_one_end_gives_12_s_and_one_item()
    {
        var d = new Driver();
        d.At(100, [Game], blind: true);
        var c = d.At(134, [Game]);
        Assert.Equal(12, S(c));
        Assert.True(c.GapItem);

        var e = new Driver();
        e.At(100, [Game]);
        var c2 = e.At(134, [Game], blind: true);
        Assert.Equal(12, S(c2));
        Assert.True(c2.GapItem);
    }

    [Fact]
    public void Gap_of_34_s_with_the_same_process_in_another_category_gives_12_s_and_one_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(134, [Game with { Category = AppCategory.Study }]);
        Assert.Equal(12, S(c));
        Assert.True(c.GapItem);
    }

    [Fact]
    public void Gap_of_34_s_between_two_samples_with_only_System_gives_0_s_and_no_item()
    {
        var d = new Driver();
        d.At(100, None);
        var c = d.At(134, None);
        Assert.Equal(0, S(c));
        Assert.False(c.GapItem);
    }

    [Fact]
    public void Same_exe_with_a_new_pid_is_the_same_program()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(134, [Game with { Process = new ProcessIdentity(99, T0.AddSeconds(20)) }]);
        Assert.Equal(34, S(c));
        Assert.False(c.GapItem);
    }

    [Fact]
    public void Cursor_entries_do_not_change_the_sameness()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(134, [Game, Study with { Role = ProbeRole.Cursor }]);
        Assert.Equal(34, S(c));
        Assert.False(c.GapItem);
    }

    [Fact]
    public void Gap_of_30_s_over_an_unexpected_end_with_the_same_process_gives_30_s()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.UnexpectedEnd(1);
        var c = d.At(130, [Game]);
        Assert.Equal(30, S(c));
        Assert.False(c.GapItem);
    }

    [Fact]
    public void Gap_over_an_unexpected_end_with_another_program_gives_12_s_and_no_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.UnexpectedEnd(1);
        var c = d.At(134, [Study]);
        Assert.Equal(12, S(c));
        Assert.False(c.GapItem);
        Assert.True(c.LongGap);
        var next = d.At(250, [Game]);
        Assert.True(next.GapItem);
    }

    [Fact]
    public void First_sample_after_a_planned_start_gives_0_s()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.PlannedStart(1);
        var c = d.At(105, [Game]);
        Assert.Equal(0, S(c));
        Assert.False(c.GapItem);
        Assert.Equal(5, S(d.At(110, [Game])));
    }

    [Fact]
    public void First_sample_after_an_unexpected_end_4_s_after_the_last_sample_gives_4_s()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.UnexpectedEnd(1);
        Assert.Equal(4, S(d.At(104, [Game])));
    }

    [Fact]
    public void Blind_sample_between_two_readable_samples_moves_the_time()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.At(105, None, blind: true);
        Assert.Equal(5, S(d.At(110, [Game])));
    }

    [Fact]
    public void Dropped_line_does_not_move_the_time()
    {
        // The reader drops the line at 105, so the counter never sees it.
        var d = new Driver();
        d.At(100, [Game]);
        Assert.Equal(10, S(d.At(110, [Game])));
    }

    [Fact]
    public void Lines_that_arrive_together_with_t_5_s_apart_still_give_5_s_each()
    {
        var d = new Driver();
        d.At(100, [Game]);
        Assert.Equal(5, S(d.At(105, [Game], receiptSeconds: 130)));
        Assert.Equal(5, S(d.At(110, [Game], receiptSeconds: 130.01)));
        Assert.Equal(5, S(d.At(115, [Game], receiptSeconds: 130.02)));
    }

    [Fact]
    public void First_sample_after_a_resume_gives_0_s()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.Resumed();
        Assert.Equal(0, S(d.At(105, [Game])));
    }

    [Fact]
    public void Line_after_a_sleep_without_a_power_event_gives_0_s()
    {
        // GetTickCount64 counted 10 minutes of sleep, the unbiased interrupt time 0.
        var d = new Driver();
        d.At(100, [Game]);
        d.Sleep(TimeSpan.FromMinutes(10));
        var c = d.At(705, [Game]);
        Assert.Equal(0, S(c));
        Assert.False(c.GapItem);
        Assert.Equal(5, S(d.At(710, [Game])));
    }

    [Fact]
    public void Two_sessions_do_not_share_a_time()
    {
        var d = new Driver();
        d.At(100, [Game], session: 1);
        Assert.Equal(0, S(d.At(105, [Game], session: 2)));
        Assert.Equal(10, S(d.At(110, [Game], session: 1)));
        Assert.Equal(5, S(d.At(110, [Game], session: 2)));
    }

    [Fact]
    public void Sample_that_took_more_than_12_s_counts_at_most_12_s_and_is_a_gap_item()
    {
        var d = new Driver();
        d.At(100, [Game]);
        var c = d.At(134, [Game], duration: 24.2);
        Assert.Equal(12, S(c));
        Assert.True(c.GapItem);
        Assert.True(c.LongGap);
    }

    [Fact]
    public void Session_end_resets_the_state()
    {
        var d = new Driver();
        d.At(100, [Game]);
        d.Counter.SessionEnded(1);
        Assert.Equal(0, S(d.At(105, [Game])));
    }
}
