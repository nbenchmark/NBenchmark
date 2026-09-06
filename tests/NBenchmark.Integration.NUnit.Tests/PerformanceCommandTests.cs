using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using NUnit.Framework;

namespace NBenchmark.Integration.NUnit.Tests;

public sealed class PerformanceCommandTests
{
    [SetUp]
    public void SetUp() => BenchmarkAssert.ResetHostAssessment();

    [Test]
    public void ValidateResult_Fails_When_Benchmark_Errored()
    {
        var data = NewDefaultThresholds();

        var errored = new BenchmarkResult
        {
            Name = "Broken",
            MeanNs = 0,
            MedianNs = 0,
            Percentiles = [],
            MinNs = 0,
            MaxNs = 0,
            StandardDeviationNs = 0,
            SampleCount = 0,
            WarmupSamples = 0,
            Errored = true,
            ErrorMessage = "Something exploded",
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        var violations = PerformanceCommand.ValidateResult(errored, [], null, null, data);

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
            MeanNs = 100,
            MedianNs = 100,
            Percentiles = [],
            MinNs = 50,
            MaxNs = 250,
            StandardDeviationNs = 10,
            SampleCount = 100,
            WarmupSamples = 25,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };

        var violations = PerformanceCommand.ValidateResult(ok, [], null, null, data);

        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void ValidateResult_Applies_Tolerance_From_Thresholds_On_Shared_Runner()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        var data = new PerformanceTestThresholds
        {
            MaxMeanNs = 500,
            MaxP95Ns = -1,
            MaxAllocatedBytes = -1,
            MaxAbsoluteThresholdTolerance = 1.25,
            MaxSlowdownRatio = 0,
        };

        var violations = PerformanceCommand.ValidateResult(CreateResult("SharedRunner", 610), [], null, null, data);

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

    [Test]
    public void ResolveReferenceMethod_Uses_Benchmark_Arguments_When_Signature_Matches()
    {
        var benchmarkMethod = typeof(ReferenceResolutionFixture).GetMethod(
            nameof(ReferenceResolutionFixture.CandidateWithArgs),
            BindingFlags.Instance | BindingFlags.Public)!;

        var (referenceMethod, referenceArgs) = PerformanceCommand.ResolveReferenceMethod(
            benchmarkMethod,
            "Reference",
            [42]);

        Assert.That(referenceMethod.GetParameters().Length, Is.EqualTo(1));
        Assert.That(referenceArgs.Length, Is.EqualTo(1));
        Assert.That(referenceArgs[0], Is.EqualTo(42));
    }

    [Test]
    public void ResolveReferenceMethod_Falls_Back_To_Parameterless_Reference()
    {
        var benchmarkMethod = typeof(ReferenceResolutionFixture).GetMethod(
            nameof(ReferenceResolutionFixture.CandidateWithArgs),
            BindingFlags.Instance | BindingFlags.Public)!;

        var (referenceMethod, referenceArgs) = PerformanceCommand.ResolveReferenceMethod(
            benchmarkMethod,
            "ZeroOnlyReference",
            [42]);

        Assert.That(referenceMethod.GetParameters().Length, Is.EqualTo(0));
        Assert.That(referenceArgs.Length, Is.EqualTo(0));
    }

    private static void VoidMethod()
    {
    }

    private static Task TaskMethod() => Task.CompletedTask;
    private static ValueTask ValueTaskMethod() => default;
    private static Task<int> TaskIntMethod() => Task.FromResult(42);
    private static async ValueTask<int> ValueTaskIntMethod() => await Task.FromResult(42);

    private static BenchmarkResult CreateResult(string name, double mean)
    {
        return new BenchmarkResult
        {
            Name = name,
            MeanNs = mean,
            MedianNs = mean,
            Percentiles = [],
            MinNs = mean,
            MaxNs = mean,
            StandardDeviationNs = 0,
            SampleCount = 100,
            WarmupSamples = 25,
            Q1Ns = mean,
            Q3Ns = mean,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };
    }

    private static IPerformanceThresholds NewDefaultThresholds() =>
        new PerformanceTestThresholds
        {
            MaxMeanNs = -1,
            MaxP95Ns = -1,
            MaxAllocatedBytes = -1,
            ReferenceMethod = null,
            MaxSlowdownRatio = 0,
            Samples = 0,
            WarmupSamples = 0,
            MeasureAllocations = false,
            OutlierMode = OutlierMode.RemoveTop5Percent,
            ConfidenceLevel = 0.95,
        };

    private sealed class PerformanceTestThresholds : IPerformanceThresholds
    {
        public double MaxMeanNs { get; init; }
        public double MaxMedianNs { get; init; } = -1;
        public double MaxP95Ns { get; init; }
        public long MaxAllocatedBytes { get; init; }
        public string? ReferenceMethod { get; init; }
        public double MaxSlowdownRatio { get; init; } = 1.2;
        public int Samples { get; init; }
        public int WarmupSamples { get; init; }
        public bool MeasureAllocations { get; init; }
        public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;
        public double ConfidenceLevel { get; init; } = 0.95;
        public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

        // Off, unlike production, because these tests exercise threshold arithmetic over fabricated
        // results that were never measured anywhere. Leaving it on would fail every one of them on the
        // isolation requirement instead of on the threshold under test. The requirement itself is
        // covered against the shared PerformanceGate in PerformanceGateIsolationTests.
        public bool RequireIsolation { get; init; }
    }

    private sealed class ReferenceResolutionFixture
    {
        public void CandidateWithArgs(int value)
        {
        }

        private static void Reference(int value)
        {
        }

        private static void Reference()
        {
        }

        private static void ZeroOnlyReference()
        {
        }
    }
}

public sealed class PerformanceAssertIntegrationTests
{
    [SetUp]
    public void SetUp() => BenchmarkAssert.ResetHostAssessment();

