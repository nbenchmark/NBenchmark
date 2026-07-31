using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     What a <c>[Performance]</c> ratio gate turns on once the test asks for replicates.
/// </summary>
/// <remarks>
///     <para>
///         With one launch the gate has one quotient and a p-value over pooled samples, and that is all
///         it can have. With two or more it has the ratio measured <i>per replicate</i> and the spread
///         between those ratios - and that spread is the quantity that decides whether a build should
///         fail, because it is the one that says whether the same code would be judged the same way
///         tomorrow.
///     </para>
///     <para>
///         So when a paired estimate is present it replaces both halves of the test: the ratio compared
///         against the threshold, and the question of whether the two differ at all. The pooled p-value
///         is still reported and deliberately not gated on - pooling multiplies statistical power
///         regardless of reproducibility, and on bodies of provably identical cost that combination marks
///         one significantly slower than another routinely.
///     </para>
/// </remarks>
public sealed class PairedRatioGateTests
{
    private const double Gate = 1.20;

    /// <summary>
    ///     A slowdown past the gate whose interval excludes 1.00x fails, and the message carries the
    ///     evidence: the interval and how many replicates produced it.
    /// </summary>
    [Fact]
    public void A_Reproducible_Slowdown_Fails_And_Reports_Its_Interval()
    {
        var outcome = Evaluate(Estimate(1.45, 1.31, 1.60));

        var violation = Assert.Single(outcome.Violations);

        Assert.Contains("Regression detected", violation);
        Assert.Contains("1.45x", violation);
        Assert.Contains("1.31-1.60x", violation);
        Assert.Contains("3 paired replicates", violation);
    }

    /// <summary>
    ///     A ratio past the gate whose interval still spans 1.00x does not fail - the run cannot
    ///     distinguish the two bodies, and failing a build on that is failing on noise.
    /// </summary>
    /// <remarks>
    ///     The note is the load-bearing half. A gate that quietly declines to enforce is a gate that
    ///     passes, and the only thing between that and a missed regression is the line saying so.
    /// </remarks>
    [Fact]
    public void A_Slowdown_Whose_Interval_Spans_Unity_Is_Not_Enforced_But_Is_Reported()
    {
        var outcome = Evaluate(Estimate(1.45, 0.82, 2.55));

        Assert.Empty(outcome.Violations);

        var note = Assert.Single(outcome.Notes);

        Assert.Contains("was not enforced", note);
        Assert.Contains("spans 1.00x", note);
        Assert.Contains("0.82-2.55x", note);
        Assert.Contains("Raise LaunchCount", note);
    }

    /// <summary>A reproducible slowdown that stays inside the gate passes silently, as it should.</summary>
    [Fact]
    public void A_Slowdown_Within_The_Gate_Passes_Without_A_Note()
    {
        var outcome = Evaluate(Estimate(1.05, 1.02, 1.08));

        Assert.Empty(outcome.Violations);
        Assert.Empty(outcome.Notes);
    }

