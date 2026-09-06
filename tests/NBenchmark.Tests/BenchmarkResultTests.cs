using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkResultTests
{
    [Fact]
    public void ConfidenceInterval_Properties_Are_Computed()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            MeanNs = 100.0,
            MedianNs = 100.0,
            Percentiles = [],
            MinNs = 80.0,
            MaxNs = 130.0,
            StandardDeviationNs = 5.0,
            MarginOfErrorNs = 2.5,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        Assert.Equal(97.5, result.ConfidenceIntervalLowerNs);
        Assert.Equal(102.5, result.ConfidenceIntervalUpperNs);
    }

    /// <summary>
    ///     A result nobody stamped must not claim isolation.
    /// </summary>
    /// <remarks>
    ///     This is a guard on a property initializer rather than on behaviour, because the initializer
    ///     is the entire invariant: <see cref="IsolationStatus.Isolated" /> is <c>0</c>, so
    ///     <c>default(IsolationStatus)</c> is the <i>permissive</i> value and any construction path
    ///     that bypassed the initializer would claim a fidelity it never had. The enum cannot be
    ///     renumbered to remove the hazard - its values travel on the wire inside
    ///     <see cref="BenchmarkResult" /> - so the initializer stays, and this test fails if it is
    ///     dropped.
    /// </remarks>
    [Fact]
    public void IsolationStatus_DefaultsToHostMeasured()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            MeanNs = 0,
            MedianNs = 0,
            Percentiles = [],
            MinNs = 0,
            MaxNs = 0,
            StandardDeviationNs = 0,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        Assert.Equal(IsolationStatus.InProcessRequested, result.IsolationStatus);
        Assert.False(result.IsolationStatus.IsIsolated());

        // The hazard being guarded against, stated so a future reader does not "tidy" the initializer
        // away on the grounds that the default looks harmless.
        Assert.Equal(IsolationStatus.Isolated, default);
    }

    [Fact]
    public void Default_OutlierMode_Is_IqrFence()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            MeanNs = 0,
            MedianNs = 0,
            Percentiles = [],
            MinNs = 0,
            MaxNs = 0,
            StandardDeviationNs = 0,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        Assert.Equal(OutlierMode.IqrFence, result.OutlierMode);
    }

    [Fact]
    public void LaunchStatistics_Default_IsNull()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            MeanNs = 0,
            MedianNs = 0,
            Percentiles = [],
            MinNs = 0,
            MaxNs = 0,
            StandardDeviationNs = 0,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        Assert.Null(result.LaunchStatistics);
    }

    [Fact]
    public void LaunchStatistics_CanBeSet()
    {
        var stats = new LaunchStatistics
        {
            LaunchCount = 3,
            LaunchMean = 105.0,
            LaunchStandardDeviation = 8.0,
            LaunchMedian = 103.0,
            LaunchConfidenceIntervalLower = 90.0,
            LaunchConfidenceIntervalUpper = 120.0,
            Launches =
            [
                new LaunchDetail { LaunchIndex = 0, MedianNs = 100, MeanNs = 102, StandardDeviationNs = 8, Samples = 50, Duration = TimeSpan.FromSeconds(1) },
                new LaunchDetail { LaunchIndex = 1, MedianNs = 110, MeanNs = 112, StandardDeviationNs = 9, Samples = 50, Duration = TimeSpan.FromSeconds(1) },
                new LaunchDetail { LaunchIndex = 2, MedianNs = 103, MeanNs = 105, StandardDeviationNs = 7, Samples = 50, Duration = TimeSpan.FromSeconds(1) },
            ],
        };

        var result = new BenchmarkResult
        {
            Name = "test",
            MeanNs = 102,
            MedianNs = 100,
            Percentiles = [],
            MinNs = 80,
            MaxNs = 130,
            StandardDeviationNs = 8,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
            LaunchStatistics = stats,
        };

        Assert.NotNull(result.LaunchStatistics);
        Assert.Equal(3, result.LaunchStatistics.LaunchCount);
        Assert.Equal(105.0, result.LaunchStatistics.LaunchMean);
        Assert.Equal(3, result.LaunchStatistics.Launches.Count);
    }

    [Fact]
    public void FromCalibration_Computes_Stats_From_Samples_Instead_Of_Zeros()
    {
        // FromCalibration previously reported fabricated zeros for StdDev/Q1Ns/Q3Ns/IQR/Skewness/
        // Kurtosis/MedianAbsoluteDeviationNs while leaving MinNs/MaxNs as the only honest sample-derived values. The fix
        // routes those through StatsSummary.Compute + Percentile.Compute so the result mirrors
        // a real benchmark's shape - the test-integration comparison path needs honest numbers,
        // and a "calibration" row that looks statistically perfect (StdDev = 0, MedianAbsoluteDeviationNs = 0) is
        // actively misleading.
        var samples = new double[] { 90, 95, 100, 105, 110, 115, 120 };

        var result = BenchmarkResult.FromCalibration("calibration", mean: 105.0, median: 105.0, samples: samples);

        Assert.Equal("calibration", result.Name);
        Assert.Equal(105.0, result.MeanNs);
        Assert.Equal(105.0, result.MedianNs);
        Assert.Equal(samples.Length, result.SampleCount);
        Assert.Equal(samples.Length, result.SampleCount);
        Assert.Equal(0, result.OutliersRemoved);

        // MinNs/MaxNs come from StatsSummary.Compute (sorted samples), not the unsorted input.
        Assert.Equal(90.0, result.MinNs);
        Assert.Equal(120.0, result.MaxNs);

        // Standard deviation of [90,95,100,105,110,115,120] (sample stddev, n-1): ~10.801.
        Assert.True(result.StandardDeviationNs > 0,
            $"StdDev {result.StandardDeviationNs} should be the sample stddev, not the previous zero");

        // Quartiles use the same nearest-rank Percentile.Compute the stats pipeline uses for
        // raw samples. For n=7: Q1Ns rank = ceil(0.25*7) - 1 = 1 -> samples[1] = 95; Q3Ns rank =
        // ceil(0.75*7) - 1 = 5 -> samples[5] = 115.
        Assert.Equal(95.0, result.Q1Ns);
        Assert.Equal(115.0, result.Q3Ns);
        Assert.Equal(20.0, result.InterquartileRangeNs);

        // MAD, skewness, and kurtosis all come from StatsSummary.Compute and must be non-zero on
        // a non-degenerate sample. Skewness of a symmetric set is ~0 (floating-point arithmetic
        // makes the computed value a small non-zero number, so we check |skew| is near zero
        // rather than exactly zero); excess kurtosis of a uniform-ish set is negative.
        Assert.True(result.MedianAbsoluteDeviationNs > 0, $"MedianAbsoluteDeviationNs {result.MedianAbsoluteDeviationNs} should be non-zero for a spread sample set");
        Assert.True(Math.Abs(result.Skewness) < 1e-9,
            $"Skewness {result.Skewness} of a symmetric set should be ~0");
        Assert.True(result.Kurtosis < 0.0,
            $"Excess kurtosis {result.Kurtosis} of a uniform-ish set should be negative (normal is 0, uniform is -1.2)");

        // StandardErrorNs, MarginOfErrorNs, and ConfidenceLevel also come from StatsSummary.Compute.
        // Before this fix they silently kept the record's own defaults (0.0 / 0.0 / 0.95), which
        // pairs a real, non-zero StandardDeviationNs with a MarginOfErrorNs of exactly 0 - an
        // impossibly tight "confidence interval" for a spread sample set. Cross-check against the
        // same formula StatsSummary itself uses (t-critical x SEM) rather than hardcoding an
        // independently-derived literal.
        var expectedSem = result.StandardDeviationNs / Math.Sqrt(samples.Length);
        var expectedT = StudentT.CriticalValue(0.95, samples.Length - 1);

        Assert.Equal(0.95, result.ConfidenceLevel);
        Assert.Equal(expectedSem, result.StandardErrorNs, 9);
        Assert.Equal(expectedT * expectedSem, result.MarginOfErrorNs, 9);

        // The previous behaviour reported zeros for every derived stat - this guards against a
        // regression that reintroduces the fabricated values.
        Assert.NotEqual(0.0, result.StandardDeviationNs);
        Assert.NotEqual(0.0, result.StandardErrorNs);
        Assert.NotEqual(0.0, result.MarginOfErrorNs);
        Assert.NotEqual(0.0, result.Q1Ns);
        Assert.NotEqual(0.0, result.Q3Ns);
        Assert.NotEqual(0.0, result.InterquartileRangeNs);
        Assert.NotEqual(0.0, result.MedianAbsoluteDeviationNs);
    }

    [Fact]
    public void FromCalibration_Preserves_Caller_Mean_And_Median()
    {
        // The caller (PerformanceCalibration) computes mean/median independently of StatsSummary.
        // FromCalibration keeps those as-supplied rather than recomputing them, so the public
        // contract is unchanged. Pass deliberately distinct values to prove the factory does not
        // overwrite them with StatsSummary's own mean/median.
        var samples = new double[] { 10.0, 20.0, 30.0 };

        var result = BenchmarkResult.FromCalibration("c", mean: 999.0, median: 998.0, samples: samples);

        Assert.Equal(999.0, result.MeanNs);
        Assert.Equal(998.0, result.MedianNs);
    }

    [Fact]
    public void FromCalibration_Handles_Empty_Samples()
    {
        var result = BenchmarkResult.FromCalibration("c", mean: 0.0, median: 0.0, samples: Array.Empty<double>());

        Assert.Equal("c", result.Name);
        Assert.Equal(0, result.SampleCount);
        Assert.Equal(0, result.SampleCount);
        Assert.Equal(0, result.OutliersRemoved);
        Assert.Equal(0.0, result.MinNs);
        Assert.Equal(0.0, result.MaxNs);
        Assert.Equal(0.0, result.StandardDeviationNs);
        Assert.Equal(0.0, result.Q1Ns);
        Assert.Equal(0.0, result.Q3Ns);
        Assert.Equal(0.0, result.InterquartileRangeNs);
        Assert.Equal(0.0, result.MedianAbsoluteDeviationNs);
    }

    [Fact]
    public void FromCalibration_Handles_Single_Sample()
    {
        var result = BenchmarkResult.FromCalibration("c", mean: 42.0, median: 42.0, samples: new[] { 42.0 });

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(42.0, result.MinNs);
        Assert.Equal(42.0, result.MaxNs);
        Assert.Equal(42.0, result.Q1Ns);
        Assert.Equal(42.0, result.Q3Ns);
        Assert.Equal(0.0, result.StandardDeviationNs); // n=1 -> sample stddev is 0 by convention
        Assert.Equal(0.0, result.InterquartileRangeNs);

        // n=1 -> StandardErrorNs and MarginOfErrorNs are 0 by the same StatsSummary convention
        // (no spread to estimate from a single sample); ConfidenceLevel is still populated at
        // StatsSummary's default (0.95), matching every other n above.
        Assert.Equal(0.0, result.StandardErrorNs);
        Assert.Equal(0.0, result.MarginOfErrorNs);
        Assert.Equal(0.95, result.ConfidenceLevel);
    }
}
