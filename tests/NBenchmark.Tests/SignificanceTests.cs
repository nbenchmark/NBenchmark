using Xunit;

namespace NBenchmark.Tests;

public class SignificanceTests
{
    [Fact]
    public void ComputeSignificance_Sets_PValue_And_SignificanceVerdict()
    {
        var rng = new Random(42);
        var baselineSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var fasterSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(40, 60)).ToArray();

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "faster", Mean = 50, Median = 50, P95 = 55, P99 = 58,
                Min = 40, Max = 60, StandardDeviation = 3, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["faster"] = fasterSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.NotNull(results[1].PValue);
        Assert.NotEqual(SignificanceVerdict.NotTested, results[1].SignificanceVerdict);
        Assert.True(results[1].PValue < 0.05);
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_Does_Not_Set_Baseline_PValue()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "other", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
            ["other"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.Equal(SignificanceVerdict.NotTested, results[0].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_Skips_Errored_Results()
    {
        var results = new List<BenchmarkResult>
        {
            ErroredResult("broken", "error"),
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 50).Select(i => (double)i).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ComputeSignificance_With_Only_One_Result_Does_Nothing()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "solo", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["solo"] = Enumerable.Range(0, 50).Select(i => (double)i).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ComputeSignificance_Uses_MinBy_Median_When_No_Baseline()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "fast", Mean = 50, Median = 50, P95 = 55, P99 = 58,
                Min = 40, Max = 60, StandardDeviation = 3, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "slow", Mean = 200, Median = 200, P95 = 220, P99 = 240,
                Min = 180, Max = 260, StandardDeviation = 10, IsBaseline = false,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var rng = new Random(42);
        var fastSamples = Enumerable.Range(0, 30).Select(_ => 50.0 + (rng.NextDouble() - 0.5) * 10).ToArray();
        var slowSamples = Enumerable.Range(0, 30).Select(_ => 200.0 + (rng.NextDouble() - 0.5) * 20).ToArray();

        var rawSamples = new Dictionary<string, double[]>
        {
            ["fast"] = fastSamples,
            ["slow"] = slowSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.NotNull(results[1].PValue);
    }

    [Fact]
    public void ApplyIfEnabled_Skips_When_Disabled()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "a", Mean = 100, Median = 100, P95 = 110, P99 = 115, Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true, Q1 = 0, Q3 = 0,
                InterquartileRange = 0, OutliersRemoved = 0, N = 0, Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
            new()
            {
                Name = "b", Mean = 50, Median = 50, P95 = 55, P99 = 58, Min = 40, Max = 60, StandardDeviation = 3, Q1 = 0, Q3 = 0, InterquartileRange = 0,
                OutliersRemoved = 0, N = 0, Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["a"] = [10, 11, 12], ["b"] = [1, 2, 3] };

        Significance.ApplyIfEnabled(results, raw, new MeasurementOptions { EnableSignificance = false });

        Assert.All(results, r => Assert.Null(r.PValue));
    }

    [Fact]
    public void ApplyIfEnabled_Skips_When_Fewer_Than_Two_Results()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "a", Mean = 100, Median = 100, P95 = 110, P99 = 115, Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true, Q1 = 0, Q3 = 0,
                InterquartileRange = 0, OutliersRemoved = 0, N = 0, Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["a"] = [10, 11, 12] };

        Significance.ApplyIfEnabled(results, raw, MeasurementOptions.Default);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ApplyIfEnabled_Runs_When_Two_Or_More_Successful()
    {
        var rng = new Random(42);
        var baselineSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var fasterSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(40, 60)).ToArray();

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115, Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true, Q1 = 0,
                Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0, Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null,
                AllocMax = null,
            },
            new()
            {
                Name = "faster", Mean = 50, Median = 50, P95 = 55, P99 = 58, Min = 40, Max = 60, StandardDeviation = 3, Q1 = 0, Q3 = 0, InterquartileRange = 0,
                OutliersRemoved = 0, N = 0, Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["baseline"] = baselineSamples, ["faster"] = fasterSamples };

        Significance.ApplyIfEnabled(results, raw, MeasurementOptions.Default);

        Assert.NotNull(results[1].PValue);
    }

    [Fact]
    public void ComputeSignificance_Respects_Configurable_SignificanceLevel()
    {
        var rng = new Random(7);
        var baselineSamples = Enumerable.Range(0, 40).Select(_ => 100.0 + (rng.NextDouble() - 0.5) * 30).ToArray();
        var candidateSamples = Enumerable.Range(0, 40).Select(_ => 92.0 + (rng.NextDouble() - 0.5) * 30).ToArray();

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        // First measure the actual p-value at the default level.
        var probe = NewPair();
        Significance.ComputeSignificance(probe, rawSamples);
        var pValue = probe[1].PValue;

        Assert.NotNull(pValue);

        // A threshold below the observed p makes the same difference NOT significant.
        var strict = NewPair();
        Significance.ComputeSignificance(strict, rawSamples, pValue!.Value / 2);
        Assert.Equal(SignificanceVerdict.NotSignificant, strict[1].SignificanceVerdict);

        // A threshold above the observed p makes it significant.
        var lenient = NewPair();
        Significance.ComputeSignificance(lenient, rawSamples, Math.Min(1.0, pValue.Value * 2));
        Assert.Equal(SignificanceVerdict.Significant, lenient[1].SignificanceVerdict);

        static List<BenchmarkResult> NewPair()
        {
            return
            [
                new()
                {
                    Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                    Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = true,
                    Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                    Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
                },
                new()
                {
                    Name = "candidate", Mean = 92, Median = 92, P95 = 100, P99 = 105,
                    Min = 80, Max = 110, StandardDeviation = 5, IsBaseline = false,
                    Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                    Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
                },
            ];
        }
    }

    private static BenchmarkResult ErroredResult(string name, string error) =>
        new()
        {
            Name = name,
            Mean = 0,
            Median = 0,
            P95 = 0,
            P99 = 0,
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            Errored = true,
            ErrorMessage = error,
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
