namespace ParentalControl.Domain.Sessions;

/// <summary>
/// The launch record of a Session Agent that the service started (session-probe-and-agent-channel, design D7 and D9):
/// the session of the start, the process and the user SID from <c>TokenUser</c> of the token that started the agent.
/// </summary>
public sealed record AgentLaunchRecord(int SessionId, ProcessIdentity Process, string UserSid);

/// <summary>
/// One launch record per session (design D9). The service starts an agent if the session has no record or if the
/// process of its record ended, and never while it runs. A process of the child with the agent name or the agent exe
/// has no record, so it never counts as the agent. The registry is thread-safe.
/// </summary>
public sealed class AgentLaunchRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (AgentLaunchRecord Record, bool Ended)> _bySession = new();

    /// <summary>
    /// True if the service must start an agent in the session: no record, or the process of the record ended.
    /// </summary>
    /// <param name="isRunning">The platform test of the record (the wait on the kept process handle).</param>
    public bool ShouldStart(int sessionId, Func<AgentLaunchRecord, bool> isRunning)
    {
        AgentLaunchRecord record;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var entry) || entry.Ended)
                return true;
            record = entry.Record;
        }
        if (isRunning(record))
            return false;
        MarkEnded(sessionId, record.Process);
        return true;
    }

    /// <summary>Keeps the record of a new start. It replaces the old record of the session.</summary>
    public void Record(AgentLaunchRecord record)
    {
        lock (_gate)
            _bySession[record.SessionId] = (record, false);
    }

    /// <summary>Marks the record as ended if it is still the record of this process.</summary>
    public void MarkEnded(int sessionId, ProcessIdentity process)
    {
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out var entry) && entry.Record.Process == process)
                _bySession[sessionId] = (entry.Record, true);
        }
    }

    /// <summary>Removes the record at the end of the session.</summary>
    public void SessionEnded(int sessionId)
    {
        lock (_gate)
            _bySession.Remove(sessionId);
    }

    /// <summary>The record of a running agent of the session, or null.</summary>
    public AgentLaunchRecord? Running(int sessionId)
    {
        lock (_gate)
            return _bySession.TryGetValue(sessionId, out var entry) && !entry.Ended ? entry.Record : null;
    }

    /// <summary>True if the process is the agent that the service started in that session and that still runs.</summary>
    public bool IsAgent(int sessionId, ProcessIdentity process) => Running(sessionId)?.Process == process;
}
