using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Sessions;

/// <summary>
/// A program that is never suspended still has a limit (session-probe-and-agent-channel, design D5, S1, N16). Each
/// counted exe that <see cref="SuspensionPlanner"/> never suspends and that is not in System gets at most
/// <see cref="AllowedSeconds"/> as a counted process within <see cref="WindowSeconds"/> of unlocked time while its
/// category is exhausted. Above that, the service applies the fallback of a process that cannot be suspended (the
/// Enforced Workstation Lock, a probe lock that follows the shadow mode).
/// <para>
/// The window is unlocked time: the caller passes an unlocked clock that does not move while the session is locked or
/// disconnected or the PC sleeps. Time before the exhaustion, locked time, and time that the Agent Window Budget
/// exempts do not count. A trusted WebView2 counts as its creator (<see cref="CountedProcesses"/>), so it counts here
/// only if its creator is never suspended. The agent counts nothing during an active prompt (S10). One instance per
/// Restricted User; not thread-safe.
/// </para>
/// </summary>
public sealed class NeverSuspendedAllowance
{
    public const int AllowedSeconds = 60;
    public const int WindowSeconds = 600;

    private readonly Func<string, bool> _neverSuspended;
    private readonly Dictionary<string, Queue<(TimeSpan At, TimeSpan Time)>> _byExe = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="neverSuspended">True for an exe that the suspension never acts on
    /// (<see cref="ProtectedPrograms.IsProtected"/>).</param>
    public NeverSuspendedAllowance(Func<string, bool> neverSuspended) => _neverSuspended = neverSuspended;

    /// <summary>
    /// Adds the interval of one sample and returns each exe that is over the allowance now.
    /// </summary>
    /// <param name="unlockedNow">The unlocked clock of the user at the sample.</param>
    /// <param name="unlocked">The session is unlocked and connected. Else the sample adds nothing.</param>
    /// <param name="selection">The counted processes of the sample.</param>
    /// <param name="interval">The time that the sample counts (the gap rule).</param>
    /// <param name="isExhausted">True if the category has no time left today.</param>
    /// <param name="agentExempt">The part of the interval that the Agent Window Budget exempts for the agent.</param>
    public IReadOnlyList<string> Sample(
        TimeSpan unlockedNow,
        bool unlocked,
        SampleSelection selection,
        TimeSpan interval,
        Func<AppCategory, bool> isExhausted,
        TimeSpan agentExempt = default)
    {
        if (unlocked && interval > TimeSpan.Zero)
        {
            foreach (var process in selection.Counted)
            {
                if (process.Category == AppCategory.System || !isExhausted(process.Category)) continue;
                if (!process.IsAgent && !_neverSuspended(process.ExePath)) continue;
                var time = process.IsAgent ? interval - agentExempt : interval;
                if (time <= TimeSpan.Zero) continue;
                if (!_byExe.TryGetValue(process.ExePath, out var queue))
                    _byExe[process.ExePath] = queue = new Queue<(TimeSpan, TimeSpan)>();
                queue.Enqueue((unlockedNow, time));
            }
        }

        var over = new List<string>();
        foreach (var (exe, queue) in _byExe.ToList())
        {
            while (queue.Count > 0 && unlockedNow - queue.Peek().At >= TimeSpan.FromSeconds(WindowSeconds))
                queue.Dequeue();
            if (queue.Count == 0)
            {
                _byExe.Remove(exe);
                continue;
            }
            if (queue.Sum(e => e.Time.TotalSeconds) > AllowedSeconds) over.Add(exe);
        }
        return over;
    }
}
