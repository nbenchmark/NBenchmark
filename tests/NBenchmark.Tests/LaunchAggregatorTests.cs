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

    /// <summary>
    ///     The ratio must divide by the within-launch standard <em>error</em>, not by the standard
    ///     deviation of individual samples. A within-process interval is <c>t * s / sqrt(n)</c>, so a
    ///     ratio built on <c>s</c> carries a spurious <c>1/sqrt(n)</c> and understates the problem by
    ///     that factor - which on a cheap body, where <c>n</c> reaches the thousands, is 50-70x.
    /// </summary>
    [Fact]
    public void Aggregate_ProcessVarianceRatio_DividesByStandardErrorNotStandardDeviation()
    {
        // Three launches whose medians spread by 10 ns, each having measured 10,000 samples with a
        // per-sample stddev of 20 ns. SE = 20 / sqrt(10000) = 0.2 ns.
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 100, 20, 10_000) with { StandardError = 0.2 },
            CreateResult("test", 110, 110, 20, 10_000) with { StandardError = 0.2 },
            CreateResult("test", 120, 120, 20, 10_000) with { StandardError = 0.2 },
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Equal(0.2, stats.WithinLaunchStandardError!.Value, precision: 6);

        // sigma_b over the three medians is 10; against SE = 0.2 that is 50.
        Assert.Equal(10.0, stats.LaunchStandardDeviation, precision: 6);
        Assert.Equal(50.0, stats.ProcessVarianceRatio!.Value, precision: 6);

        // Against the old denominator (the per-sample stddev of 20) the ratio would have been 0.5 -
        // comfortably below the threshold of 4, so the warning stayed silent on a benchmark whose
        // between-process spread was fifty times the precision it claimed.
        Assert.NotNull(LaunchAggregator.DescribeReproducibility(stats));
    }

    /// <summary>
    ///     The corrected ratio must stay quiet on an expensive body. Few samples keep the standard error
    ///     comparable to the between-launch spread, so a within-process interval is a fair guide and
    ///     there is nothing to warn about. This is what makes the metric discriminate by body cost
    ///     rather than fire on everything.
    /// </summary>
    [Fact]
    public void Aggregate_ProcessVarianceRatio_StaysQuietWhenSampleCountIsSmall()
    {
        // A 100 ms body: 30 samples, per-sample stddev 2 ns, so SE = 2 / sqrt(30) = 0.365 ns.
        const double standardError = 2.0 / 5.477225575051661;

        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100.0, 100.0, 2, 30) with { StandardError = standardError },
            CreateResult("test", 100.4, 100.4, 2, 30) with { StandardError = standardError },
            CreateResult("test", 100.8, 100.8, 2, 30) with { StandardError = standardError },
        };

        var stats = LaunchAggregator.Aggregate(results);

        // sigma_b = 0.4 against SE = 0.365 -> ~1.1: the two agree, as they should.
        Assert.True(stats.ProcessVarianceRatio < LaunchAggregator.ProcessVarianceWarningThreshold,
            $"expected a quiet ratio, got {stats.ProcessVarianceRatio}");
        Assert.Null(LaunchAggregator.DescribeReproducibility(stats));
    }

    [Fact]
    public void Aggregate_ProcessVarianceRatio_IsNullWhenNoLaunchReportedAStandardError()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 100, 20, 10_000),
            CreateResult("test", 110, 110, 20, 10_000),
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Null(stats.ProcessVarianceRatio);
        Assert.Null(stats.WithinLaunchStandardError);
        Assert.Null(LaunchAggregator.DescribeReproducibility(stats));
    }

    [Fact]
    public void Aggregate_ProcessVarianceRatio_IsNullForASingleLaunch()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 100, 20, 10_000) with { StandardError = 0.2 },
        };

        var stats = LaunchAggregator.Aggregate(results);

        Assert.Null(stats.ProcessVarianceRatio);
        Assert.Null(stats.WithinLaunchStandardError);
    }

    /// <summary>
    ///     The message must not claim the row's interval is a within-process one. Since the multi-launch
    ///     overhaul, <c>Average</c> replaces it with the between-launch half-width, so the interval
    ///     already carries this variance; what does not is the significance verdict, which pools raw
    ///     samples across launches.
    /// </summary>
    [Fact]
    public void DescribeReproducibility_AttributesTheProblemToTheSignificanceVerdict()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 100, 100, 20, 10_000) with { StandardError = 0.2 },
            CreateResult("test", 110, 110, 20, 10_000) with { StandardError = 0.2 },
            CreateResult("test", 120, 120, 20, 10_000) with { StandardError = 0.2 },
        };

        var warning = LaunchAggregator.DescribeReproducibility(LaunchAggregator.Aggregate(results));

        Assert.NotNull(warning);
        Assert.Contains("significance verdict", warning);
        Assert.Contains("between-launch interval", warning);
        Assert.DoesNotContain("describes precision within a single process", warning);
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
    ///     With more than one launch the row's median confidence interval must describe
    ///     reproducibility <i>between</i> launches - the Student-t interval over the k launch
    ///     medians, the same machinery the margin uses - not the average of each launch's own
    ///     within-launch (distribution-free) interval. Averaging the within-launch intervals
    ///     printed a narrow band around the mean that described precision inside one process while
    ///     saying nothing about run-to-run spread, beside a margin line that already described the
    ///     spread: two intervals about the same number with no label to tell them apart.
    /// </summary>
    [Fact]
    public void Combine_MedianIntervalIsBetweenLaunch_WhenMultipleLaunches()
    {
        // Three launches with medians 100, 200, 300. Each carries its own narrow within-launch
        // median CI of [median - 1, median + 1]; averaging those would yield [199, 201], a
        // within-process band that hides the 200 ns run-to-run spread entirely.
        var launches = new[]
        {
            Launch(CreateResult("test", 100, 100, 0.1, 100) with { MedianCiLower = 99, MedianCiUpper = 101 }),
            Launch(CreateResult("test", 200, 200, 0.1, 100) with { MedianCiLower = 199, MedianCiUpper = 201 }),
            Launch(CreateResult("test", 300, 300, 0.1, 100) with { MedianCiLower = 299, MedianCiUpper = 301 }),
        };

        var combined = LaunchAggregator.Combine(launches);

        // The interval is the between-launch one: median +/- the between-launch margin, not the
        // averaged within-launch [199, 201].
        Assert.NotNull(combined.MedianCiLower);
        Assert.NotNull(combined.MedianCiUpper);
        Assert.Equal(combined.Median - combined.MarginOfError, combined.MedianCiLower!.Value, 6);
        Assert.Equal(combined.Median + combined.MarginOfError, combined.MedianCiUpper!.Value, 6);

        // And it is wide - it must span the run-to-run spread, not the 2 ns within-launch band
        // the averaging would have produced.
        Assert.True(
            combined.MedianCiUpper!.Value - combined.MedianCiLower!.Value > 100,
            $"expected a between-launch interval spanning the spread, got [{combined.MedianCiLower}, {combined.MedianCiUpper}]");
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
    ///     Each launch is its own worker with its own canary origin, so the absolute readings are
    ///     not comparable between them - but <c>RelativeToRunStart</c> is normalised inside each
    ///     launch, so the mean of it is what describes the aggregate row. Taking launch 0's stamp
    ///     (which is what a bare <c>with</c> expression would do) would report one process's drift
    ///     as though it were the run's.
    /// </summary>
    [Fact]
    public void Combine_Averages_The_Host_Timeline_Across_Launches()
    {
        var launches = new[]
        {
            Launch(WithTimeline(CreateResult("a", 100, 100, 5, 10), relative: 1.00, position: 0)),
            Launch(WithTimeline(CreateResult("a", 100, 100, 5, 10), relative: 1.10, position: 2)),
        };

        var combined = LaunchAggregator.Combine(launches);

        Assert.Equal(1.05, combined.HostTimeline!.RelativeToRunStart, 9);
        Assert.Equal(1.0, combined.HostTimeline!.Position, 9);
    }

    /// <summary>
    ///     One launch whose canary reading came back unusable should cost that launch its stamp,
    ///     not cost the row its timeline.
    /// </summary>
    [Fact]
    public void Combine_Averages_Over_Only_The_Launches_That_Have_A_Timeline()
    {
        var launches = new[]
        {
            Launch(WithTimeline(CreateResult("a", 100, 100, 5, 10), relative: 1.20, position: 1)),
            Launch(CreateResult("a", 100, 100, 5, 10)),
        };

        var combined = LaunchAggregator.Combine(launches);

        Assert.Equal(1.20, combined.HostTimeline!.RelativeToRunStart, 9);
    }

    [Fact]
    public void Combine_Leaves_The_Host_Timeline_Null_When_No_Launch_Has_One()
    {
        var launches = new[]
        {
            Launch(CreateResult("a", 100, 100, 5, 10)),
            Launch(CreateResult("a", 100, 100, 5, 10)),
        };

        Assert.Null(LaunchAggregator.Combine(launches).HostTimeline);
    }

    private static BenchmarkResult WithTimeline(BenchmarkResult result, double relative, double position)
        => result with
        {
            HostTimeline = new HostTimeline
            {
                BeforeNs = 100 * relative,
                AfterNs = 100 * relative,
                RelativeToRunStart = relative,
                Position = position,
            },
        };

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