    /// <summary>
    ///     The pooled p-value does not veto a paired failure.
    /// </summary>
    /// <remarks>
    ///     Both sides are handed <b>identical</b> sample arrays, so Mann-Whitney sees no difference
    ///     whatever and would decline to flag anything. The per-replicate ratios say otherwise, and they
    ///     are the measurement that reflects how the two bodies actually compared inside each worker.
    /// </remarks>
    [Fact]
    public void The_Paired_Interval_Decides_When_The_Pooled_Test_Sees_Nothing()
    {
        var identical = Samples(100);

        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 145),
            identical,
            Result("reference", 100),
            identical,
            new Thresholds { MaxSlowdownRatio = Gate },
            pairedRatio: Estimate(1.45, 1.31, 1.60));

        Assert.Contains(outcome.Violations, v => v.Contains("Regression detected"));
    }

    /// <summary>
    ///     And it does not manufacture one either.
    /// </summary>
    /// <remarks>
    ///     This is the conflict item 2 of the follow-up plan surfaced, reached from the gate's side: the
    ///     pooled samples here differ enormously and the p-value is emphatic, while the run-to-run spread
    ///     says the two cannot be told apart. Before replicates the gate had only the p-value and would
    ///     have failed the build.
    /// </remarks>
    [Fact]
    public void The_Paired_Interval_Decides_When_The_Pooled_Test_Is_Emphatic()
    {
        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 600),
            Samples(600),
            Result("reference", 100),
            Samples(100),
            new Thresholds { MaxSlowdownRatio = Gate },
            pairedRatio: Estimate(1.45, 0.82, 2.55));

        Assert.Empty(outcome.Violations);
        Assert.Contains(outcome.Notes, n => n.Contains("spans 1.00x"));
    }

    /// <summary>
    ///     The calibration gate - the ratio a test with no reference method uses - pairs too, from the
    ///     per-launch calibration medians the worker measured beside each launch of the benchmark.
    /// </summary>
    [Fact]
    public void The_Calibration_Gate_Pairs_From_Per_Launch_Medians()
    {
        // Three launches disagreeing with each other by 5x, in which the benchmark was 3x the
        // calibration every single time. A quotient of averages would find the same 3x here, but only
        // the pairing can say the three launches agreed about it.
        var candidate = WithLaunches("candidate", [300, 1500, 600]);

        var calibration = new CalibrationResult(100, 100, Samples(100))
        {
            LaunchMedians = [100, 500, 200],
        };

        var outcome = PerformanceGate.Evaluate(
            candidate,
            Samples(300),
            null,
            null,
            new Thresholds { MaxSlowdownRatio = 2.0 },
            workerCalibration: calibration);

        var violation = Assert.Single(outcome.Violations);

        Assert.Contains("3.00x", violation);
        Assert.Contains("3 paired replicates", violation);
    }

    /// <summary>
    ///     A single-launch calibration ratio has no interval to pair, and keeps the pooled-sample
    ///     behaviour it has always had.
    /// </summary>
    [Fact]
    public void A_Single_Launch_Calibration_Gate_Is_Unchanged()
    {
        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 300),
            Samples(300),
            null,
            null,
            new Thresholds { MaxSlowdownRatio = 2.0 },
            workerCalibration: new CalibrationResult(100, 100, Samples(100)));

        var violation = Assert.Single(outcome.Violations);

        Assert.Contains("ratio 3.00x", violation);
        Assert.Contains("p=", violation);
    }

    private static PerformanceGate.Outcome Evaluate(RatioEstimate estimate)
        => PerformanceGate.Evaluate(
            Result("candidate", 145),
            Samples(145),
            Result("reference", 100),
            Samples(100),
            new Thresholds { MaxSlowdownRatio = Gate },
            pairedRatio: estimate);

    private static RatioEstimate Estimate(double value, double lower, double upper) => new()
    {
        Value = value,
        Lower = lower,
        Upper = upper,
        Replicates = 3,
        ConfidenceLevel = 0.95,
    };

    /// <summary>
    ///     A result carrying real per-launch detail, so the paired estimator has launch indices to pair
    ///     against rather than a fabricated estimate.
    /// </summary>
    private static BenchmarkResult WithLaunches(string name, double[] medians)
    {
        var launches = medians
            .Select((median, index) => new LaunchDetail
            {
                LaunchIndex = index,
                Median = median,
                Mean = median,
                StandardDeviation = 0,
                Iterations = 50,
                Duration = TimeSpan.Zero,
            })
            .ToList();

        return Result(name, medians.Average()) with
        {
            LaunchStatistics = new LaunchStatistics
            {
                LaunchCount = launches.Count,
                LaunchMean = medians.Average(),
                LaunchMedian = medians.Order().ElementAt(medians.Length / 2),
                LaunchStandardDeviation = 0,
                Launches = launches,
            },
        };
    }

    private static double[] Samples(double mean)
    {
        var samples = new double[50];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = mean + (i % 10 - 5) * 0.02 * mean;
        }

        return samples;
    }

    /// <summary>
    ///     Both sides are stamped as measured in a worker, which is what a replicated test produces. The
    ///     ratio gate is only enforced between two such measurements, so a fixture left at the in-host
    ///     default would be testing the isolation policy instead of the pairing.
    /// </summary>
    private static BenchmarkResult Result(string name, double value) => new()
    {
        Name = name,
        IsolationStatus = IsolationStatus.Isolated,
        Mean = value,
        Median = value,
        Percentiles = [],
        Min = value,
        Max = value,
        StandardDeviation = 0,
        MeasuredIterations = 50,
        WarmupIterations = 10,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 50,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
    };

    private sealed class Thresholds : IPerformanceThresholds
    {
        public double MaxMeanNs => -1;
        public double MaxP95Ns => -1;
        public long MaxAllocatedBytes => -1;
        public string? ReferenceMethod { get; init; }
        public double MaxSlowdownRatio { get; init; }
        public int Iterations => 0;
        public int WarmupIterations => 0;
        public bool MeasureAllocations => false;
        public OutlierMode OutlierMode => OutlierMode.IqrFence;
        public double ConfidenceLevel => 0.95;
        public double MaxAbsoluteThresholdTolerance => 1.0;
    }
}
