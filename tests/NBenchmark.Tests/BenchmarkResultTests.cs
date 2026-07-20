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
            Mean = 100.0,
            Median = 100.0,
            Percentiles = [],
            Min = 80.0,
            Max = 130.0,
            StandardDeviation = 5.0,
            MarginOfError = 2.5,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 0,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };

        Assert.Equal(97.5, result.ConfidenceIntervalLower);
        Assert.Equal(102.5, result.ConfidenceIntervalUpper);
    }

    [Fact]
    public void Default_OutlierMode_Is_IqrFence()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 0,
            Median = 0,
            Percentiles = [],
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 0,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };

        Assert.Equal(OutlierMode.IqrFence, result.OutlierMode);
    }

    [Fact]
    public void LaunchStatistics_Default_IsNull()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 0,
            Median = 0,
            Percentiles = [],
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 0,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
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
                new LaunchDetail { LaunchIndex = 0, Median = 100, Mean = 102, StandardDeviation = 8, Iterations = 50, Duration = TimeSpan.FromSeconds(1) },
                new LaunchDetail { LaunchIndex = 1, Median = 110, Mean = 112, StandardDeviation = 9, Iterations = 50, Duration = TimeSpan.FromSeconds(1) },
                new LaunchDetail { LaunchIndex = 2, Median = 103, Mean = 105, StandardDeviation = 7, Iterations = 50, Duration = TimeSpan.FromSeconds(1) },
            ],
        };

        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 102,
            Median = 100,
            Percentiles = [],
            Min = 80,
            Max = 130,
            StandardDeviation = 8,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 0,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
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
        // FromCalibration previously reported fabricated zeros for StdDev/Q1/Q3/IQR/Skewness/
        // Kurtosis/Mad while leaving Min/Max as the only honest sample-derived values. The fix
        // routes those through StatsSummary.Compute + Percentile.Compute so the result mirrors
        // a real benchmark's shape - the test-integration comparison path needs honest numbers,
        // and a "calibration" row that looks statistically perfect (StdDev = 0, Mad = 0) is
        // actively misleading.
        var samples = new double[] { 90, 95, 100, 105, 110, 115, 120 };

        var result = BenchmarkResult.FromCalibration("calibration", mean: 105.0, median: 105.0, samples: samples);

        Assert.Equal("calibration", result.Name);
        Assert.Equal(105.0, result.Mean);
        Assert.Equal(105.0, result.Median);
        Assert.Equal(samples.Length, result.N);
        Assert.Equal(samples.Length, result.MeasuredIterations);
        Assert.Equal(0, result.OutliersRemoved);

        // Min/Max come from StatsSummary.Compute (sorted samples), not the unsorted input.
        Assert.Equal(90.0, result.Min);
        Assert.Equal(120.0, result.Max);

        // Standard deviation of [90,95,100,105,110,115,120] (sample stddev, n-1): ~10.801.
        Assert.True(result.StandardDeviation > 0,
            $"StdDev {result.StandardDeviation} should be the sample stddev, not the previous zero");

        // Quartiles use the same nearest-rank Percentile.Compute the stats pipeline uses for
        // raw samples. For n=7: Q1 rank = ceil(0.25*7) - 1 = 1 -> samples[1] = 95; Q3 rank =
        // ceil(0.75*7) - 1 = 5 -> samples[5] = 115.
        Assert.Equal(95.0, result.Q1);
        Assert.Equal(115.0, result.Q3);
        Assert.Equal(20.0, result.InterquartileRange);

        // MAD, skewness, and kurtosis all come from StatsSummary.Compute and must be non-zero on
        // a non-degenerate sample. Skewness of a symmetric set is ~0 (floating-point arithmetic
        // makes the computed value a small non-zero number, so we check |skew| is near zero
        // rather than exactly zero); excess kurtosis of a uniform-ish set is negative.
        Assert.True(result.Mad > 0, $"Mad {result.Mad} should be non-zero for a spread sample set");
        Assert.True(Math.Abs(result.Skewness) < 1e-9,
            $"Skewness {result.Skewness} of a symmetric set should be ~0");
        Assert.True(result.Kurtosis < 0.0,
            $"Excess kurtosis {result.Kurtosis} of a uniform-ish set should be negative (normal is 0, uniform is -1.2)");

        // StandardError, MarginOfError, and ConfidenceLevel also come from StatsSummary.Compute.
        // Before this fix they silently kept the record's own defaults (0.0 / 0.0 / 0.95), which
        // pairs a real, non-zero StandardDeviation with a MarginOfError of exactly 0 - an
        // impossibly tight "confidence interval" for a spread sample set. Cross-check against the
        // same formula StatsSummary itself uses (t-critical x SEM) rather than hardcoding an
        // independently-derived literal.
        var expectedSem = result.StandardDeviation / Math.Sqrt(samples.Length);
        var expectedT = StudentT.CriticalValue(0.95, samples.Length - 1);

        Assert.Equal(0.95, result.ConfidenceLevel);
        Assert.Equal(expectedSem, result.StandardError, 9);
        Assert.Equal(expectedT * expectedSem, result.MarginOfError, 9);

        // The previous behaviour reported zeros for every derived stat - this guards against a
        // regression that reintroduces the fabricated values.
        Assert.NotEqual(0.0, result.StandardDeviation);
        Assert.NotEqual(0.0, result.StandardError);
        Assert.NotEqual(0.0, result.MarginOfError);
        Assert.NotEqual(0.0, result.Q1);
        Assert.NotEqual(0.0, result.Q3);
        Assert.NotEqual(0.0, result.InterquartileRange);
        Assert.NotEqual(0.0, result.Mad);
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

        Assert.Equal(999.0, result.Mean);
        Assert.Equal(998.0, result.Median);
    }

    [Fact]
    public void FromCalibration_Handles_Empty_Samples()
    {
        var result = BenchmarkResult.FromCalibration("c", mean: 0.0, median: 0.0, samples: Array.Empty<double>());

        Assert.Equal("c", result.Name);
        Assert.Equal(0, result.N);
        Assert.Equal(0, result.MeasuredIterations);
        Assert.Equal(0, result.OutliersRemoved);
        Assert.Equal(0.0, result.Min);
        Assert.Equal(0.0, result.Max);
        Assert.Equal(0.0, result.StandardDeviation);
        Assert.Equal(0.0, result.Q1);
        Assert.Equal(0.0, result.Q3);
        Assert.Equal(0.0, result.InterquartileRange);
        Assert.Equal(0.0, result.Mad);
    }

    [Fact]
    public void FromCalibration_Handles_Single_Sample()
    {
        var result = BenchmarkResult.FromCalibration("c", mean: 42.0, median: 42.0, samples: new[] { 42.0 });

        Assert.Equal(1, result.N);
        Assert.Equal(42.0, result.Min);
        Assert.Equal(42.0, result.Max);
        Assert.Equal(42.0, result.Q1);
        Assert.Equal(42.0, result.Q3);
        Assert.Equal(0.0, result.StandardDeviation); // n=1 -> sample stddev is 0 by convention
        Assert.Equal(0.0, result.InterquartileRange);

        // n=1 -> StandardError and MarginOfError are 0 by the same StatsSummary convention
        // (no spread to estimate from a single sample); ConfidenceLevel is still populated at
        // StatsSummary's default (0.95), matching every other n above.
        Assert.Equal(0.0, result.StandardError);
        Assert.Equal(0.0, result.MarginOfError);
        Assert.Equal(0.95, result.ConfidenceLevel);
    }
}
