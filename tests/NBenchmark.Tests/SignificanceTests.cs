using Xunit;

namespace NBenchmark.Tests;

public class SignificanceTests
{
    [Fact]
    public void ComputeSignificance_Sets_PValue_And_IsSignificant()
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
            },
            new()
            {
                Name = "faster", Mean = 50, Median = 50, P95 = 55, P99 = 58,
                Min = 40, Max = 60, StandardDeviation = 3, IsBaseline = false,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["faster"] = fasterSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.NotNull(results[1].PValue);
        Assert.NotNull(results[1].IsSignificant);
        Assert.True(results[1].PValue < 0.05);
        Assert.True(results[1].IsSignificant);
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
            },
            new()
            {
                Name = "other", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5, IsBaseline = false,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
            ["other"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.Null(results[0].IsSignificant);
    }

    [Fact]
    public void ComputeSignificance_Skips_Errored_Results()
    {
        var results = new List<BenchmarkResult>
        {
            BenchmarkResult.CreateErrored("broken", "error"),
            new()
            {
                Name = "baseline", Mean = 100, Median = 100, P95 = 110, P99 = 115,
                Min = 85, Max = 120, StandardDeviation = 5,
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
            },
            new()
            {
                Name = "slow", Mean = 200, Median = 200, P95 = 220, P99 = 240,
                Min = 180, Max = 260, StandardDeviation = 10, IsBaseline = false,
            },
        };

        var fastSamples = Enumerable.Range(0, 30).Select(_ => 50.0 + (Random.Shared.NextDouble() - 0.5) * 10).ToArray();
        var slowSamples = Enumerable.Range(0, 30).Select(_ => 200.0 + (Random.Shared.NextDouble() - 0.5) * 20).ToArray();

        var rawSamples = new Dictionary<string, double[]>
        {
            ["fast"] = fastSamples,
            ["slow"] = slowSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.NotNull(results[1].PValue);
    }
}