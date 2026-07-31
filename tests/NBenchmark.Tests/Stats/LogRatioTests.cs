using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests.Stats;

/// <summary>
///     The paired, log-scale ratio estimator.
/// </summary>
/// <remarks>
///     A comparison group is measured co-resident in one worker per replicate, so pairing replicate
///     <i>i</i> of the candidate against replicate <i>i</i> of the baseline divides that worker's own
///     CPU draw and memory layout out of the ratio. These tests pin that the pairing is real - that the
///     estimator is not simply dividing two averages behind a different name.
/// </remarks>
public class LogRatioTests
{
    /// <summary>
    ///     The pairing is the whole point: a shared per-replicate factor must cancel.
    /// </summary>
    /// <remarks>
    ///     Each replicate here is a worker that ran uniformly slow or fast - 1x, 2x, 4x - while the
    ///     candidate is always exactly 1.5x the baseline within that worker. The paired estimator
    ///     returns 1.5x with a zero-width interval, because every replicate agrees. An unpaired
    ///     estimator would also land near 1.5 here but would carry the workers' 4x spread in its
    ///     interval, reporting uncertainty that the pairing had already removed.
    /// </remarks>
    [Fact]
    public void Estimate_CancelsAPerReplicateFactorSharedByBothSides()
    {
        double[] baseline = [100, 200, 400];
        double[] candidate = [150, 300, 600];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.Equal(1.5, estimate.Value, 10);
        Assert.Equal(1.5, estimate.Lower, 6);
        Assert.Equal(1.5, estimate.Upper, 6);
        Assert.False(estimate.IncludesUnity);
    }

    /// <summary>
    ///     The point estimate is the geometric mean of the per-replicate ratios, not the arithmetic
    ///     mean of them and not the ratio of the two means.
    /// </summary>
    /// <remarks>
    ///     0.5x and 2.0x are the same effect in opposite directions, so their average must be 1.0x. An
    ///     arithmetic mean gives 1.25x - a fabricated 25% slowdown out of two measurements that
    ///     cancel.
    /// </remarks>
    [Fact]
    public void Estimate_IsTheGeometricMeanOfTheRatios()
    {
        double[] baseline = [100, 100];
        double[] candidate = [50, 200];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.Equal(1.0, estimate.Value, 10);

        // The arithmetic mean of the ratios, which this deliberately is not.
        Assert.NotEqual(1.25, estimate.Value, 3);
    }

    /// <summary>
    ///     The interval is multiplicatively symmetric about the estimate, which is what taking logs
    ///     buys. A linear interval on a ratio with real spread can put the lower bound below zero, and
    ///     a negative ratio is not a thing a benchmark can produce.
    /// </summary>
    [Fact]
    public void Estimate_IntervalIsMultiplicativelySymmetricAndPositive()
    {
        double[] baseline = [100, 100, 100, 100];
        double[] candidate = [80, 150, 90, 200];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.True(estimate.Lower > 0, $"lower bound must be a ratio, got {estimate.Lower}");

        // Equal multiplicative distance either side: value/lower == upper/value.
        Assert.Equal(estimate.Value / estimate.Lower, estimate.Upper / estimate.Value, 6);
    }

    /// <summary>
    ///     Two benchmarks of identical cost produce an interval that contains 1.0 - the estimator
    ///     saying, correctly, that this run cannot tell them apart.
    /// </summary>
    [Fact]
    public void Estimate_OnEquivalentBodies_SpansUnity()
    {
        double[] baseline = [100, 102, 99, 101];
        double[] candidate = [101, 99, 102, 100];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.True(estimate.IncludesUnity, $"expected [{estimate.Lower}, {estimate.Upper}] to span 1.0");
    }

    /// <summary>
    ///     A consistent difference larger than the replicate spread produces an interval that excludes
    ///     1.0. Without this the estimator would be uninformative rather than merely conservative.
    /// </summary>
    [Fact]
    public void Estimate_OnAConsistentDifference_ExcludesUnity()
    {
        double[] baseline = [100, 102, 99, 101];
        double[] candidate = [200, 204, 198, 202];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.Equal(2.0, estimate.Value, 2);
        Assert.False(estimate.IncludesUnity);
    }

    /// <summary>
    ///     One replicate is a ratio, not an estimate of one. Returning it with no interval would be
    ///     indistinguishable from the unpaired ratio the caller already has.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Estimate_NeedsAtLeastTwoPairs(int pairs)
    {
        var baseline = Enumerable.Repeat(100.0, pairs).ToArray();
        var candidate = Enumerable.Repeat(150.0, pairs).ToArray();

        Assert.Null(LogRatio.Estimate(candidate, baseline));
    }

    /// <summary>
    ///     A replicate where either side did not measure is dropped as a <em>pair</em>, not as a single
    ///     value. Keeping the survivor would contribute a difference between two different processes,
    ///     which is exactly what the pairing exists to exclude.
    /// </summary>
    [Fact]
    public void Estimate_DropsWholePairsWhenEitherSideIsMissing()
    {
        double[] baseline = [100, 0, 100, 100];
        double[] candidate = [150, 150, 0, 150];

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.Equal(2, estimate.Replicates);
        Assert.Equal(1.5, estimate.Value, 10);
    }

