using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class PerformanceFactIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceFactIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PerformanceFact_Passes_When_Performance_Is_Within_Thresholds()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with { Iterations = 10, WarmupIterations = 5 },
        };

        var outcome = BenchmarkRunner.Instance.Run("FastTest", SimpleWork, spec);

        var result = outcome.Result;
        var thresholds = new PerformanceThresholds { MaxMeanNs = 1_000_000_000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Empty(violations);
    }

    [Fact]
    public void PerformanceFact_Fails_When_Mean_Exceeds_Threshold()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with { Iterations = 10, WarmupIterations = 5 },
        };

        var outcome = BenchmarkRunner.Instance.Run("SlowTest", SlowWork, spec);

        var result = outcome.Result;
        var thresholds = new PerformanceThresholds { MaxMeanNs = 1 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void PerformanceFact_Fails_When_P95_Exceeds_Threshold()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with { Iterations = 10, WarmupIterations = 5 },
        };

        var outcome = BenchmarkRunner.Instance.Run("JitteryTest", JitteryWork, spec);

        var result = outcome.Result;
        var thresholds = new PerformanceThresholds { MaxP95Ns = 1 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void PerformanceFact_Checks_Allocations_When_Enabled()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with
            {
                Iterations = 10,
                WarmupIterations = 3,
                MeasureAllocations = true,
            },
        };

        var outcome = BenchmarkRunner.Instance.Run("AllocTest", AllocatingWork, spec);

        var result = outcome.Result;
        Assert.NotNull(result.MeanAllocatedBytes);
        Assert.True(result.MeanAllocatedBytes > 0);
    }

    [Fact]
    public void PerformanceFact_Handles_Errored_Benchmarks()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with { Iterations = 3, WarmupIterations = 1 },
        };

        var outcome = BenchmarkRunner.Instance.Run("ThrowingTest", ThrowingWork, spec);

        var result = outcome.Result;
        Assert.True(result.Errored);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void PerformanceFact_Custom_Iterations_Default_To_Standard_Values()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with { MeasureAllocations = true, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("DefaultTest", SimpleWork, spec);

        var result = outcome.Result;
        Assert.Equal(200, result.MeasuredIterations);
        Assert.Equal(25, result.WarmupIterations);
    }

    [Fact]
    public void PerformanceFact_With_Baseline_Regression_Passes()
    {
        var baselinePath = Path.GetTempFileName();

        try
        {
            var baselineJson = """
                               {
                                 "generatedAt": "2025-01-01T00:00:00Z",
                                 "results": [
                                   { "name": "BaselineTest", "mean": 100 }
                                 ]
                               }
                               """;

            File.WriteAllText(baselinePath, baselineJson);

            var spec = new RunSpec
            {
                Options = MeasurementOptions.Default with { Iterations = 10, WarmupIterations = 5 },
            };

            var outcome = BenchmarkRunner.Instance.Run("BaselineTest", SimpleWork, spec);
            var result = outcome.Result;

            var violations = RegressionBaseline.Check(result, baselinePath, 10.0);

            Assert.Empty(violations);
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public void PerformanceFact_Accepts_OutlierMode_Configuration()
    {
        var spec = new RunSpec
        {
            Options = MeasurementOptions.Default with
            {
                Iterations = 50,
                WarmupIterations = 5,
                OutlierMode = OutlierMode.None,
            },
        };

        var outcome = BenchmarkRunner.Instance.Run("OutlierNoneTest", SimpleWork, spec);

        Assert.Equal(OutlierMode.None, outcome.Result.OutlierMode);
    }

    private static void SimpleWork()
    {
    }

    private static void SlowWork() => Thread.Sleep(1);

    private static void JitteryWork()
    {
        Thread.Sleep(1);

        for (var i = 0; i < 1000; i++)
        {
        }
    }

    private static byte[] AllocatingWork() => new byte[1024];

    private static void ThrowingWork() => throw new InvalidOperationException("test failure");

    [Theory]
    [InlineData(nameof(VoidMethod), false)]
    [InlineData(nameof(TaskMethod), true)]
    [InlineData(nameof(ValueTaskMethod), true)]
    [InlineData(nameof(TaskIntMethod), true)]
    [InlineData(nameof(ValueTaskIntMethod), true)]
    public void TryBuildBody_Recognises_All_Supported_Return_Types(string methodName, bool expectedIsAsync)
    {
        var method = typeof(PerformanceFactIntegrationTests).GetMethod(
            methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        var built = PerformanceTestCase.TryBuildBody(method, null, [], out var body, out var isAsync);

        Assert.True(built);
        Assert.Equal(expectedIsAsync, isAsync);
        Assert.NotNull(body);
    }

    [Fact]
    public void ValidateResult_Fails_When_Benchmark_Errored()
    {
        var data = NewDefaultData();

        var errored = new BenchmarkResult
        {
            Name = "Broken",
            Mean = 0,
            Median = 0,
            P95 = 0,
            P99 = 0,
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            MeasuredIterations = 0,
            WarmupIterations = 0,
            Errored = true,
            ErrorMessage = "Something exploded",
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

        var violations = PerformanceTestCase.ValidateResult(errored, data);

        Assert.Contains(violations, v => v.Contains("Benchmark errored") && v.Contains("Something exploded"));
    }

    [Fact]
    public void ValidateResult_Passes_When_Benchmark_Succeeds_And_No_Thresholds_Set()
    {
        var data = NewDefaultData();

        var ok = new BenchmarkResult
        {
            Name = "Fine",
            Mean = 100,
            Median = 100,
            P95 = 200,
            P99 = 200,
            Min = 50,
            Max = 250,
            StandardDeviation = 10,
            MeasuredIterations = 100,
            WarmupIterations = 25,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 100,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };

        var violations = PerformanceTestCase.ValidateResult(ok, data);

        Assert.Empty(violations);
    }

    private static PerformanceTestData NewDefaultData() =>
        new(
            -1,
            -1,
            -1,
            null,
            1.2,
            0,
            0,
            false,
            OutlierMode.RemoveTop5Percent,
            0.95);

    private static void VoidMethod()
    {
    }

    private static Task TaskMethod() => Task.CompletedTask;
    private static ValueTask ValueTaskMethod() => default;
    private static Task<int> TaskIntMethod() => Task.FromResult(42);
    private static async ValueTask<int> ValueTaskIntMethod() => await Task.FromResult(42);
}
