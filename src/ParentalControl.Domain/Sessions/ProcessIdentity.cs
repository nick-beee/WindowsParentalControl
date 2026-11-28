namespace ParentalControl.Domain.Sessions;

/// <summary>
/// One process at one time: the pid and the start time. Windows reuses a pid after the process ended, so the pid alone
/// does not name a process.
/// </summary>
/// <param name="Pid">The process ID.</param>
/// <param name="StartUtc">The creation time of the process (<c>GetProcessTimes</c>), in UTC.</param>
public readonly record struct ProcessIdentity(int Pid, DateTime StartUtc);
