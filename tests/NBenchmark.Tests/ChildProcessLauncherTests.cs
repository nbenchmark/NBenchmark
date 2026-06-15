using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class ChildProcessLauncherTests
{
    [Fact]
    public async Task Write_Then_Read_Round_Trips_Items_Results_And_Samples()
    {
        var result = new BenchmarkResult
        {
            Name = "Suite.Benchmark",
            Description = "round-trip",
            Mean = 123.4,
            Median = 120.0,
            P95 = 150.0,
            P99 = 160.0,
            Min = 90.0,
            Max = 200.0,
            StandardDeviation = 12.5,
            StandardError = 1.25,
            MarginOfError = 2.5,
            ConfidenceLevel = 0.99,
            CoefficientOfVariation = 0.1,
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
            MeanAllocatedBytes = 4096,
            PValue = 0.012,
            SignificanceVerdict = SignificanceVerdict.Significant,
            SignificanceLevel = 0.01,
            MeasuredIterations = 190,
            WarmupIterations = 25,
            TotalDuration = TimeSpan.FromMilliseconds(500),
            MeasuredDuration = TimeSpan.FromMilliseconds(420),
            IsBaseline = true,
            OutlierMode = OutlierMode.IqrFence,
            Warnings = ["something bimodal happened"],
        };

        var items = new List<IsolatedResultItem>
        {
            new() { Result = result, RawSamples = [1.1, 2.2, 3.3] },
        };

        var path = Path.Combine(Path.GetTempPath(), $"nbench-isolated-test-{Guid.NewGuid():N}.json");

        try
        {
            await ChildProcessLauncher.WritePayloadAsync(path, items, CancellationToken.None);
            var read = await ChildProcessLauncher.ReadPayloadAsync(path, CancellationToken.None);

            var item = Assert.Single(read);
            Assert.Equal(result.Name, item.Result.Name);
            Assert.Equal(result.Mean, item.Result.Mean);
            Assert.Equal(result.SignificanceLevel, item.Result.SignificanceLevel);
            Assert.Equal(result.PValue, item.Result.PValue);
            Assert.Equal(SignificanceVerdict.Significant, item.Result.SignificanceVerdict);
            Assert.Equal(result.OutlierMode, item.Result.OutlierMode);
            Assert.Equal(result.MeasuredIterations, item.Result.MeasuredIterations);
            Assert.Equal(result.Warnings, item.Result.Warnings);
            Assert.Equal<double[]>([1.1, 2.2, 3.3], item.RawSamples);

            // Computed bounds recompute from Mean +/- MarginOfError after deserialization.
            Assert.Equal(result.ConfidenceIntervalLower, item.Result.ConfidenceIntervalLower);
            Assert.Equal(result.ConfidenceIntervalUpper, item.Result.ConfidenceIntervalUpper);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_Then_Read_Round_Trips_Multiple_Items()
    {
        var items = new List<IsolatedResultItem>
        {
            new() { Result = MinimalResult("Suite.A"), RawSamples = [1.0, 2.0] },
            new() { Result = MinimalResult("Suite.B"), RawSamples = [3.0] },
        };

        var path = Path.Combine(Path.GetTempPath(), $"nbench-isolated-test-{Guid.NewGuid():N}.json");

        try
        {
            await ChildProcessLauncher.WritePayloadAsync(path, items, CancellationToken.None);
            var read = await ChildProcessLauncher.ReadPayloadAsync(path, CancellationToken.None);

            Assert.Equal(2, read.Count);
            Assert.Equal("Suite.A", read[0].Result.Name);
            Assert.Equal<double[]>([1.0, 2.0], read[0].RawSamples);
            Assert.Equal("Suite.B", read[1].Result.Name);
            Assert.Equal<double[]>([3.0], read[1].RawSamples);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static BenchmarkResult MinimalResult(string name) => new()
    {
        Name = name,
        Mean = 1,
        Median = 1,
        P95 = 1,
        P99 = 1,
        Min = 1,
        Max = 1,
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
}
