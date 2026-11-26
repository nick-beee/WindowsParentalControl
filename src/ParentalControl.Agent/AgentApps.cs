namespace ParentalControl.Agent;

/// <summary>A process of the child from the status response: a Suspended App or a Pending Suspension.</summary>
public sealed record AgentAppDto(int pid, DateTime startTimeUtc);
