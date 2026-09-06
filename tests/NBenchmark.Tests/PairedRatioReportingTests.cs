using NBenchmark.Engine;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Where the paired ratio has to actually arrive: the table rows reporters render, and the
///     threshold gate that turns a ratio into an exit code.
/// </summary>
/// <remarks>
///     The estimator itself is covered by <c>LogRatioTests</c>. This file is about the wiring, because
///     a correct statistic that no consumer reads is the same as not having one - and the ratio column
///     dividing two aggregated medians is precisely the shape of "computed the good thing, then
///     displayed the other one".
/// </remarks>
public class PairedRatioReportingTests
{
    /// <summary>
    ///     The row's ratio is the paired estimate, and it carries the interval.
    /// </summary>
    /// <remarks>
    ///     The medians are deliberately arranged so the paired answer differs from the quotient of the
    ///     aggregated medians: each launch has the candidate at exactly 1.5x, but the launches
    ///     themselves span 4x, so an unpaired ratio of averages lands elsewhere. If this test ever
    ///     reads the unpaired value, the pairing has been dropped somewhere between the estimator and
    ///     the row.
    /// </remarks>
    [Fact]
    public void Row_CarriesThePairedRatioAndItsInterval()
    {
        var results = new[]
        {
            Result("Baseline", [100, 200, 400], isBaseline: true),
            Result("Candidate", [150, 300, 600]),
        };

        var table = BenchmarkTable.Build(results);
        var candidate = table.Rows.Single(r => r.Result.Name == "Candidate");

        Assert.NotNull(candidate.RatioEstimate);
        Assert.Equal(3, candidate.RatioEstimate.Replicates);
        Assert.Equal(1.5, candidate.RatioEstimate.Value, 6);

        // The row's scalar Ratio is the paired estimate, not the quotient of aggregated medians.
        Assert.Equal(candidate.RatioEstimate.Value, candidate.Ratio, 6);
        Assert.False(candidate.RatioEstimate.IncludesUnity);
    }

    /// <summary>
    ///     Bodies of equal cost whose launches disagree get an interval spanning 1.0 - the row saying
    ///     the run cannot distinguish them.
    /// </summary>
    [Fact]
    public void Row_MarksAnIndistinguishableRatio()
    {
        var results = new[]
        {
            Result("Baseline", [100, 130, 90], isBaseline: true),
            Result("Candidate", [130, 90, 100]),
        };

        var candidate = BenchmarkTable.Build(results).Rows.Single(r => r.Result.Name == "Candidate");

        Assert.NotNull(candidate.RatioEstimate);
        Assert.True(candidate.RatioEstimate.IncludesUnity);
    }

    /// <summary>The baseline row compares against nothing, so there is no interval to report.</summary>
    [Fact]
    public void Row_BaselineHasNoRatioEstimate()
    {
        var results = new[]
        {
            Result("Baseline", [100, 100, 100], isBaseline: true),
            Result("Candidate", [150, 150, 150]),
        };

        Assert.Null(BenchmarkTable.Build(results).Rows.Single(r => r.Result.Name == "Baseline").RatioEstimate);
    }

    /// <summary>
    ///     A single-launch run has nothing to pair, so the row falls back to the plain quotient of
    ///     medians and reports no interval rather than a fabricated one.
    /// </summary>
    [Fact]
    public void Row_WithoutReplicates_FallsBackToTheQuotientOfMedians()
    {
        var results = new[]
        {
            Result("Baseline", [100], isBaseline: true) with { LaunchStatistics = null, Median = 100 },
            Result("Candidate", [150]) with { LaunchStatistics = null, Median = 150 },
        };

        var candidate = BenchmarkTable.Build(results).Rows.Single(r => r.Result.Name == "Candidate");

        Assert.Null(candidate.RatioEstimate);
        Assert.Equal(1.5, candidate.Ratio, 6);
    }

