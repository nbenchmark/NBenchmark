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

    [Fact]
    public void Check_ExceedsThreshold_ReturnsVerdictWithRatioAndDelta()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", median: 100, isBaseline: true),
            MakeResult("slow", median: 150, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.True(verdict.HasRegression);
        Assert.Equal("baseline", verdict.BaselineName);
        Assert.Single(verdict.RegressedCandidates);
        Assert.Single(verdict.RegressedNames);

        var candidate = verdict.RegressedCandidates[0];
        Assert.Equal("slow", candidate.Name);
        Assert.Equal(150d, candidate.CandidateMedian);
        Assert.Equal(100d, candidate.BaselineMedian);
        Assert.Equal(1.5, candidate.Ratio, 12);
        Assert.Equal(50d, candidate.DeltaNs, 12);
        Assert.Equal("slow", verdict.RegressedNames[0]);
    }

    [Fact]
    public void Check_WithinThreshold_ReturnsNoneVerdict()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", median: 100, isBaseline: true),
            MakeResult("candidate", median: 105, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.False(verdict.HasRegression);
        Assert.Equal(string.Empty, verdict.BaselineName);
        Assert.Empty(verdict.RegressedCandidates);
        Assert.Empty(verdict.RegressedNames);
    }

    [Fact]
    public void Check_MultipleRegressed_CandidatesInEvaluationOrder_NamesSortedAscending()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", median: 100, isBaseline: true),
            MakeResult("zeta", median: 200, isBaseline: false),
            MakeResult("alpha", median: 150, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.True(verdict.HasRegression);
        Assert.Equal(2, verdict.RegressedCandidates.Count);
        Assert.Equal(2, verdict.RegressedNames.Count);

        // RegressedCandidates preserve evaluation order (input order).
        Assert.Equal("zeta", verdict.RegressedCandidates[0].Name);
        Assert.Equal("alpha", verdict.RegressedCandidates[1].Name);

        // RegressedNames are sorted ascending by ordinal (string ordinal sort).
        Assert.Equal("alpha", verdict.RegressedNames[0]);
        Assert.Equal("zeta", verdict.RegressedNames[1]);
    }

    [Fact]
    public void Check_BaselineMedianZero_RatioIsNaN_DeltaIsCandidateMedian()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", median: 0, isBaseline: true),
            MakeResult("candidate", median: 100, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.True(verdict.HasRegression);
        var candidate = verdict.RegressedCandidates[0];
        Assert.Equal(double.NaN, candidate.Ratio);
        Assert.Equal(100d, candidate.DeltaNs, 12);
    }

    [Fact]
    public void Check_ImplicitBaseline_UsesFastestByMedian_AsBaselineName()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("slow", median: 200, isBaseline: false),
            MakeResult("fast", median: 50, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.True(verdict.HasRegression);
        Assert.Equal("fast", verdict.BaselineName);
        Assert.Single(verdict.RegressedCandidates);
        Assert.Equal("slow", verdict.RegressedCandidates[0].Name);
        Assert.Equal(4.0, verdict.RegressedCandidates[0].Ratio, 12);
    }

    [Fact]
    public void Check_SingleSuccessfulBenchmark_ReturnsNoneVerdict()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("solo", median: 100, isBaseline: false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.False(verdict.HasRegression);
        Assert.Equal(RegressionVerdict.None.BaselineName, verdict.BaselineName);
    }

    [Fact]
    public void Check_ZeroThreshold_Throws()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", median: 100, isBaseline: true),
            MakeResult("slow", median: 150, isBaseline: false),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCheck.Check(results, 0));
    }

    private static BenchmarkResult MakeResult(string name, double median, bool isBaseline)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = median,
            Median = median,
            Percentiles = [],
            Min = median * 0.85,
            Max = median * 1.2,
            StandardDeviation = median * 0.05,
            IsBaseline = isBaseline,
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
    }
}
