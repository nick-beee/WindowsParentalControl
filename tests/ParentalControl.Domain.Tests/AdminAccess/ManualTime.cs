namespace ParentalControl.Domain.Tests.AdminAccess;

/// <summary>A clock for tests that moves only when the test moves it.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTime(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
