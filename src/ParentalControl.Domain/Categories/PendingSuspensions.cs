namespace ParentalControl.Domain.Categories;

/// <summary>The result of one step: the actions to suspend now, the actions that still wait, and the ones of them that started to wait in this step.</summary>
public sealed record PendingStep(IReadOnlyList<SuspendAction> Ready, IReadOnlyList<SuspendAction> Pending, IReadOnlyList<SuspendAction> Started);

/// <summary>
/// The Pending Suspensions of the service. A process that becomes due for suspension first waits <see cref="Delay"/>.
/// In this time the Session Agent minimizes its windows while it still runs. Then the service suspends it, also if the
/// agent did not answer. A process that is not due any more (its session is held, its exe has time left again, or it
/// ended) is dropped without a suspension. The pid and the start time identify a process.
/// The class is not thread-safe: the caller holds a lock.
/// </summary>
public sealed class PendingSuspensions
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    private readonly Dictionary<(int Pid, DateTime StartUtc), DateTime> _since = new();
    private IReadOnlyList<SuspendAction> _pending = [];

    /// <summary>The processes that wait for their suspension, from the last step.</summary>
    public IReadOnlyList<SuspendAction> Pending => _pending;

    /// <summary>The time when the next pending process is due, or null.</summary>
    public DateTime? NextDueUtc => _since.Count == 0 ? null : _since.Values.Min() + Delay;

    /// <summary>
    /// Takes the processes that the planner wants to suspend now. A new one starts to wait. One that waited at least
    /// <see cref="Delay"/> is ready and leaves the list. Each waiting process that is not in <paramref name="due"/> is dropped.
    /// </summary>
    public PendingStep Step(IReadOnlyList<SuspendAction> due, DateTime nowUtc)
    {
        var ready = new List<SuspendAction>();
        var pending = new List<SuspendAction>();
        var started = new List<SuspendAction>();
        var keys = new HashSet<(int, DateTime)>();

        foreach (var action in due)
        {
            var key = (action.Process.Pid, action.Process.StartTimeUtc);
            if (!keys.Add(key))
                continue;

            if (!_since.TryGetValue(key, out var since))
            {
                _since[key] = nowUtc;
                pending.Add(action);
                started.Add(action);
            }
            else if (nowUtc - since >= Delay)
            {
                _since.Remove(key);
                ready.Add(action);
            }
            else
            {
                pending.Add(action);
            }
        }

        foreach (var key in _since.Keys.Where(k => !keys.Contains(k)).ToList())
            _since.Remove(key);

        _pending = pending;
        return new PendingStep(ready, pending, started);
    }

    /// <summary>Forgets all pending processes, for example when the service stops.</summary>
    public void Clear()
    {
        _since.Clear();
        _pending = [];
    }
}
