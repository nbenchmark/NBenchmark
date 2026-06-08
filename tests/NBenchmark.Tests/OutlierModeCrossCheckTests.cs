using NBenchmark.Engine;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Cross-checks for the outlier-trimming modes. The trim *counts* are pinned
///     against their documented formulas, and the quartile definition used by
///     <see cref="OutlierMode.IqrFence" /> is pinned to the nearest-rank percentile -
///     which deliberately diverges from R's default (type 7) linear interpolation.
/// </summary>
public class OutlierModeCrossCheckTests
{
    // RemoveTop5Percent keeps floor(n × 0.95) samples (i.e. removes ceil(n × 0.05)).
    [Theory]
    [InlineData(20, 19)]
    [InlineData(50, 47)]
    [InlineData(100, 95)]
    [InlineData(200, 190)]
    public async Task RemoveTop5Percent_Keeps_Floor_95Percent(int iterations, int expectedKept)
    {
        var outcome = await Measure(iterations, OutlierMode.RemoveTop5Percent);

        Assert.Equal(expectedKept, outcome.Result.MeasuredIterations);
        Assert.Equal(iterations, outcome.RawSamples.Length);
    }

    // RemoveTopAndBottom5Percent removes floor(n × 0.05) from each end.
    [Theory]
    [InlineData(20, 18)]
    [InlineData(50, 46)]
    [InlineData(100, 90)]
    [InlineData(200, 180)]
    public async Task RemoveBoth5Percent_Trims_Floor_Each_End(int iterations, int expectedKept)
    {
        var outcome = await Measure(iterations, OutlierMode.RemoveTopAndBottom5Percent);

        Assert.Equal(expectedKept, outcome.Result.MeasuredIterations);
        Assert.Equal(iterations, outcome.RawSamples.Length);
    }

    // The IqrFence quartiles come from the nearest-rank percentile. For a
    // 1..20 ramp this gives Q1 = 5, Q3 = 15 (numpy method='inverted_cdf'),
    // NOT R's default type-7 linear interpolation (which gives Q1 = 5.75,
    // Q3 = 15.25). This pins the deliberate divergence documented in
    // docs/advanced/statistics.md.
    [Fact]
    public void IqrFence_Quartiles_Use_NearestRank_Not_R_Type7()
    {
        var sorted = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);

        // Nearest-rank (numpy inverted_cdf).
        Assert.Equal(5.0, q1, 12);
        Assert.Equal(15.0, q3, 12);

        // Explicitly different from R type 7 / numpy default linear interpolation.
        Assert.NotEqual(5.75, q1, 12);
        Assert.NotEqual(15.25, q3, 12);
    }

    private static Task<MeasurementOutcome> Measure(int iterations, OutlierMode mode) =>
        BenchmarkRunner.Instance.RunAsync(
            "outlier",
            () => Task.CompletedTask,
            new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 1,
                    Iterations = iterations,
                    OutlierMode = mode,
                },
            });
}
