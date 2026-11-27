namespace ParentalControl.Domain.Requests;

/// <summary>What the Request Window does after a navigation of the main document completed (design D6).</summary>
public enum NavigationOutcome
{
    KeepPage,
    ShowFallback,
}

/// <summary>
/// The fallback decision of the Request Window (request-window, design D6, Review 1 N1 and N2). It records the first
/// navigation and each navigation that the agent cancelled, at its start or at a redirect. A redirect keeps the
/// <c>NavigationId</c> of the first event. The decision never reads the <c>WebErrorStatus</c>.
/// Not thread-safe: the window calls it on its UI thread.
/// </summary>
public sealed class RequestWindowNavigations
{
    private readonly HashSet<ulong> _cancelled = new();
    private ulong? _firstId;
    private bool _firstCompleted;

    /// <summary>Records a <c>NavigationStarting</c> event of the main document, also for a redirect.</summary>
    public void Started(ulong navigationId, bool cancelledByAgent)
    {
        _firstId ??= navigationId;
        if (cancelledByAgent) _cancelled.Add(navigationId);
    }

    /// <summary>Records a <c>NavigationCompleted</c> event of the main document and gives the decision.</summary>
    public NavigationOutcome Completed(ulong navigationId, bool isSuccess)
    {
        var isFirst = !_firstCompleted && (_firstId is null || _firstId == navigationId);
        if (isFirst) _firstCompleted = true;
        var cancelled = _cancelled.Remove(navigationId);
        return Decide(isFirst, cancelled, isSuccess);
    }

    /// <summary>
    /// The pure decision: a failed first navigation shows the fallback for any reason, also if the agent cancelled its
    /// redirect. A later navigation that the agent cancelled keeps the page. A later failed navigation that the agent
    /// did not cancel shows the fallback.
    /// </summary>
    public static NavigationOutcome Decide(bool isFirst, bool cancelledByAgent, bool isSuccess)
    {
        if (isSuccess) return NavigationOutcome.KeepPage;
        if (isFirst) return NavigationOutcome.ShowFallback;
        return cancelledByAgent ? NavigationOutcome.KeepPage : NavigationOutcome.ShowFallback;
    }
}
