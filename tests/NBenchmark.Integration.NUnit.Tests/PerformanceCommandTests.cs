using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using NUnit.Framework;

namespace NBenchmark.Integration.NUnit.Tests;

public sealed class PerformanceCommandTests
{
    [Test]
    public void ValidateResult_Fails_When_Benchmark_Errored()
    {
        var data = NewDefaultThresholds();

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

        var violations = PerformanceCommand.ValidateResult(errored, data);

        Assert.That(violations, Has.Some.Contains("Benchmark errored"));
        Assert.That(violations, Has.Some.Contains("Something exploded"));
    }

    [Test]
    public void ValidateResult_Passes_When_Benchmark_Succeeds_And_No_Thresholds_Set()
    {
        var data = NewDefaultThresholds();

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

        var violations = PerformanceCommand.ValidateResult(ok, data);

        Assert.That(violations, Is.Empty);
    }

    [TestCase(nameof(VoidMethod), false)]
    [TestCase(nameof(TaskMethod), true)]
    [TestCase(nameof(ValueTaskMethod), true)]
    [TestCase(nameof(TaskIntMethod), true)]
    [TestCase(nameof(ValueTaskIntMethod), true)]
    public void TryBuildBody_Recognises_All_Supported_Return_Types(string methodName, bool expectedIsAsync)
    {
        var method = typeof(PerformanceCommandTests).GetMethod(
            methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        var built = PerformanceCommand.TryBuildBody(method, null, [], out var body, out var isAsync);

        Assert.That(built, Is.True);
        Assert.That(isAsync, Is.EqualTo(expectedIsAsync));
        Assert.That(body, Is.Not.Null);
    }

    private static void VoidMethod()
    {
    }

    private static Task TaskMethod() => Task.CompletedTask;
    private static ValueTask ValueTaskMethod() => default;
    private static Task<int> TaskIntMethod() => Task.FromResult(42);
    private static async ValueTask<int> ValueTaskIntMethod() => await Task.FromResult(42);

    private static IPerformanceThresholds NewDefaultThresholds() =>
        new PerformanceTestThresholds
        {
            MaxMeanNs = -1,
            MaxP95Ns = -1,
            MaxAllocatedBytes = -1,
            BaselinePath = null,
            MaxSlowdownRatio = 1.2,
            Iterations = 0,
            WarmupIterations = 0,
            MeasureAllocations = false,
            OutlierMode = OutlierMode.RemoveTop5Percent,
            ConfidenceLevel = 0.95,
        };

    private sealed class PerformanceTestThresholds : IPerformanceThresholds
    {
        public double MaxMeanNs { get; init; }
        public double MaxP95Ns { get; init; }
        public long MaxAllocatedBytes { get; init; }
        public string? BaselinePath { get; init; }
        public double MaxSlowdownRatio { get; init; } = 1.2;
        public int Iterations { get; init; }
        public int WarmupIterations { get; init; }
        public bool MeasureAllocations { get; init; }
        public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;
        public double ConfidenceLevel { get; init; } = 0.95;
    }
}

public sealed class PerformanceAssertIntegrationTests
{
    [Test]
    public void PerformanceAssert_Run_Passes_When_Performance_Is_Within_Thresholds()
    {
        var result = PerformanceAssert.Run(SimpleWork, new PerformanceAssertionOptions
        {
            Iterations = 10,
            WarmupIterations = 5,
            MaxMeanNs = 1_000_000_000,
        });

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Errored, Is.False);
    }

    [Test]
    public void PerformanceAssert_Run_Fails_When_Mean_Exceeds_Threshold()
    {
        var ex = Assert.Throws<AssertionException>(() =>
        {
            PerformanceAssert.Run(SlowWork, new PerformanceAssertionOptions
            {
                Iterations = 10,
                WarmupIterations = 5,
                MaxMeanNs = 1,
            });
        });

        Assert.That(ex!.Message, Does.Contain("Mean"));
    }

    [Test]
    public void PerformanceAssert_Validate_Fails_When_Benchmark_Errored()
    {
        var result = BenchmarkRunner.Instance.Run("ThrowingTest", ThrowingWork, new RunSpec
        {
            Options = MeasurementOptions.Default with { Iterations = 3, WarmupIterations = 1 },
        }).Result;

        Assert.That(result.Errored, Is.True);

        var ex = Assert.Throws<AssertionException>(() => { PerformanceAssert.Validate(result); });

        Assert.That(ex!.Message, Does.Contain("Benchmark errored"));
    }

    [Test]
    public void PerformanceAssert_Run_With_Allocations()
    {
        var result = PerformanceAssert.Run(AllocatingWork, new PerformanceAssertionOptions
        {
            Iterations = 10,
            WarmupIterations = 3,
            MeasureAllocations = true,
        });

        Assert.That(result, Is.Not.Null);
        Assert.That(result.MeanAllocatedBytes, Is.GreaterThan(0));
    }

    private static void SimpleWork()
    {
    }

    private static void SlowWork() => Thread.Sleep(1);
    private static byte[] AllocatingWork() => new byte[1024];
    private static void ThrowingWork() => throw new InvalidOperationException("test failure");
}
