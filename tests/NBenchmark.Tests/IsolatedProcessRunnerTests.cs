using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class IsolatedProcessRunnerTests
{
    [Fact]
    public async Task Write_Then_Read_Round_Trips_Result_And_Samples()
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

        var outcome = new MeasurementOutcome { Result = result, RawSamples = [1.1, 2.2, 3.3] };
        var path = Path.Combine(Path.GetTempPath(), $"nbench-isolated-test-{Guid.NewGuid():N}.json");

        try
        {
            await IsolatedProcessRunner.WriteResultAsync(path, outcome, CancellationToken.None);
            var read = await IsolatedProcessRunner.ReadResultAsync(path, CancellationToken.None);

            Assert.Equal(result.Name, read.Result.Name);
            Assert.Equal(result.Mean, read.Result.Mean);
            Assert.Equal(result.SignificanceLevel, read.Result.SignificanceLevel);
            Assert.Equal(result.PValue, read.Result.PValue);
            Assert.Equal(SignificanceVerdict.Significant, read.Result.SignificanceVerdict);
            Assert.Equal(result.OutlierMode, read.Result.OutlierMode);
            Assert.Equal(result.MeasuredIterations, read.Result.MeasuredIterations);
            Assert.Equal(result.Warnings, read.Result.Warnings);
            Assert.Equal(outcome.RawSamples, read.RawSamples);

            // Computed bounds recompute from Mean +/- MarginOfError after deserialization.
            Assert.Equal(result.ConfidenceIntervalLower, read.Result.ConfidenceIntervalLower);
            Assert.Equal(result.ConfidenceIntervalUpper, read.Result.ConfidenceIntervalUpper);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
