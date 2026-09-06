using NBenchmark.Engine;
using NBenchmark.Engine.Detectors;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     The host drift canary, driven by a scripted clock so the "readings" are a programmed ramp
///     rather than whatever the machine running the tests happened to do.
/// </summary>
public class HostDriftCanaryTests
{
    private static readonly DriftCanaryOptions Cheap = new() { Samples = 4, WorkPerSample = 64 };

    /// <summary>
    ///     One reading consumes exactly <c>Samples</c> timed reads, so a clock scripted by call
    ///     index can hand each reading its own value.
    /// </summary>
    private static ScriptedClock Ramp(params double[] perReadingNs)
        => new(call => perReadingNs[Math.Min(call / Cheap.Samples, perReadingNs.Length - 1)]);

    [Fact]
    public void Create_Returns_Null_When_Disabled()
        => Assert.Null(HostDriftCanary.Create(DriftCanaryOptions.Disabled, Ramp(100)));

    [Fact]
    public void Create_Returns_Null_When_Options_Are_Null()
        => Assert.Null(HostDriftCanary.Create(null, Ramp(100)));

    [Fact]
    public void StampFor_Brackets_A_Benchmark_With_The_Readings_Either_Side()
    {
        var canary = HostDriftCanary.Create(Cheap, Ramp(100, 110, 130))!;

        canary.Take();
        canary.Take();
        canary.Take();

        var first = canary.StampFor(0)!;
        var second = canary.StampFor(1)!;

        Assert.Equal(100, first.BeforeNs);
        Assert.Equal(110, first.AfterNs);
        Assert.Equal(110, second.BeforeNs);
        Assert.Equal(130, second.AfterNs);
    }

    /// <summary>
    ///     The relative figure is the bracketing mean over the run's first reading, which is the
    ///     only comparison between two readings that means anything - the absolute nanoseconds are
    ///     an arbitrary amount of work on an arbitrary machine.
    /// </summary>
    [Fact]
    public void StampFor_Normalizes_Against_The_Runs_First_Reading()
    {
        var canary = HostDriftCanary.Create(Cheap, Ramp(100, 100, 140))!;

        canary.Take();
        canary.Take();
        canary.Take();

        Assert.Equal(1.0, canary.StampFor(0)!.RelativeToRunStart, 9);
        Assert.Equal(1.2, canary.StampFor(1)!.RelativeToRunStart, 9);
    }

    [Fact]
    public void StampFor_Records_The_Position_In_The_Run()
    {
        var canary = HostDriftCanary.Create(Cheap, Ramp(100, 100, 100))!;

        canary.Take();
        canary.Take();
        canary.Take();

        Assert.Equal(0, canary.StampFor(0)!.CompletedBenchmarks);
        Assert.Equal(1, canary.StampFor(1)!.CompletedBenchmarks);
    }

    /// <summary>
    ///     A benchmark whose trailing reading has not been taken yet has no bracket, so it has no
    ///     stamp. Asked for out of range, the canary says so rather than inventing one end.
    /// </summary>
    [Fact]
    public void StampFor_Returns_Null_Without_Both_Bracketing_Readings()
    {
        var canary = HostDriftCanary.Create(Cheap, Ramp(100, 100))!;

        canary.Take();

        Assert.Null(canary.StampFor(0));
        Assert.Null(canary.StampFor(-1));

        canary.Take();

        Assert.NotNull(canary.StampFor(0));
        Assert.Null(canary.StampFor(1));
    }

    /// <summary>
    ///     A probe with too few samples to have a median produces no reading, and a stamp that
    ///     depended on it is withheld rather than rendered as data that means nothing. The
    ///     surviving readings keep their indices, so the benchmarks either side are unaffected.
    /// </summary>
    [Fact]
    public void StampFor_Returns_Null_When_A_Bracketing_Reading_Was_Unusable()
    {
        // Sample counts below the floor make JitterCalibrator refuse the probe outright, which is
        // the unavailable path a host without a usable reading takes.
        var unusable = new DriftCanaryOptions { Samples = DriftCanaryOptions.MinSamples, WorkPerSample = 64 };
        var canary = HostDriftCanary.Create(unusable, new ScriptedClock(call => call < 4 ? 100 : double.NaN))!;

        canary.Take();
        canary.Take();

        Assert.Null(canary.StampFor(0));
    }

