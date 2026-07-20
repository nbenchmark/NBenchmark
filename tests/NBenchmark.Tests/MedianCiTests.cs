using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Cross-checks for the distribution-free median confidence interval. Reference ranks and
///     bounds were produced independently with SciPy 1.18.0 / NumPy 2.5.1: the exact branch
///     from <c>scipy.stats.binom.cdf</c> (largest rank whose lower-tail mass ≤ α/2), the normal
///     branch from <c>l = ⌊(n − z√n)/2⌋</c>, <c>u = ⌈1 + (n + z√n)/2⌉</c> with
///     <c>z = norm.ppf((1+CL)/2)</c>.
/// </summary>
public class MedianCiTests
{
    // 1..10, n=10, CL=0.95 -> ranks (2, 9) -> values (2, 9).
    [Fact]
    public void Exact_Small_Sample_Matches_Binomial()
    {
        var sorted = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();

        var ci = MedianCi.Compute(sorted, 0.95);

        Assert.NotNull(ci);
        Assert.Equal(2.0, ci!.Value.Lower, 12);
        Assert.Equal(9.0, ci.Value.Upper, 12);
    }

    // Primes, n=13, CL=0.95 -> ranks (3, 11) -> values (5, 31).
    [Fact]
    public void Exact_Odd_N_Matches_Binomial()
    {
        double[] sorted = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41];

        var ci = MedianCi.Compute(sorted, 0.95);

        Assert.NotNull(ci);
        Assert.Equal(5.0, ci!.Value.Lower, 12);
        Assert.Equal(31.0, ci.Value.Upper, 12);
    }

    // 1..20, n=20 -> (6, 15) at 95%, widening to (4, 17) at 99%.
    [Fact]
    public void Exact_Widens_With_Confidence_Level()
    {
        var sorted = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        var ci95 = MedianCi.Compute(sorted, 0.95);
        var ci99 = MedianCi.Compute(sorted, 0.99);

        Assert.Equal((6.0, 15.0), (ci95!.Value.Lower, ci95.Value.Upper));
        Assert.Equal((4.0, 17.0), (ci99!.Value.Lower, ci99.Value.Upper));
    }

    // 1..100, n=100 (>= 50 -> normal approximation) -> ranks (40, 61).
    [Fact]
    public void NormalApproximation_Above_50_Matches_Reference()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var ci = MedianCi.Compute(sorted, 0.95);

        Assert.NotNull(ci);
        Assert.Equal(40.0, ci!.Value.Lower, 12);
        Assert.Equal(61.0, ci.Value.Upper, 12);
    }

    [Fact]
    public void Returns_Null_For_Fewer_Than_Two_Samples()
    {
        Assert.Null(MedianCi.Compute([], 0.95));
        Assert.Null(MedianCi.Compute([42.0], 0.95));
    }

    [Fact]
    public void Interval_Brackets_The_Median()
    {
        var sorted = Enumerable.Range(1, 40).Select(i => (double)i).ToArray();
        var median = Percentile.Compute(sorted, 0.50);

        var ci = MedianCi.Compute(sorted, 0.95);

        Assert.True(ci!.Value.Lower <= median);
        Assert.True(ci.Value.Upper >= median);
    }
}
