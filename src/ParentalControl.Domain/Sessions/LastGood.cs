namespace ParentalControl.Domain.Sessions;

/// <summary>The result of <see cref="LastGood{T}.Read"/>.</summary>
/// <param name="Value">The value of the read, else the last good value, else the fallback.</param>
/// <param name="Error">The exception of a failed read, or null.</param>
public readonly record struct LastGoodResult<T>(T Value, Exception? Error)
{
    /// <summary>True if the read failed and <see cref="Value"/> is the last good value or the fallback.</summary>
    public bool Failed => Error is not null;
}

/// <summary>
/// Keeps the last good result of a read that can fail, for example a database read (session-probe-and-agent-channel,
/// design D14: the inputs of the probe locks are in memory or read with a fallback). A failed read gives the last good
/// value; a read that succeeds replaces it, also with null. One instance per value; not thread-safe.
/// </summary>
public sealed class LastGood<T>
{
    private T? _value;
    private bool _hasValue;

    /// <summary>True after one read that succeeded.</summary>
    public bool HasValue => _hasValue;

    /// <summary>Runs <paramref name="read"/>. If it throws, gives the last good value, or
    /// <paramref name="fallback"/> if no read succeeded yet. Never throws for a failed read.</summary>
    public LastGoodResult<T> Read(Func<T> read, Func<T> fallback)
    {
        try
        {
            var value = read();
            _value = value;
            _hasValue = true;
            return new LastGoodResult<T>(value, null);
        }
        catch (Exception ex)
        {
            return new LastGoodResult<T>(_hasValue ? _value! : fallback(), ex);
        }
    }
}