    [Fact]
    public void ReadingCount_Tracks_The_Readings_Taken()
    {
        var canary = HostDriftCanary.Create(Cheap, Ramp(100))!;

        Assert.Equal(0, canary.ReadingCount);

        canary.Take();
        canary.Take();

        Assert.Equal(2, canary.ReadingCount);
    }

    /// <summary>
    ///     The canary's workload is the jitter probe's, read for its centre instead of its spread.
    ///     The probe therefore has to return both, and the median has to be the median of what it
    ///     timed - not a value derived from the MAD.
    /// </summary>
    [Fact]
    public void JitterCalibrator_Run_Returns_Both_The_Median_And_The_Jitter_Metric()
    {
        var probe = JitterCalibrator.Run(4, 64, new ScriptedClock(call => call switch
        {
            0 => 100,
            1 => 100,
            2 => 300,
            _ => 300,
        }));

        Assert.Equal(200, probe.MedianNs);
        Assert.True(probe.HasMedian);
        Assert.Equal(0.5, probe.JitterMetric!.Value, 9);
    }

    [Fact]
    public void JitterCalibrator_Run_Reports_No_Median_When_The_Probe_Is_Unusable()
    {
        var probe = JitterCalibrator.Run(1, 64, new ScriptedClock(100));

        Assert.False(probe.HasMedian);
        Assert.Null(probe.JitterMetric);
    }
}

/// <summary>
///     The drift comparison: whether the machine moved more between two benchmarks' measurement
///     points than the difference being reported between them.
/// </summary>
public class HostDriftTests
{
    [Fact]
    public void Between_Is_The_Gap_As_A_Fraction_Of_The_Faster_Point()
    {
        var slow = new HostTimeline { RelativeToRunStart = 1.10 };
        var fast = new HostTimeline { RelativeToRunStart = 1.00 };

        Assert.Equal(0.10, HostDrift.Between(slow, fast)!.Value, 9);
        Assert.Equal(0.10, HostDrift.Between(fast, slow)!.Value, 9);
    }

    [Fact]
    public void Between_Is_Null_Without_Both_Stamps()
    {
        var stamp = new HostTimeline { RelativeToRunStart = 1.0 };

        Assert.Null(HostDrift.Between(stamp, null));
        Assert.Null(HostDrift.Between(null, stamp));
        Assert.Null(HostDrift.Between(null, null));
    }

    [Fact]
    public void Describe_Warns_When_The_Drift_Exceeds_The_Reported_Difference()
    {
        var warning = HostDrift.Describe(
            Row("candidate", 1.08),
            Row("baseline", 1.00),
            relativeShift: 0.03,
            minimumReportableDrift: 0.01);

        Assert.NotNull(warning);
        Assert.Contains("host drift", warning);
        Assert.Contains("'candidate'", warning);
        Assert.Contains("'baseline'", warning);
        Assert.Contains("slower", warning);
    }

    /// <summary>
    ///     Direction matters to the reader: drift that flatters a row and drift that penalises it
    ///     lead to opposite conclusions about which way the true difference lies.
    /// </summary>
    [Fact]
    public void Describe_Names_The_Direction_The_Host_Moved()
    {
        var warning = HostDrift.Describe(
            Row("candidate", 1.00),
            Row("baseline", 1.08),
            relativeShift: 0.03,
            minimumReportableDrift: 0.01);

        Assert.Contains("faster", warning);
    }

    [Fact]
    public void Describe_Is_Silent_When_The_Difference_Is_Larger_Than_The_Drift()
        => Assert.Null(HostDrift.Describe(
            Row("candidate", 1.02),
            Row("baseline", 1.00),
            relativeShift: 0.50,
            minimumReportableDrift: 0.01));

