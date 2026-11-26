namespace ParentalControl.Domain.Breaks;

/// <summary>
/// The Break Rule of a Restricted User: after <see cref="UseMinutes"/> minutes of Continuous Use,
/// a Break of <see cref="BreakMinutes"/> minutes starts. A user without a stored rule gets <see cref="Default"/>.
/// </summary>
public sealed record BreakRule(bool Enabled, int UseMinutes, int BreakMinutes)
{
    public const int MinUseMinutes = 10;
    public const int MaxUseMinutes = 240;
    public const int MinBreakMinutes = 5;
    public const int MaxBreakMinutes = 120;

    /// <summary>The minutes of Continuous Use before a Break at which the first warning comes.</summary>
    public const int FirstWarningMinutes = 5;

    /// <summary>The minutes of Continuous Use before a Break at which the last warning comes.</summary>
    public const int LastWarningMinutes = 1;

    /// <summary>The rule of a Restricted User without a stored rule: on, 45 minutes of use, 10 minutes of break.</summary>
    public static BreakRule Default { get; } = new(true, 45, 10);

    public static bool IsValidUseMinutes(int minutes) => minutes is >= MinUseMinutes and <= MaxUseMinutes;

    public static bool IsValidBreakMinutes(int minutes) => minutes is >= MinBreakMinutes and <= MaxBreakMinutes;

    /// <summary>True if both values are in range. The range check does not depend on <see cref="Enabled"/>.</summary>
    public bool IsValid => IsValidUseMinutes(UseMinutes) && IsValidBreakMinutes(BreakMinutes);
}