    /// <summary>
    ///     A wider confidence level gives a wider interval around the same point estimate.
    /// </summary>
    [Fact]
    public void Estimate_HonoursTheConfidenceLevel()
    {
        double[] baseline = [100, 100, 100, 100];
        double[] candidate = [140, 150, 160, 155];

        var narrow = LogRatio.Estimate(candidate, baseline, 0.95)!;
        var wide = LogRatio.Estimate(candidate, baseline, 0.99)!;

        Assert.Equal(narrow.Value, wide.Value, 10);
        Assert.True(wide.Upper > narrow.Upper);
        Assert.True(wide.Lower < narrow.Lower);
        Assert.Equal(0.99, wide.ConfidenceLevel);
    }

    /// <summary>
    ///     The result overload pairs by <c>LaunchIndex</c>, not by list position.
    /// </summary>
    /// <remarks>
    ///     An errored launch is recorded in the detail list but contributes no median, so two results
    ///     can hold lists of different lengths whose <i>n</i>th entries are different replicates.
    ///     Pairing by position there compares a candidate's second worker against the baseline's third,
    ///     and reports the difference between two processes as a property of the code. Here the
    ///     baseline's launch 1 errored, so only launches 0 and 2 may be paired - and those are the two
    ///     where the ratio is exactly 1.5x.
    /// </remarks>
    [Fact]
    public void Estimate_FromResults_PairsByLaunchIndexNotPosition()
    {
        var candidate = ResultWithLaunches("cand", [(0, 150, false), (1, 999, false), (2, 300, false)]);
        var baseline = ResultWithLaunches("base", [(0, 100, false), (1, 0, true), (2, 200, false)]);

        var estimate = LogRatio.Estimate(candidate, baseline);

        Assert.NotNull(estimate);
        Assert.Equal(2, estimate.Replicates);
        Assert.Equal(1.5, estimate.Value, 10);
    }

    /// <summary>A single-launch run has no per-replicate detail to pair.</summary>
    [Fact]
    public void Estimate_FromResults_WithoutLaunchStatistics_IsNull()
    {
        var candidate = ResultWithLaunches("cand", [(0, 150, false)]) with { LaunchStatistics = null };
        var baseline = ResultWithLaunches("base", [(0, 100, false)]);

        Assert.Null(LogRatio.Estimate(candidate, baseline));
        Assert.Null(LogRatio.Estimate(baseline, candidate));
    }

    /// <summary>
    ///     The divisor overload - used where the divisor is the calibration standard rather than a
    ///     benchmark - addresses its list by launch index, not by position.
    /// </summary>
    /// <remarks>
    ///     The candidate here is missing launch 1, and the divisor list still has three entries because
    ///     it is indexed rather than compacted. Only launches 0 and 2 pair, and both agree the candidate
    ///     is 3x its divisor. A positional reading would pair the candidate's launch 2 against the
    ///     divisor's launch 1 and report 1.5x - a difference between two processes, presented as a
    ///     property of the code.
    /// </remarks>
    [Fact]
    public void Estimate_AgainstADivisorList_PairsByLaunchIndex()
    {
        var candidate = ResultWithLaunches("cand", [(0, 300, false), (1, 0, true), (2, 600, false)]);

        var estimate = LogRatio.Estimate(candidate, [100, 500, 200]);

        Assert.NotNull(estimate);
        Assert.Equal(2, estimate.Replicates);
        Assert.Equal(3.0, estimate.Value, 10);
    }

    /// <summary>
    ///     A launch for which the divisor was never measured drops the pair rather than the single
    ///     survivor, because a lone survivor would contribute a comparison against another process.
    /// </summary>
    [Fact]
    public void Estimate_AgainstADivisorList_DropsLaunchesWithNoDivisor()
    {
        var candidate = ResultWithLaunches("cand", [(0, 300, false), (1, 900, false), (2, 600, false)]);

        var estimate = LogRatio.Estimate(candidate, [100, 0, 200]);

        Assert.NotNull(estimate);
        Assert.Equal(2, estimate.Replicates);
        Assert.Equal(3.0, estimate.Value, 10);
    }

    /// <summary>
    ///     An empty divisor list is what a single-launch run produces, and one pair is not an estimate.
    /// </summary>
    [Fact]
    public void Estimate_AgainstAnEmptyDivisorList_IsNull()
    {
        var candidate = ResultWithLaunches("cand", [(0, 300, false), (1, 900, false)]);

        Assert.Null(LogRatio.Estimate(candidate, []));
        Assert.Null(LogRatio.Estimate(candidate, [100]));
    }

    private static BenchmarkResult ResultWithLaunches(
        string name,
        (int Index, double Median, bool Errored)[] launches)
        => new()
        {
            Name = name,
            Mean = launches.Average(l => l.Median),
            Median = launches.Average(l => l.Median),
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 10,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
            LaunchStatistics = new LaunchStatistics
            {
                LaunchCount = launches.Count(l => !l.Errored),
                LaunchMean = 0,
                LaunchStandardDeviation = 0,
                LaunchMedian = 0,
                Launches = launches
                    .Select(l => new LaunchDetail
                    {
                        LaunchIndex = l.Index,
                        Median = l.Median,
                        Mean = l.Median,
                        StandardDeviation = 0,
                        Iterations = 10,
                        Duration = TimeSpan.FromSeconds(1),
                        Errored = l.Errored,
                    })
                    .ToList(),
            },
        };
}
