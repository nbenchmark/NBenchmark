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

        // Nearest-rank percentiles recomputed independently.
        Assert.Equal(NearestRank(samples, 0.50), stats.Median, 12);
        Assert.Equal(NearestRank(samples, 0.95), stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 12);
        Assert.Equal(NearestRank(samples, 0.99), stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 12);
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
            Assert.Equal(NearestRank(samples, p), Percentile.Compute(samples, p), 12);
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
}
