using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

/// <summary>Spec: category-limits "Count a Child Resume" (lock-after-repeated-resume, design D10).</summary>
public class ChildResumeEvidenceTests
{
    private static readonly TimeSpan Suspended = TimeSpan.FromSeconds(12.5);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(156_250); // one clock tick of 15.625 ms

    [Fact]
    public void No_CPU_growth_is_no_evidence() =>
        Assert.Equal(ResumeEvidence.NoGrowth, ChildResumeEvidence.Judge(Suspended, Suspended));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_few_clock_ticks_are_no_evidence(int ticks) =>
        Assert.Equal(ResumeEvidence.NoGrowth, ChildResumeEvidence.Judge(Suspended, Suspended + Tick * ticks));

    [Fact]
    public void Growth_of_at_least_the_minimum_is_evidence()
    {
        Assert.Equal(ResumeEvidence.Ran, ChildResumeEvidence.Judge(Suspended, Suspended + ChildResumeEvidence.MinCpuGrowth));
        Assert.Equal(ResumeEvidence.Ran, ChildResumeEvidence.Judge(Suspended, Suspended + Tick * 4));
        Assert.Equal(ResumeEvidence.Ran, ChildResumeEvidence.Judge(Suspended, Suspended + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Smaller_CPU_time_than_at_the_suspension_is_no_evidence() =>
        Assert.Equal(ResumeEvidence.NoGrowth, ChildResumeEvidence.Judge(Suspended, Suspended - TimeSpan.FromSeconds(1)));

    [Fact]
    public void Failed_read_is_no_evidence()
    {
        Assert.Equal(ResumeEvidence.ReadFailed, ChildResumeEvidence.Judge(Suspended, null));
        Assert.Equal(ResumeEvidence.ReadFailed, ChildResumeEvidence.Judge(null, null));
    }

    [Fact]
    public void Unknown_CPU_time_at_the_suspension_is_no_evidence() =>
        Assert.Equal(ResumeEvidence.NoBaseline, ChildResumeEvidence.Judge(null, TimeSpan.FromSeconds(60)));
}
