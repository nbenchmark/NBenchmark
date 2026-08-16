using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Property / brute-force recomputation tests for the descriptive statistics.
///     Rather than pinning a handful of hand-computed arrays, these generate many
///     random samples and recompute every reported quantity from first principles
///     inside the test, asserting agreement to near machine precision. This catches
///     regressions in <see cref="StatsSummary" /> for arbitrary inputs.
/// </summary>
public class StatsRecomputationTests
{
    private const double RelTol = 1e-9;

    public static IEnumerable<object[]> Seeds()
    {
        // A spread of seeds and sample sizes so the property is exercised broadly.
        yield return [1, 2];
        yield return [2, 3];
        yield return [3, 7];
        yield return [4, 10];
        yield return [5, 31];
        yield return [6, 64];
        yield return [7, 100];
        yield return [8, 199];
        yield return [9, 200];
        yield return [10, 500];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Compute_Matches_FirstPrinciples_Recomputation(int seed, int n)
    {
        var rng = new Random(seed);
        var samples = new double[n];

        for (var i = 0; i < n; i++)
        {
            // Realistic-ish nanosecond timings: positive, right-skewed.
            samples[i] = 100.0 + Math.Abs(rng.NextDouble() * 250.0) + rng.NextDouble() * rng.NextDouble() * 1000.0;
        }

        Array.Sort(samples);

        const double confidence = 0.95;
        var stats = StatsSummary.Compute(samples);

        // Mean from first principles.
        var sum = 0.0;

        foreach (var v in samples)
        {
            sum += v;
        }

        var expectedMean = sum / n;
        Numerics.AssertRelativeClose(expectedMean, stats.Mean, RelTol);

        // Sample variance / standard deviation (Bessel's correction).
        var sumSq = 0.0;

        foreach (var v in samples)
        {
            sumSq += (v - expectedMean) * (v - expectedMean);
        }

        var expectedVariance = sumSq / (n - 1);
        var expectedStdDev = Math.Sqrt(expectedVariance);
        Numerics.AssertRelativeClose(expectedStdDev, stats.StandardDeviation, RelTol);

        // Standard error of the mean.
        var expectedSem = expectedStdDev / Math.Sqrt(n);
        Numerics.AssertRelativeClose(expectedSem, stats.StandardError, RelTol);

        // Margin of error is the documented composition t* × SEM.
        var expectedMoe = StudentT.CriticalValue(confidence, n - 1) * expectedSem;
        Numerics.AssertRelativeClose(expectedMoe, stats.MarginOfError, RelTol);

        // Coefficient of variation.
        Numerics.AssertRelativeClose(expectedStdDev / expectedMean, stats.CoefficientOfVariation, RelTol);

        // Min / Max are the sorted endpoints.
        Assert.Equal(samples[0], stats.Min, 12);
        Assert.Equal(samples[^1], stats.Max, 12);

        // The median uses the mid-average convention (mean of the two middles on even n); every
        // other percentile keeps nearest-rank. Recomputed independently.
        Assert.Equal(MidAverageMedian(samples), stats.Median, 12);
        Assert.Equal(NearestRank(samples, 0.95), stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 12);
        Assert.Equal(NearestRank(samples, 0.99), stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 12);
    }

    /// <summary>
    ///     The same property, on the trimmed path: once outlier trimming has removed samples, the
    ///     reported standard error is the Winsorized (Yuen) one, recomputed here from the definition
    ///     - clamp the trimmed tails to the nearest retained value, take the standard deviation of
    ///     that full-length sample with the <c>n - 1</c> denominator, and rescale onto the trimmed
    ///     mean by <c>sqrt(n) / h</c> on <c>h - 1</c> degrees of freedom. Every other reported
    ///     quantity stays on the trimmed set and is asserted to be untouched by the correction.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Compute_OnATrimmedSet_Matches_FirstPrinciples_Yuen(int seed, int n)
    {
        // Two samples off each end needs at least five, and h >= 2 needs at least six.
        if (n < 6)
            return;

        var rng = new Random(seed * 17 + 3);
        var sortedAll = new double[n];

        for (var i = 0; i < n; i++)
        {
            sortedAll[i] = 100.0 + rng.NextDouble() * 250.0 + rng.NextDouble() * rng.NextDouble() * 1000.0;
        }

        Array.Sort(sortedAll);

        const int trimmedLow = 2;
        const int trimmedHigh = 2;
        var kept = sortedAll[trimmedLow..(n - trimmedHigh)];

        const double confidence = 0.95;
        var stats = StatsSummary.Compute(kept, confidence, trim: new TrimContext(sortedAll, trimmedLow, trimmedHigh));

        // Winsorize from first principles: clamp, then the ordinary Bessel-corrected spread.
        var winsorized = new double[n];

        for (var i = 0; i < n; i++)
        {
            winsorized[i] = i < trimmedLow ? sortedAll[trimmedLow]
                : i >= n - trimmedHigh ? sortedAll[n - trimmedHigh - 1]
                : sortedAll[i];
        }

        var winsorizedMean = winsorized.Sum() / n;
        var winsorizedSumSq = winsorized.Sum(v => (v - winsorizedMean) * (v - winsorizedMean));
        var winsorizedStdDev = Math.Sqrt(winsorizedSumSq / (n - 1));

        var h = n - trimmedLow - trimmedHigh;
        var expectedSem = winsorizedStdDev * Math.Sqrt(n) / h;
        var expectedMoe = StudentT.CriticalValue(confidence, h - 1) * expectedSem;

        Numerics.AssertRelativeClose(expectedSem, stats.StandardError, RelTol);
        Numerics.AssertRelativeClose(expectedMoe, stats.MarginOfError, RelTol);

        // The correction moves the interval and nothing else: every central and shape statistic is
        // still the one computed on the kept samples alone.
        var untrimmed = StatsSummary.Compute(kept, confidence);

        Assert.Equal(untrimmed.Mean, stats.Mean);
        Assert.Equal(untrimmed.Median, stats.Median);
        Assert.Equal(untrimmed.StandardDeviation, stats.StandardDeviation);
        Assert.Equal(untrimmed.CoefficientOfVariation, stats.CoefficientOfVariation);
        Assert.Equal(untrimmed.Skewness, stats.Skewness);
        Assert.Equal(untrimmed.Kurtosis, stats.Kurtosis);
        Assert.Equal(untrimmed.Mad, stats.Mad);

        // And it widens: the reported interval is larger than the one that pretended the trimmed
        // samples were never collected.
        Assert.True(stats.MarginOfError > untrimmed.MarginOfError);
    }

    /// <summary>
    ///     The reduction property, at the level users see it: an untrimmed set produces a margin of
    ///     error bit-identical to the pre-Yuen formula, so a clean run's reported numbers do not
    ///     move. Asserted with exact equality on purpose - "close enough" would let a rounding-level
    ///     drift into every clean benchmark pass unnoticed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Compute_WithNothingTrimmed_Is_BitIdentical_To_ThePlainInterval(int seed, int n)
    {
        var rng = new Random(seed * 23 + 5);
        var samples = new double[n];

        for (var i = 0; i < n; i++)
        {
            samples[i] = 100.0 + rng.NextDouble() * 250.0;
        }

        Array.Sort(samples);

        var plain = StatsSummary.Compute(samples);
        var withContext = StatsSummary.Compute(samples, trim: new TrimContext(samples, 0, 0));

        Assert.Equal(plain.StandardError, withContext.StandardError);
        Assert.Equal(plain.MarginOfError, withContext.MarginOfError);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Percentile_Matches_NearestRank_Definition(int seed, int n)
    {
        var rng = new Random(seed * 31 + 1);
        var samples = new double[n];

        for (var i = 0; i < n; i++)
        {
            samples[i] = rng.NextDouble() * 10_000.0;
        }

        Array.Sort(samples);

        for (var pct = 1; pct <= 99; pct++)
        {
            var p = pct / 100.0;
            // p == 0.50 is the median: mid-average convention. Every other percentile is nearest-rank.
            var expected = pct == 50 ? MidAverageMedian(samples) : NearestRank(samples, p);
            Assert.Equal(expected, Percentile.Compute(samples, p), 12);
        }
    }

    // Independent reference implementation of the nearest-rank percentile:
    // index = ceil(p × n) − 1, clamped to [0, n − 1].
    private static double NearestRank(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;

        if (sorted.Length == 1)
            return sorted[0];

        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    // Independent reference for the median: the mean of the two middle order statistics on
    // even n, the single middle element on odd n.
    private static double MidAverageMedian(double[] sorted)
    {
        if (sorted.Length == 0)
            return 0;

        if (sorted.Length == 1)
            return sorted[0];

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
