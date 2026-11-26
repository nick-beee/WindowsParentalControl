using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class PendingSuspensionsTests
{
    private const string Game = @"C:\Steam\steamapps\common\Portal\portal.exe";
    private static readonly DateTime T0 = new(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 11, 26, 13, 0, 0, DateTimeKind.Utc);

    private static SuspendAction Action(int pid, DateTime? start = null, bool recorded = false) =>
        new(new SessionProcess(pid, start ?? Start, Game, 1, IsSuspended: false), 2, AppCategory.Games, Game, recorded);

    [Fact]
    public void New_action_is_pending_and_not_ready()
    {
        var pending = new PendingSuspensions();
        var step = pending.Step([Action(10)], T0);
        Assert.Empty(step.Ready);
        Assert.Equal([10], step.Pending.Select(a => a.Process.Pid));
        Assert.Equal([10], pending.Pending.Select(a => a.Process.Pid));
        Assert.Equal(T0.AddSeconds(10), pending.NextDueUtc);
    }

    [Fact]
    public void Action_is_ready_at_10_seconds_and_not_before()
    {
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        Assert.Empty(pending.Step([Action(10)], T0.AddSeconds(9.99)).Ready);
        var step = pending.Step([Action(10)], T0.AddSeconds(10));
        Assert.Equal([10], step.Ready.Select(a => a.Process.Pid));
        Assert.Empty(step.Pending);
    }

    [Fact]
    public void Ready_action_leaves_the_list()
    {
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        pending.Step([Action(10)], T0.AddSeconds(11));
        Assert.Empty(pending.Pending);
        Assert.Null(pending.NextDueUtc);
    }

    [Fact]
    public void Recorded_action_that_runs_again_also_waits()
    {
        var pending = new PendingSuspensions();
        Assert.Empty(pending.Step([Action(10, recorded: true)], T0).Ready);
        Assert.True(Assert.Single(pending.Step([Action(10, recorded: true)], T0.AddSeconds(10)).Ready).IsRecorded);
    }

    [Fact]
    public void Action_that_is_not_due_any_more_is_dropped()
    {
        // Spec scenario "Bonus Minutes during a Pending Suspension": the planner does not list the game any more.
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        var step = pending.Step([], T0.AddSeconds(5));
        Assert.Empty(step.Ready);
        Assert.Empty(pending.Pending);
        Assert.Null(pending.NextDueUtc);

        // If it is due again later, it waits the full time again.
        Assert.Empty(pending.Step([Action(10)], T0.AddSeconds(12)).Ready);
        Assert.Equal(T0.AddSeconds(22), pending.NextDueUtc);
    }

    [Fact]
    public void Pid_with_a_new_start_time_is_a_new_entry()
    {
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        var step = pending.Step([Action(10, Start.AddHours(1))], T0.AddSeconds(10));
        Assert.Empty(step.Ready);
        Assert.Equal(T0.AddSeconds(20), pending.NextDueUtc);
    }

    [Fact]
    public void Next_due_time_is_the_earliest()
    {
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        pending.Step([Action(10), Action(11)], T0.AddSeconds(4));
        Assert.Equal(T0.AddSeconds(10), pending.NextDueUtc);

        var step = pending.Step([Action(10), Action(11)], T0.AddSeconds(10));
        Assert.Equal([10], step.Ready.Select(a => a.Process.Pid));
        Assert.Equal([11], step.Pending.Select(a => a.Process.Pid));
        Assert.Equal(T0.AddSeconds(14), pending.NextDueUtc);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var pending = new PendingSuspensions();
        pending.Step([Action(10)], T0);
        pending.Clear();
        Assert.Empty(pending.Pending);
        Assert.Null(pending.NextDueUtc);
    }
}
