using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class AgentLaunchRegistryTests
{
    private static readonly DateTime Start = new(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);
    private static readonly AgentLaunchRecord Agent = new(1, new ProcessIdentity(100, Start), "S-1-5-21-1-2-3-1001");

    [Fact]
    public void First_start_when_the_session_has_no_record()
    {
        var registry = new AgentLaunchRegistry();
        Assert.True(registry.ShouldStart(1, _ => true));
        Assert.Null(registry.Running(1));
    }

    [Fact]
    public void No_second_start_while_the_process_runs()
    {
        var registry = new AgentLaunchRegistry();
        registry.Record(Agent);
        Assert.False(registry.ShouldStart(1, _ => true));
        Assert.Equal(Agent, registry.Running(1));
    }

    [Fact]
    public void New_start_after_the_process_ended()
    {
        var registry = new AgentLaunchRegistry();
        registry.Record(Agent);
        Assert.True(registry.ShouldStart(1, _ => false));
        Assert.Null(registry.Running(1));
        Assert.True(registry.ShouldStart(1, _ => throw new InvalidOperationException("no test after the end")));
    }

    [Fact]
    public void Process_of_the_child_with_the_agent_exe_is_not_the_agent()
    {
        var registry = new AgentLaunchRegistry();
        registry.Record(Agent);
        Assert.True(registry.IsAgent(1, Agent.Process));
        Assert.False(registry.IsAgent(1, new ProcessIdentity(200, Start)));
        Assert.False(registry.IsAgent(1, new ProcessIdentity(100, Start.AddSeconds(5))));
        Assert.False(registry.IsAgent(2, Agent.Process));
    }

    [Fact]
    public void Session_end_removes_the_record()
    {
        var registry = new AgentLaunchRegistry();
        registry.Record(Agent);
        registry.SessionEnded(1);
        Assert.Null(registry.Running(1));
        Assert.True(registry.ShouldStart(1, _ => true));
    }

    [Fact]
    public void End_of_an_old_process_does_not_end_a_newer_record()
    {
        var registry = new AgentLaunchRegistry();
        registry.Record(Agent);
        var newer = Agent with { Process = new ProcessIdentity(300, Start.AddMinutes(1)) };
        registry.Record(newer);
        registry.MarkEnded(1, Agent.Process);
        Assert.Equal(newer, registry.Running(1));
    }
}
