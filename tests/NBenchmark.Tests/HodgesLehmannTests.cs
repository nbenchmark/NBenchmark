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

    // The Hodges-Lehmann interval is the standard companion to the Mann-Whitney U test: its
    // zero-exclusion is meant to agree with the U test's rejection. That equivalence only holds
    // when the two share the same n. Estimating the HL shift on a stride-subsample while the U
    // test runs on the full arrays breaks it: the subsample's wider interval can include zero
    // exactly when the higher-power full-n U test rejects, so a row reports a significant
    // verdict beside a confidence interval that contradicts it. Below the old 512 cap the two
    // paths already shared n; this is the >512 case that did not.
    [Fact]
    public void Estimate_And_MannWhitneyU_Agree_On_ZeroExclusion_AboveTheOldCap()
    {
        // n = 600 per group (above the former 512 subsample cap). A small shift lands in the
        // band where the full-n U test rejects but a 512-subsample HL interval still crosses
        // zero - the disagreement the shared-n fix removes.
        const int n = 600;
        const double shift = 0.025;
        var rng = new Random(7);
        var baseline = Enumerable.Range(0, n).Select(_ => 100.0 + rng.NextDouble()).ToArray();
        var candidate = Enumerable.Range(0, n).Select(_ => 100.0 + shift + rng.NextDouble()).ToArray();

        var hl = HodgesLehmann.Estimate(baseline, candidate, 0.95);
        var mw = MannWhitneyU.Test(baseline, candidate);

        Assert.NotNull(hl);
        var hlExcludesZero = hl!.Value.Lower > 0;
        var uRejects = mw.PValue < 0.05;

        // The interval excludes zero exactly when the U test rejects: the consistency property
        // that holds only while both statistics see the same samples.
        Assert.Equal(uRejects, hlExcludesZero);
    }

    // Removing the subsample makes Estimate O(n1·n2) in time and memory. A multi-thousand
    // sample run - the upper end of what auto-tune produces - must still finish promptly, or
    // the consistency fix is not usable in practice.
    [Fact]
    public void Estimate_StaysBounded_OnLargeGroups()
    {
        const int n = 3000;
        var rng = new Random(11);
        var baseline = Enumerable.Range(0, n).Select(_ => 100.0 + rng.NextDouble()).ToArray();
        var candidate = Enumerable.Range(0, n).Select(_ => 103.0 + rng.NextDouble()).ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hl = HodgesLehmann.Estimate(baseline, candidate, 0.95);
        sw.Stop();

        Assert.NotNull(hl);
        // 9 million pairwise differences plus a sort: well under the bound on any modern machine,
        // but wide enough to catch a regression to an accidental O(n^3) or unbounded allocation.
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"HL estimate on {n}x{n} took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }
}
