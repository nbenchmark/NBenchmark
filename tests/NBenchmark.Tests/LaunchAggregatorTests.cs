using NBenchmark.Engine;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class LaunchAggregatorTests
{
    [Fact]
    public void Aggregate_SingleLaunch_ReturnsExpectedStats()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "test", Mean = 100, Median = 95, StandardDeviation = 10,
                Percentiles = [], Min = 80, Max = 130, N = 100,
                Q1 = 90, Q3 = 105, InterquartileRange = 15,
                OutliersRemoved = 0, Skewness = 0.5, Kurtosis = 3, Mad = 8,
                AllocMedian = null, AllocP95 = null, AllocMax = null,
                MeasuredIterations = 100, TotalDuration = TimeSpan.FromSeconds(1),
            },
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(1, stats.LaunchCount);
        Assert.Equal(95, stats.LaunchMean);
        Assert.Equal(95, stats.LaunchMedian);
        Assert.Equal(0, stats.LaunchStandardDeviation);
        Assert.Null(stats.LaunchConfidenceIntervalLower);
        Assert.Null(stats.LaunchConfidenceIntervalUpper);
        Assert.Single(stats.Launches);
    }

    [Fact]
    public void Aggregate_MultipleLaunches_ComputesCrossLaunchStats()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 102, 8, 100),
            CreateResult("test", 110, 112, 9, 100),
            CreateResult("test", 105, 107, 7, 100),
            CreateResult("test", 95, 97, 6, 100),
            CreateResult("test", 108, 110, 10, 100),
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(5, stats.LaunchCount);
        Assert.Equal(103.6, stats.LaunchMean, 1);
        Assert.Equal(105, stats.LaunchMedian, 1);
        Assert.True(stats.LaunchStandardDeviation > 0);
        Assert.NotNull(stats.LaunchConfidenceIntervalLower);
        Assert.NotNull(stats.LaunchConfidenceIntervalUpper);
        Assert.True(stats.LaunchConfidenceIntervalLower < stats.LaunchMean);
        Assert.True(stats.LaunchConfidenceIntervalUpper > stats.LaunchMean);
        Assert.Equal(5, stats.Launches.Count);
    }

    [Fact]
    public void Aggregate_WithErroredLaunches_SkipsErrors()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 102, 8, 100),
            CreateResult("test", 0, 0, 0, 0, true),
            CreateResult("test", 110, 112, 9, 100),
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(2, stats.LaunchCount);
        Assert.Equal(105, stats.LaunchMean, 1);
        Assert.Equal(105, stats.LaunchMedian, 1);
        Assert.Equal(3, stats.Launches.Count);
        Assert.True(stats.Launches[1].Errored);
    }

    [Fact]
    public void Aggregate_AllErrored_ReturnsZeroStats()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 0, 0, 0, 0, true),
            CreateResult("test", 0, 0, 0, 0, true),
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(0, stats.LaunchCount);
        Assert.Equal(0, stats.LaunchMean);
        Assert.Equal(0, stats.LaunchMedian);
        Assert.Equal(0, stats.LaunchStandardDeviation);
        Assert.Null(stats.LaunchConfidenceIntervalLower);
        Assert.Null(stats.LaunchConfidenceIntervalUpper);
    }

    [Fact]
    public void Aggregate_TwoLaunches_ComputesCI()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 100, 5, 50),
            CreateResult("test", 120, 120, 5, 50),
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(2, stats.LaunchCount);
        Assert.Equal(110, stats.LaunchMean);
        Assert.Equal(110, stats.LaunchMedian);
        Assert.NotNull(stats.LaunchConfidenceIntervalLower);
        Assert.NotNull(stats.LaunchConfidenceIntervalUpper);

        // For 2 launches, t-value at df=1 is 12.706, so CI should be wide
        Assert.True(stats.LaunchConfidenceIntervalLower < 100);
        Assert.True(stats.LaunchConfidenceIntervalUpper > 120);
    }

    [Fact]
    public void Aggregate_ThreeLaunches_At_99Percent_Uses_StudentT_CriticalValue()
    {
        // The previous TValue implementation scaled a hardcoded 95% t-table by z/z95, which is
        // wrong for t-quantiles. At df=2, CL=0.99 it returned 5.66 vs the true 9.925 - a CI
        // ~43% too narrow. The fix delegates to StudentT.CriticalValue, which is accurate to
        // <1% even at low df. This test pins the corrected behaviour: the CI must reflect
        // t(0.99, df=2) ~= 9.925, not the scaled 5.66.
        var medians = new double[] { 100, 110, 120 };
        var results = medians.Select(m => CreateResult("test", m, m, 0, 100)).ToList();

        const double cl = 0.99;

        var stats = LaunchAggregator.Aggregate(results, cl);

        Assert.Equal(3, stats.LaunchCount);
        Assert.Equal(110.0, stats.LaunchMean, 10);

        // Sample stddev of [100, 110, 120] is 10; se = 10 / sqrt(3) ~= 5.7735.
        // t(0.99, df=2) ~= 9.925 -> margin ~= 57.32. The old code returned 5.66 -> margin ~= 32.66.
        // We assert the corrected margin is within 1e-2 of the Student-t-derived value, which
        // both documents the fix and guards against a regression to the scaled approximation.
        Assert.NotNull(stats.LaunchConfidenceIntervalLower);
        Assert.NotNull(stats.LaunchConfidenceIntervalUpper);

        var tCritical = StudentT.CriticalValue(cl, degreesOfFreedom: medians.Length - 1);
        var sampleStdDev = Math.Sqrt(medians.Sum(m => (m - stats.LaunchMean) * (m - stats.LaunchMean))
                                     / (medians.Length - 1));
        var expectedMargin = tCritical * sampleStdDev / Math.Sqrt(medians.Length);
        var expectedLower = stats.LaunchMean - expectedMargin;
        var expectedUpper = stats.LaunchMean + expectedMargin;

        Assert.Equal(expectedLower, stats.LaunchConfidenceIntervalLower!.Value, 2);
        Assert.Equal(expectedUpper, stats.LaunchConfidenceIntervalUpper!.Value, 2);

        // Sanity check: the corrected CI is materially wider than the old scaled approximation.
        Assert.True(expectedMargin > 50.0,
            $"corrected margin {expectedMargin:F2} should exceed the old 32.66 approximation");
    }

    [Fact]
    public void Aggregate_At_95Percent_Matches_StudentT_CriticalValue()
    {
        // Cross-check the 95% path against StudentT directly - the old code was accurate at
        // CL=0.95, so this guards against accidental regressions for the common case.
        var medians = new double[] { 100, 110, 120 };
        var results = medians.Select(m => CreateResult("test", m, m, 0, 100)).ToList();

        const double cl = 0.95;

        var stats = LaunchAggregator.Aggregate(results, cl);

        var tCritical = StudentT.CriticalValue(cl, degreesOfFreedom: medians.Length - 1);
        var sampleStdDev = Math.Sqrt(medians.Sum(m => (m - stats.LaunchMean) * (m - stats.LaunchMean))
                                     / (medians.Length - 1));
        var expectedMargin = tCritical * sampleStdDev / Math.Sqrt(medians.Length);

        Assert.Equal(stats.LaunchMean - expectedMargin, stats.LaunchConfidenceIntervalLower!.Value, 2);
        Assert.Equal(stats.LaunchMean + expectedMargin, stats.LaunchConfidenceIntervalUpper!.Value, 2);
    }

    /// <summary>
    ///     The reported number is the average across launches, not the best of them.
    /// </summary>
    /// <remarks>
    ///     Each launch is a fresh worker, so the differences between them are a real systematic
    ///     component rather than transient noise. Reporting the minimum selected for the luckiest
    ///     process draw, which made raising <c>LaunchCount</c> to improve the estimate produce a
    ///     <i>more</i> optimistic headline - backwards for a number a regression gate reads.
    /// </remarks>
    [Fact]
    public void Combine_AveragesTheLaunchesRatherThanTakingTheBest()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 110, 112, 9, 100)),
            Launch(CreateResult("test", 100, 102, 8, 100)),
            Launch(CreateResult("test", 120, 122, 10, 100)),
        ]);

        Assert.Equal(110, combined.Median, 6);
        Assert.Equal(112, combined.Mean, 6);

        // Not the fastest launch, which is what this replaced.
        Assert.NotEqual(100, combined.Median);
    }

    /// <summary>
    ///     Counts and durations are totals - the run really did measure that many iterations over that
    ///     much wall clock - while the extremes span everything observed.
    /// </summary>
    [Fact]
    public void Combine_SumsCountsAndSpansTheExtremes()
    {
        var launches = new[]
        {
            Launch(CreateResult("test", 100, 102, 8, 100)),
            Launch(CreateResult("test", 120, 122, 10, 200)),
        };

        var combined = LaunchAggregator.Combine(launches);

        Assert.Equal(300, combined.N);
        Assert.Equal(300, combined.MeasuredIterations);
        Assert.Equal(TimeSpan.FromSeconds(2), combined.TotalDuration);

        Assert.Equal(launches.Min(l => l.Result.Min), combined.Min);
        Assert.Equal(launches.Max(l => l.Result.Max), combined.Max);
    }

    /// <summary>
    ///     The reported interval comes from the spread <em>between</em> launches, so it describes how
    ///     well the number reproduces rather than how precisely one process measured it. This is the
    ///     failure mode the whole launch machinery exists to expose: a tight interval around a value
    ///     that does not reproduce.
    /// </summary>
    [Fact]
    public void Combine_TakesTheIntervalFromTheSpreadBetweenLaunches()
    {
        var launches = new[]
        {
            Launch(CreateResult("test", 100, 100, 0.1, 100)),
            Launch(CreateResult("test", 200, 200, 0.1, 100)),
            Launch(CreateResult("test", 300, 300, 0.1, 100)),
        };

        var combined = LaunchAggregator.Combine(launches);
        var statistics = LaunchAggregator.Aggregate(launches.Select(l => l.Result).ToList());

        Assert.Equal(statistics.LaunchStandardDeviation / Math.Sqrt(3), combined.StandardError, 6);

        // Far larger than any launch's own 0.1 ns spread would imply - which is the point.
        Assert.True(
            combined.MarginOfError > 10,
            $"expected the between-launch margin to dominate, got {combined.MarginOfError}");
    }

    /// <summary>
    ///     The combined interval is computed at the confidence level the launches were measured at,
    ///     not at a hardcoded 95%.
    /// </summary>
    /// <remarks>
    ///     Read off the results rather than passed in as a parameter. Every call site used to take the
    ///     default, so a run configured for 99% aggregated at 95% and reported the level it had not
    ///     used - the failure mode of any default that has a real value sitting next to it.
    /// </remarks>
    [Theory]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void Combine_UsesTheConfidenceLevelTheLaunchesWereMeasuredAt(double level)
    {
        var launches = new[]
        {
            Launch(CreateResult("test", 100, 100, 1, 100) with { ConfidenceLevel = level }),
            Launch(CreateResult("test", 200, 200, 1, 100) with { ConfidenceLevel = level }),
            Launch(CreateResult("test", 300, 300, 1, 100) with { ConfidenceLevel = level }),
        };

        var combined = LaunchAggregator.Combine(launches);
        var expected = StudentT.CriticalValue(level, 2) * combined.StandardError;

        Assert.Equal(level, combined.ConfidenceLevel);
        Assert.Equal(expected, combined.MarginOfError, 6);
    }

    /// <summary>Throughput follows the averaged times rather than being averaged itself.</summary>
    /// <remarks>
    ///     <c>1/x</c> is not linear, so the mean of per-launch rates is not the rate implied by the
    ///     mean of per-launch times. Averaging both independently prints an Ops/s column that
    ///     contradicts the duration beside it.
    /// </remarks>
    [Fact]
    public void Combine_DerivesThroughputFromTheAveragedTimes()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 100, 100, 1, 100)),
            Launch(CreateResult("test", 300, 300, 1, 100)),
        ]);

        Assert.Equal(200, combined.Mean, 6);
        Assert.Equal(1_000_000_000.0 / 200, combined.OperationsPerSecond, 3);
        Assert.Equal(1_000_000_000.0 / 200, combined.MedianOperationsPerSecond, 3);
    }

    /// <summary>An errored launch contributes nothing to the statistics but is still recorded.</summary>
    [Fact]
    public void Combine_ExcludesErroredLaunchesFromTheAverage()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 0, 0, 0, 0, errored: true)),
            Launch(CreateResult("test", 100, 100, 8, 100)),
            Launch(CreateResult("test", 200, 200, 8, 100)),
        ]);

        Assert.False(combined.Errored);
        Assert.Equal(150, combined.Median, 6);
        Assert.Equal(2, combined.LaunchStatistics!.LaunchCount);
        Assert.Equal(3, combined.LaunchStatistics.Launches.Count);
    }

    /// <summary>
    ///     When every launch failed there is nothing to average, and the failure has to survive rather
    ///     than being smoothed into a zero.
    /// </summary>
    [Fact]
    public void Combine_WithNoSuccessfulLaunches_ReportsTheFailure()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 0, 0, 0, 0, errored: true)),
            Launch(CreateResult("test", 0, 0, 0, 0, errored: true)),
        ]);

        Assert.True(combined.Errored);
        Assert.Equal("Test error", combined.ErrorMessage);
    }

    /// <summary>
    ///     The row carries the samples of the launch nearest the averaged median, and its trim marks -
    ///     which index into that array. Taking the marks from one launch and the samples from another
    ///     would not fail; it would mark the wrong samples, and the marks would still render.
    /// </summary>
    [Fact]
    public void Combine_CarriesTheSamplesOfTheMostRepresentativeLaunch()
    {
        double[] slowest = [300, 301, 302];
        double[] typical = [200, 201, 202];
        double[] fastest = [100, 101, 102];

        var combined = LaunchAggregator.Combine(
        [
            new LaunchAggregator.Launch(
                CreateResult("test", 300, 300, 1, 3) with { TrimmedOrdinals = [2] }, slowest),
            new LaunchAggregator.Launch(
                CreateResult("test", 200, 200, 1, 3) with { TrimmedOrdinals = [1] }, typical),
            new LaunchAggregator.Launch(
                CreateResult("test", 100, 100, 1, 3) with { TrimmedOrdinals = [0] }, fastest),
        ]);

        Assert.Equal(200, combined.Median, 6);
        Assert.Equal(typical, combined.RawSamples);
        Assert.Equal([1], combined.TrimmedOrdinals);
    }

    /// <summary>
    ///     Percentiles are averaged per percentile rather than by position, so a launch reporting a
    ///     different set cannot have its p50 averaged into another launch's p95.
    /// </summary>
    [Fact]
    public void Combine_AveragesPercentilesByPercentileNotPosition()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 100, 100, 1, 10) with
            {
                Percentiles = [new PercentileEntry(50, 100), new PercentileEntry(95, 180)],
            }),
            Launch(CreateResult("test", 200, 200, 1, 10) with
            {
                Percentiles = [new PercentileEntry(95, 220), new PercentileEntry(50, 200)],
            }),
        ]);

        Assert.Collection(
            combined.Percentiles,
            p => Assert.Equal((50d, 150d), (p.Percentile, p.Value)),
            p => Assert.Equal((95d, 200d), (p.Percentile, p.Value)));
    }

    /// <summary>A warning any launch raised is true of the run, so warnings union rather than being
    ///     taken from whichever launch happened to be representative.</summary>
    [Fact]
    public void Combine_UnionsWarningsAcrossLaunches()
    {
        var combined = LaunchAggregator.Combine(
        [
            Launch(CreateResult("test", 100, 100, 1, 10) with { Warnings = ["shared", "only-first"] }),
            Launch(CreateResult("test", 100, 100, 1, 10) with { Warnings = ["shared", "only-second"] }),
        ]);

        Assert.Contains("only-first", combined.Warnings);
        Assert.Contains("only-second", combined.Warnings);
        Assert.Single(combined.Warnings, w => w == "shared");
    }

    /// <summary>
    ///     A launch whose samples are whatever the result already carries - enough for the assertions
    ///     that are about the averaged statistics rather than about which launch's samples survive.
    /// </summary>
    private static LaunchAggregator.Launch Launch(BenchmarkResult result)
        => new(result, [.. result.RawSamples]);

    private static BenchmarkResult CreateResult(
        string name,
        double median,
        double mean,
        double stdDev,
        int iterations,
        bool errored = false)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = mean,
            Median = median,
            StandardDeviation = stdDev,
            Percentiles = [],
            Min = median * 0.8,
            Max = median * 1.3,
            N = iterations,
            Q1 = median * 0.9,
            Q3 = median * 1.1,
            InterquartileRange = median * 0.2,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 3,
            Mad = stdDev * 0.8,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
            MeasuredIterations = iterations,
            TotalDuration = TimeSpan.FromSeconds(1),
            Errored = errored,
            ErrorMessage = errored ? "Test error" : null,
        };
    }
}