    /// <summary>
    ///     A ratio across two runtime configurations stays withheld. The paired estimator does not
    ///     change that: pairing removes a worker's own draw, not the ~3.3x difference between running
    ///     with tiering off and inheriting the host's configuration.
    /// </summary>
    [Fact]
    public void Row_MixedConfigurationRatioStaysSuppressed()
    {
        var results = new[]
        {
            Result("Baseline", [100, 100, 100], isBaseline: true),
            Result("Candidate", [150, 150, 150]) with
            {
                RuntimeProfileName = RuntimeProfile.Host.Name,
                IsolationStatus = IsolationStatus.InProcessRequested,
            },
        };

        var candidate = BenchmarkTable.Build(results).Rows.Single(r => r.Result.Name == "Candidate");

        Assert.True(candidate.RatioSuppressed);
        Assert.Null(candidate.RatioEstimate);
        Assert.True(double.IsNaN(candidate.Ratio));
    }

    /// <summary>
    ///     The threshold gate applies its percentage to the paired ratio.
    /// </summary>
    /// <remarks>
    ///     This is the case that matters most, because it decides a build. Every launch has the
    ///     candidate 5% slower, well inside a 20% gate - but the launches themselves span 4x, so a gate
    ///     comparing aggregated medians is at the mercy of which worker drew which draw. Pairing makes
    ///     the comparison about the code.
    /// </remarks>
    [Fact]
    public void ThresholdGate_UsesThePairedRatio()
    {
        var results = new[]
        {
            Result("Baseline", [100, 200, 400], isBaseline: true),
            Result("Candidate", [105, 210, 420]),
        };

        var verdict = ThresholdCheck.Check(results, 20);

        Assert.False(verdict.HasRegression);
    }

    /// <summary>
    ///     A real regression still trips the gate, and the candidate carries the interval so a reader
    ///     can tell whether the failure is supported by the data.
    /// </summary>
    [Fact]
    public void ThresholdGate_StillCatchesARealRegression()
    {
        var results = new[]
        {
            Result("Baseline", [100, 200, 400], isBaseline: true),
            Result("Candidate", [200, 400, 800]),
        };

        var verdict = ThresholdCheck.Check(results, 20);

        Assert.True(verdict.HasRegression);
        Assert.Equal(["Candidate"], verdict.RegressedNames);

        var candidate = Assert.Single(verdict.RegressedCandidates);
        Assert.Equal(2.0, candidate.Ratio, 6);
        Assert.NotNull(candidate.Estimate);
        Assert.False(candidate.Estimate.IncludesUnity);
    }

    /// <summary>
    ///     The stats block states the interval, and says so plainly when it spans 1.00x. A reader
    ///     scanning a column of ranges will not mentally test each one.
    /// </summary>
    [Fact]
    public void StatsBlock_ReportsTheRatioInterval()
    {
        var results = new[]
        {
            Result("Baseline", [100, 130, 90], isBaseline: true),
            Result("Candidate", [130, 90, 100]),
        };

        var candidate = BenchmarkTable.Build(results).Rows.Single(r => r.Result.Name == "Candidate");
        var block = BenchmarkTable.RenderStatsBlock(candidate, ReportDetail.Advanced);

        Assert.Contains("Ratio:", block);
        Assert.Contains("paired across 3 launches", block);
        Assert.Contains("cannot distinguish", block);
    }

    /// <summary>
    ///     A result with per-launch detail, so the paired estimator has replicates to work with.
    /// </summary>
    private static BenchmarkResult Result(string name, double[] launchMedians, bool isBaseline = false)
        => new()
        {
            Name = name,
            Median = launchMedians.Average(),
            Mean = launchMedians.Average(),
            Min = launchMedians.Min(),
            Max = launchMedians.Max(),
            StandardDeviation = 1,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 100,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
            IsBaseline = isBaseline,
            IsolationStatus = IsolationStatus.Isolated,
            RuntimeProfileName = RuntimeProfile.SteadyState.Name,
            LaunchStatistics = new LaunchStatistics
            {
                LaunchCount = launchMedians.Length,
                LaunchMean = launchMedians.Average(),
                LaunchStandardDeviation = 0,
                LaunchMedian = launchMedians.Average(),
                Launches = launchMedians
                    .Select((median, index) => new LaunchDetail
                    {
                        LaunchIndex = index,
                        Median = median,
                        Mean = median,
                        StandardDeviation = 0,
                        Iterations = 100,
                        Duration = TimeSpan.FromSeconds(1),
                    })
                    .ToList(),
            },
        };
}
