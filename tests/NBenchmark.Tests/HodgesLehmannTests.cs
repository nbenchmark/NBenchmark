using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Cross-checks for the Hodges-Lehmann shift estimate and its Lehmann confidence interval.
///     Reference values were produced independently with NumPy 2.5.1: the point estimate is
///     <c>median(outer(b) − outer(a))</c>; the interval spans the k-th smallest to k-th largest
///     pairwise difference with <c>k = ⌊mn/2 − z·σ_U⌋</c> and the tie-corrected Mann-Whitney
///     <c>σ_U</c> - the same construction R's <c>wilcox.test(conf.int = TRUE)</c> uses in its
///     normal-approximation branch.
/// </summary>
public class HodgesLehmannTests
{
    // A = 10..19, B = 15..24 -> shift +5, 95% CI [2, 8].
    [Fact]
    public void Estimate_ShiftedRamp_Matches_Reference()
    {
        double[] a = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
        double[] b = [15, 16, 17, 18, 19, 20, 21, 22, 23, 24];

        var shift = HodgesLehmann.Estimate(a, b, 0.95);

        Assert.NotNull(shift);
        Assert.Equal(5.0, shift!.Value.Value, 12);
        Assert.Equal(2.0, shift.Value.Lower, 12);
        Assert.Equal(8.0, shift.Value.Upper, 12);
        Assert.Equal(0.95, shift.Value.ConfidenceLevel, 12);
    }

    // A2 = 1..8, B2 = 3..12 -> shift +3, 95% CI [0, 6].
    [Fact]
    public void Estimate_Uneven_Groups_Matches_Reference()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8];
        double[] b = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        var shift = HodgesLehmann.Estimate(a, b, 0.95);

        Assert.NotNull(shift);
        Assert.Equal(3.0, shift!.Value.Value, 12);
        Assert.Equal(0.0, shift.Value.Lower, 12);
        Assert.Equal(6.0, shift.Value.Upper, 12);
    }

    [Fact]
    public void Estimate_Is_Negative_When_Candidate_Faster()
    {
        double[] baseline = [20, 21, 22, 23, 24, 25];
        double[] candidate = [10, 11, 12, 13, 14, 15];

        var shift = HodgesLehmann.Estimate(baseline, candidate, 0.95);

        Assert.NotNull(shift);
        Assert.True(shift!.Value.Value < 0);
        Assert.True(shift.Value.Upper < 0); // interval excludes zero: a real shift.
    }

    [Fact]
    public void Estimate_Returns_Null_For_Tiny_Groups()
    {
        Assert.Null(HodgesLehmann.Estimate([1.0], [2.0, 3.0], 0.95));
        Assert.Null(HodgesLehmann.Estimate([1.0, 2.0], [3.0], 0.95));
    }

    // Deterministic stride subsampling: identical inputs always give identical estimates, and
    // groups larger than the cap are reduced (no O(n^2) blow-up on auto-tuned sample counts).
    [Fact]
    public void Estimate_Is_Deterministic_Under_Subsampling()
    {
        var baseline = Enumerable.Range(0, 5000).Select(i => 100.0 + i % 7).ToArray();
        var candidate = Enumerable.Range(0, 5000).Select(i => 110.0 + i % 7).ToArray();

        var first = HodgesLehmann.Estimate(baseline, candidate, 0.95);
        var second = HodgesLehmann.Estimate(baseline, candidate, 0.95);

        Assert.NotNull(first);
        Assert.Equal(first!.Value, second!.Value);
    }
}
