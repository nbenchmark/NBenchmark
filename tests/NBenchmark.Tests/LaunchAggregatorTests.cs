using NBenchmark.Engine;
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
    public void BestLaunch_ReturnsLowestMedian()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 110, 112, 9, 100),
            CreateResult("test", 100, 102, 8, 100),
            CreateResult("test", 120, 122, 10, 100),
        };

        var best = LaunchAggregator.BestLaunch(results);

        Assert.Equal(100, best.Median);
    }

    [Fact]
    public void BestLaunch_SkipsErrored()
    {
        var results = new List<BenchmarkResult>
        {
            CreateResult("test", 0, 0, 0, 0, true),
            CreateResult("test", 120, 122, 10, 100),
            CreateResult("test", 100, 102, 8, 100),
        };

        var best = LaunchAggregator.BestLaunch(results);

        Assert.Equal(100, best.Median);
    }

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