    /// <summary>
    ///     Below the floor the canary is measuring its own noise. Without this the warning would
    ///     fire on every quiet run whose two benchmarks happened to be a fraction of a percent
    ///     apart, which is most sub-percent comparisons.
    /// </summary>
    [Fact]
    public void Describe_Is_Silent_Below_The_Minimum_Reportable_Drift()
        => Assert.Null(HostDrift.Describe(
            Row("candidate", 1.004),
            Row("baseline", 1.000),
            relativeShift: 0.001,
            minimumReportableDrift: 0.01));

    [Fact]
    public void Describe_Is_Silent_When_Either_Row_Has_No_Stamp()
        => Assert.Null(HostDrift.Describe(
            Row("candidate", null),
            Row("baseline", 1.00),
            relativeShift: 0.001,
            minimumReportableDrift: 0.01));

    /// <summary>
    ///     The warning composes with the significance gates rather than replacing them: it is
    ///     appended, the verdict is untouched, and it reaches the row through the same
    ///     <see cref="BenchmarkResult.Warnings" /> list every reporter's footer reads. The canary
    ///     measures the host, not the comparison, so it never downgrades.
    /// </summary>
    [Fact]
    public void ComputeSignificance_Appends_The_Drift_Warning_Without_Downgrading_The_Verdict()
    {
        var rng = new Random(7);
        var baselineSamples = Enumerable.Range(0, 200).Select(_ => 100.0 + rng.NextDouble() * 0.2).ToArray();
        var candidateSamples = Enumerable.Range(0, 200).Select(_ => 103.0 + rng.NextDouble() * 0.2).ToArray();

        var results = new List<BenchmarkResult>
        {
            Comparable("baseline", 100, isBaseline: true, relative: 1.00),

            // Measured after the host had slowed by 8%, so the 3% it is reported slower by is
            // entirely inside the drift.
            Comparable("candidate", 103, isBaseline: false, relative: 1.08),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        Significance.ComputeSignificance(
            results, rawSamples,
            significanceLevel: 0.05,
            minimumPracticalEffect: 0,
            minimumRelativeShift: 0,
            minimumReportableDrift: 0.01);

        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
        Assert.Contains(results[1].Warnings, w => w.Contains("host drift", StringComparison.Ordinal));
        Assert.Empty(results[0].Warnings);
    }

    [Fact]
    public void ComputeSignificance_Adds_No_Drift_Warning_When_The_Host_Held_Steady()
    {
        var rng = new Random(7);
        var results = new List<BenchmarkResult>
        {
            Comparable("baseline", 100, isBaseline: true, relative: 1.00),
            Comparable("candidate", 103, isBaseline: false, relative: 1.001),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 200).Select(_ => 100.0 + rng.NextDouble() * 0.2).ToArray(),
            ["candidate"] = Enumerable.Range(0, 200).Select(_ => 103.0 + rng.NextDouble() * 0.2).ToArray(),
        };

        Significance.ComputeSignificance(
            results, rawSamples,
            significanceLevel: 0.05,
            minimumPracticalEffect: 0,
            minimumRelativeShift: 0,
            minimumReportableDrift: 0.01);

        Assert.DoesNotContain(results[1].Warnings, w => w.Contains("host drift", StringComparison.Ordinal));
    }

    private static BenchmarkResult Row(string name, double? relative)
        => Comparable(name, 100, isBaseline: false, relative);

    private static BenchmarkResult Comparable(string name, double median, bool isBaseline, double? relative) => new()
    {
        Name = name,
        MeanNs = median,
        MedianNs = median,
        Percentiles = [],
        MinNs = median,
        MaxNs = median,
        StandardDeviationNs = 0,
        IsBaseline = isBaseline,
        Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
        Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
        HostTimeline = relative is { } r
            ? new HostTimeline
            {
                BeforeNs = 100 * r,
                AfterNs = 100 * r,
                RelativeToRunStart = r,
            }
            : null,
    };
}
