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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 120, MedianNs = 120, Percentiles = [],
                MinNs = 100, MaxNs = 140, StandardDeviationNs = 8, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 110, MedianNs = 110, Percentiles = [],
                MinNs = 95, MaxNs = 125, StandardDeviationNs = 5, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "solo", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "broken", MeanNs = 0, MedianNs = 0, Percentiles = [],
                MinNs = 0, MaxNs = 0, StandardDeviationNs = 0, Errored = true,
                ErrorMessage = "error",
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 0, MedianNs = 0, Percentiles = [],
                MinNs = 0, MaxNs = 0, StandardDeviationNs = 0, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "candidate", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 0, MedianNs = 0, Percentiles = [],
                MinNs = 0, MaxNs = 0, StandardDeviationNs = 0, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "candidate", MeanNs = 0, MedianNs = 0, Percentiles = [],
                MinNs = 0, MaxNs = 0, StandardDeviationNs = 0, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 120, MedianNs = 120, Percentiles = [],
                MinNs = 100, MaxNs = 140, StandardDeviationNs = 8, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "faster", MeanNs = 50, MedianNs = 50, Percentiles = [],
                MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow_one", MeanNs = 150, MedianNs = 150, Percentiles = [],
                MinNs = 130, MaxNs = 170, StandardDeviationNs = 7, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow_two", MeanNs = 200, MedianNs = 200, Percentiles = [],
                MinNs = 180, MaxNs = 260, StandardDeviationNs = 10, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "fast", MeanNs = 50, MedianNs = 50, Percentiles = [],
                MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 200, MedianNs = 200, Percentiles = [],
                MinNs = 180, MaxNs = 260, StandardDeviationNs = 10, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "broken", MeanNs = 0, MedianNs = 0, Percentiles = [],
                MinNs = 0, MaxNs = 0, StandardDeviationNs = 0, Errored = true,
                ErrorMessage = "error",
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 150, MedianNs = 150, Percentiles = [],
                MinNs = 130, MaxNs = 170, StandardDeviationNs = 7, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
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
            MakeResult("baseline", 100, true),
            MakeResult("slow", 150, false),
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
            MakeResult("baseline", 100, true),
            MakeResult("candidate", 105, false),
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
            MakeResult("baseline", 100, true),
            MakeResult("zeta", 200, false),
            MakeResult("alpha", 150, false),
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
            MakeResult("baseline", 0, true),
            MakeResult("candidate", 100, false),
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
            MakeResult("slow", 200, false),
            MakeResult("fast", 50, false),
        };

        var verdict = ThresholdCheck.Check(results, 10);

        Assert.True(verdict.HasRegression);
        Assert.Equal("fast", verdict.BaselineName);
        Assert.Single(verdict.RegressedCandidates);
        Assert.Equal("slow", verdict.RegressedCandidates[0].Name);
        Assert.Equal(4.0, verdict.RegressedCandidates[0].Ratio, 12);
    }

    [Fact]
    public void Check_WithLaunchStatistics_UsesLaunchMedianForComparison()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", 100, true) with
            {
                LaunchStatistics = new LaunchStatistics
                {
                    LaunchCount = 3,
                    LaunchMean = 110,
                    LaunchStandardDeviation = 5,
                    LaunchMedian = 110,
                },
            },
            MakeResult("candidate", 105, false) with
            {
                LaunchStatistics = new LaunchStatistics
                {
                    LaunchCount = 3,
                    LaunchMean = 140,
                    LaunchStandardDeviation = 7,
                    LaunchMedian = 140,
                },
            },
        };

        // Best-launch medians would not trip a 20% gate (105 vs 100), but launch medians do
        // (140 vs 110 => ~27%).
        var verdict = ThresholdCheck.Check(results, 20);

        Assert.True(verdict.HasRegression);
        var candidate = Assert.Single(verdict.RegressedCandidates);
        Assert.Equal("candidate", candidate.Name);
        Assert.Equal(140d, candidate.CandidateMedian);
        Assert.Equal(110d, candidate.BaselineMedian);
    }

    [Fact]
    public void Check_SingleSuccessfulBenchmark_ReturnsNoneVerdict()
    {
        var results = new List<BenchmarkResult>
        {
            MakeResult("solo", 100, false),
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
            MakeResult("baseline", 100, true),
            MakeResult("slow", 150, false),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCheck.Check(results, 0));
    }

    // --- W-34: the threshold gate must partition by comparison group and class ----------

    [Fact]
    public void HasRegression_Unpartitioned_MixedProfile_FlagsIsolatedAgainstInProcessBaseline()
    {
        // The bug, pinned as a contrast: with no partitioning the in-process row (profile
        // "host", faster purely from configuration) becomes the implicit baseline and every
        // isolated row is a fabricated regression.
        var results = new List<BenchmarkResult>
        {
            MakeResult("in_process", 30, false, RuntimeProfile.Host.Name, "ClassA"),
            MakeResult("isolated", 100, false, "SteadyState", "ClassA", IsolationStatus.Isolated),
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegression(results, 10);

        Assert.True(hasRegression);
        Assert.Equal("isolated", Assert.Single(regressed));
    }

    [Fact]
    public void HasRegressionAcrossGroups_MixedProfile_DoesNotFlagIsolatedAgainstInProcess()
    {
        // Same inputs as above; partitioning by comparison group puts the in-process row
        // ("host") and the isolated row ("SteadyState") in different partitions, so neither
        // is the other's baseline and no regression is fabricated.
        var results = new List<BenchmarkResult>
        {
            MakeResult("in_process", 30, false, RuntimeProfile.Host.Name, "ClassA"),
            MakeResult("isolated", 100, false, "SteadyState", "ClassA", IsolationStatus.Isolated),
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegressionAcrossGroups(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegressionAcrossGroups_DifferentClasses_DoesNotCrossClassBaseline()
    {
        // An unrelated benchmark in a different class must not become this class's implicit
        // baseline. Unpartitioned, class A's slow row is "regressed" against class B's fast
        // row; partitioned by class, each class is evaluated on its own.
        var results = new List<BenchmarkResult>
        {
            MakeResult("a_slow", 200, false, "SteadyState", "ClassA"),
            MakeResult("b_fast", 50, false, "SteadyState", "ClassB"),
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegressionAcrossGroups(results, 10);

        Assert.False(hasRegression);
        Assert.Empty(regressed);
    }

    [Fact]
    public void HasRegressionAcrossGroups_SameGroup_StillDetectsRealRegression()
    {
        // A genuine regression within one comparison group and one class is still flagged.
        var results = new List<BenchmarkResult>
        {
            MakeResult("baseline", 100, true, "SteadyState", "ClassA"),
            MakeResult("slow", 150, false, "SteadyState", "ClassA"),
        };

        var (hasRegression, regressed) = ThresholdCheck.HasRegressionAcrossGroups(results, 10);

        Assert.True(hasRegression);
        Assert.Equal("slow", Assert.Single(regressed));
    }

    private static BenchmarkResult MakeResult(string name, double median, bool isBaseline)
        => MakeResult(name, median, isBaseline, RuntimeProfile.Host.Name, "");

    private static BenchmarkResult MakeResult(
        string name, double median, bool isBaseline, string runtimeProfileName, string className,
        IsolationStatus isolationStatus = IsolationStatus.InProcessRequested)
    {
        return new BenchmarkResult
        {
            Name = name,
            MeanNs = median,
            MedianNs = median,
            Percentiles = [],
            MinNs = median * 0.85,
            MaxNs = median * 1.2,
            StandardDeviationNs = median * 0.05,
            IsBaseline = isBaseline,
            RuntimeProfileName = runtimeProfileName,
            ClassName = className,
            IsolationStatus = isolationStatus,
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
    }
}
