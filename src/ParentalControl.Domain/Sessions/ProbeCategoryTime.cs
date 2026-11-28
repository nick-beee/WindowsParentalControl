using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Sessions;

/// <summary>
/// The time that one Session Probe sample adds to each category (session-probe-and-agent-channel, design D5, "The
/// Agent Window Budget"). Each category of the counted processes gets the interval once. The part of the interval that
/// the <see cref="AgentWindowBudget"/> exempts comes off a category only if the Session Agent is its only counted
/// process: another counted process of the same category still adds the whole interval.
/// </summary>
public static class ProbeCategoryTime
{
    public static IReadOnlyDictionary<AppCategory, TimeSpan> PerCategory(SampleSelection selection, TimeSpan interval, TimeSpan agentExempt)
    {
        var result = new Dictionary<AppCategory, TimeSpan>();
        if (interval <= TimeSpan.Zero) return result;
        foreach (var category in selection.Categories)
        {
            var onlyAgent = selection.Counted.Where(c => c.Category == category).All(c => c.IsAgent);
            var time = onlyAgent ? interval - agentExempt : interval;
            if (time > TimeSpan.Zero) result[category] = time;
        }
        return result;
    }
}
