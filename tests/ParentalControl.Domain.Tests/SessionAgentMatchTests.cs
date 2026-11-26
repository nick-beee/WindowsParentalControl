using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests;

public class SessionAgentMatchTests
{
    private const string AgentPath = @"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe";

    [Theory]
    [InlineData(AgentPath, "ParentalControl.Agent")] // issue #10: not "ParentalControl"
    [InlineData(@"C:\x\PARENTALCONTROL.AGENT.EXE", "PARENTALCONTROL.AGENT")]
    [InlineData(@"C:\x\tool", "tool")]
    public void ProcessName_removes_only_the_exe_extension(string path, string expected) =>
        Assert.Equal(expected, SessionAgentMatch.ProcessName(path));

    [Fact]
    public void The_agent_in_its_session_matches() =>
        Assert.True(SessionAgentMatch.IsSessionAgent(2, AgentPath, 2, AgentPath));

    [Theory]
    [InlineData(@"c:\program files\parentalcontrol\agent\parentalcontrol.agent.exe")]
    [InlineData(@"\\?\C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe")]
    [InlineData(@"C:\Program Files\ParentalControl\service\..\agent\ParentalControl.Agent.exe")]
    public void The_path_is_compared_in_canonical_form(string imagePath) =>
        Assert.True(SessionAgentMatch.IsSessionAgent(2, imagePath, 2, AgentPath));

    [Fact]
    public void The_agent_of_another_session_does_not_match() =>
        Assert.False(SessionAgentMatch.IsSessionAgent(3, AgentPath, 2, AgentPath));

    [Theory]
    [InlineData(@"C:\Users\Kid\Downloads\ParentalControl.Agent.exe")]
    [InlineData(@"C:\Program Files\ParentalControl\agent\ParentalControl.Agent.exe.bak")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ParentalControl.Agent.exe")]
    public void A_process_with_the_agent_name_from_another_path_does_not_match(string? imagePath) =>
        Assert.False(SessionAgentMatch.IsSessionAgent(2, imagePath, 2, AgentPath));
}
