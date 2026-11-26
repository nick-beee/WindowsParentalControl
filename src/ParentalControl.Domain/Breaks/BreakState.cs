namespace ParentalControl.Domain.Breaks;

/// <summary>
/// The stored break state of a Restricted User (table <c>break_state</c>). All times are UTC.
/// </summary>
/// <param name="ContinuousUseMinutes">The whole minutes of use since the last Rest or Break.</param>
/// <param name="LastUseUtc">The time of the last counted minute, of the start of a Rest, or of the end of a Rest.</param>
/// <param name="BreakStartedUtc">The start of the stored Break, or null.</param>
/// <param name="BreakEndsUtc">The end of the stored Break, or null. A Break runs while the time is before this value.</param>
/// <param name="Warned5">The 5-minute warning of this cycle went out.</param>
/// <param name="Warned1">The 1-minute warning of this cycle went out.</param>
public sealed record BreakState(
    int ContinuousUseMinutes,
    DateTime? LastUseUtc,
    DateTime? BreakStartedUtc,
    DateTime? BreakEndsUtc,
    bool Warned5,
    bool Warned1)
{
    public static BreakState Empty { get; } = new(0, null, null, null, false, false);

    /// <summary>True if a Break is stored, also if its end time is already past.</summary>
    public bool HasBreak => BreakEndsUtc is not null;

    /// <summary>True if a Break is stored and its end time is after <paramref name="nowUtc"/>.</summary>
    public bool IsBreakRunning(DateTime nowUtc) => BreakEndsUtc is { } end && nowUtc < end;

    /// <summary>The state after a Rest or a Break: no use, no Break, no warning. The time of the last use stays.</summary>
    public BreakState ResetCycle() => this with
    {
        ContinuousUseMinutes = 0,
        BreakStartedUtc = null,
        BreakEndsUtc = null,
        Warned5 = false,
        Warned1 = false,
    };
}
