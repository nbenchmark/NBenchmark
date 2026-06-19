using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class ThresholdCheckTests
{
    [Fact]
    public void HasRegression_ExceedsThreshold_ReturnsTrue()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 120, Median = 120, Percentiles = [],
                Min = 100, Max = 140, StandardDeviation = 8, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.True(hasRegression);
        Assert.Single(regressed);
        Assert.Equal("slow", regressed[0]);
    }

    [Fact]
    public void HasRegression_WithinThreshold_ReturnsFalse()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 110, Median = 110, Percentiles = [],
                Min = 95, Max = 125, StandardDeviation = 5, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 15);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegression_SingleBenchmark_ReturnsFalse()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "solo", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegression_AllErrored_ReturnsFalse()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "broken", Mean = 0, Median = 0, Percentiles = [],
                Min = 0, Max = 0, StandardDeviation = 0, Errored = true,
                ErrorMessage = "error",
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegression_BaselineMedianZero_PositiveCandidate_ReturnsTrue()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 0, Median = 0, Percentiles = [],
                Min = 0, Max = 0, StandardDeviation = 0, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "candidate", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.True(hasRegression);
        Assert.Single(regressed);
        Assert.Equal("candidate", regressed[0]);
    }

    [Fact]
    public void HasRegression_BaselineMedianZero_ZeroCandidate_ReturnsFalse()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 0, Median = 0, Percentiles = [],
                Min = 0, Max = 0, StandardDeviation = 0, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "candidate", Mean = 0, Median = 0, Percentiles = [],
                Min = 0, Max = 0, StandardDeviation = 0, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegression_ZeroThreshold_Throws()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 120, Median = 120, Percentiles = [],
                Min = 100, Max = 140, StandardDeviation = 8, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCheck.HasRegression(results, 0));
    }

    [Fact]
    public void HasRegression_NegativeThreshold_Throws()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCheck.HasRegression(results, -1));
    }

    [Fact]
    public void HasRegression_FasterBenchmark_ReturnsFalse()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "faster", Mean = 50, Median = 50, Percentiles = [],
                Min = 40, Max = 60, StandardDeviation = 3, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegression_MultipleRegressed_ReturnsAllNames()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow_one", Mean = 150, Median = 150, Percentiles = [],
                Min = 130, Max = 170, StandardDeviation = 7, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow_two", Mean = 200, Median = 200, Percentiles = [],
                Min = 180, Max = 260, StandardDeviation = 10, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 20);

        Assert.True(hasRegression);
        Assert.Equal(2, regressed.Count);
        Assert.Contains("slow_one", regressed);
        Assert.Contains("slow_two", regressed);
    }

    [Fact]
    public void HasRegression_ImplicitBaseline_UsesFastestByMedian()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "fast", Mean = 50, Median = 50, Percentiles = [],
                Min = 40, Max = 60, StandardDeviation = 3, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 200, Median = 200, Percentiles = [],
                Min = 180, Max = 260, StandardDeviation = 10, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.True(hasRegression);
        Assert.Single(regressed);
        Assert.Equal("slow", regressed[0]);
    }

    [Fact]
    public void HasRegression_ErroredResult_IsSkipped()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, Percentiles = [],
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "broken", Mean = 0, Median = 0, Percentiles = [],
                Min = 0, Max = 0, StandardDeviation = 0, Errored = true,
                ErrorMessage = "error",
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 150, Median = 150, Percentiles = [],
                Min = 130, Max = 170, StandardDeviation = 7, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.True(hasRegression);
        Assert.Single(regressed);
        Assert.Equal("slow", regressed[0]);
    }
}