    [Test]
    public void PerformanceAssert_Run_Passes_When_Performance_Is_Within_Thresholds()
    {
        var result = PerformanceAssert.Run(SimpleWork, new PerformanceAssertionOptions
        {
            Samples = 10,
            WarmupSamples = 5,
            MaxMeanNs = 1_000_000_000,
        });

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Errored, Is.False);
    }

    [Test]
    public void PerformanceAssert_Run_Fails_When_Mean_Exceeds_Threshold()
    {
        var ex = Assert.Throws<AssertionException>((Action)(() =>
        {
            PerformanceAssert.Run(SlowWork, new PerformanceAssertionOptions
            {
                Samples = 10,
                WarmupSamples = 5,
                MaxMeanNs = 1,
            });
        }));

        Assert.That(ex!.Message, Does.Contain("MeanNs"));
    }

    [Test]
    public void PerformanceAssert_Validate_Fails_When_Benchmark_Errored()
    {
        var result = BenchmarkRunner.Instance.Run("ThrowingTest", ThrowingWork, new RunSpec
        {
            Options = MeasurementOptions.Default with { Samples = 3, WarmupSamples = 1 },
        }).Result;

        Assert.That(result.Errored, Is.True);

        var ex = Assert.Throws<AssertionException>((Action)(() => { PerformanceAssert.Validate(result); }));

        Assert.That(ex!.Message, Does.Contain("Benchmark errored"));
    }

    [Test]
    public void PerformanceAssert_Run_With_Allocations()
    {
        var result = PerformanceAssert.Run(AllocatingWork, new PerformanceAssertionOptions
        {
            Samples = 10,
            WarmupSamples = 3,
            MeasureAllocations = true,
        });

        Assert.That(result, Is.Not.Null);
        Assert.That(result.AllocatedBytesMean, Is.GreaterThan(0));
    }

    [Test]
    public void PerformanceAssert_Validate_Applies_Tolerance_From_Options_On_Shared_Runner()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        PerformanceAssert.Validate(
            CreateResult("SharedRunner", 610),
            new PerformanceAssertionOptions
            {
                MaxMeanNs = 500,
                MaxAbsoluteThresholdTolerance = 1.25,
                MaxSlowdownRatio = 0,

                // The result is fabricated, not measured, so the isolation requirement has nothing
                // real to assess here. This is the option bag's opt-out, which the PerformanceAssert
                // pattern needs because there is no attribute target for [AllowInProcessGate].
                RequireIsolation = false,
            });
    }

    private static void SimpleWork()
    {
    }

    private static BenchmarkResult CreateResult(string name, double mean)
    {
        return new BenchmarkResult
        {
            Name = name,
            MeanNs = mean,
            MedianNs = mean,
            Percentiles = [],
            MinNs = mean,
            MaxNs = mean,
            StandardDeviationNs = 0,
            SampleCount = 100,
            WarmupSamples = 25,
            Q1Ns = mean,
            Q3Ns = mean,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };
    }

    private static void SlowWork() => Thread.Sleep(1);
    private static byte[] AllocatingWork() => new byte[1024];
    private static void ThrowingWork() => throw new InvalidOperationException("test failure");
}
