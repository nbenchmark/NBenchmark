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
            P95 = 110.0,
            P99 = 120.0,
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
            P95 = 0,
            P99 = 0,
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
            P95 = 0,
            P99 = 0,
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
            P95 = 110,
            P99 = 120,
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
}
