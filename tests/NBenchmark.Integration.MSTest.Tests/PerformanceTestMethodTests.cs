using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class PerformanceTestMethodTests
{
    [TestInitialize]
    public void TestInitialize() => BenchmarkAssert.ResetHostAssessment();

    [TestMethod]
    public void ValidateResult_Fails_When_Benchmark_Errored()
    {
        var thresholds = NewDefaultThresholds();

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

        var violations = PerformanceTestMethodAttribute.ValidateResult(errored, [], null, null, thresholds);

        Assert.IsTrue(violations.Any(v => v.Contains("Benchmark errored") && v.Contains("Something exploded")));
    }

    [TestMethod]
    public void ValidateResult_Passes_When_Benchmark_Succeeds_And_No_Thresholds_Set()
    {
        var thresholds = NewDefaultThresholds();

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

        var violations = PerformanceTestMethodAttribute.ValidateResult(ok, [], null, null, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void ValidateResult_Applies_Tolerance_From_Thresholds_On_Shared_Runner()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        var thresholds = new PerformanceTestThresholds
        {
            MaxMeanNs = 500,
            MaxP95Ns = -1,
            MaxAllocatedBytes = -1,
            MaxAbsoluteThresholdTolerance = 1.25,
            MaxSlowdownRatio = 0,
        };

        var violations = PerformanceTestMethodAttribute.ValidateResult(CreateResult("SharedRunner", 610), [], null, null, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    [DataRow(nameof(VoidMethod), false)]
    [DataRow(nameof(TaskMethod), true)]
    [DataRow(nameof(ValueTaskMethod), true)]
    [DataRow(nameof(TaskIntMethod), true)]
    [DataRow(nameof(ValueTaskIntMethod), true)]
    public void TryBuildBody_Recognises_All_Supported_Return_Types(string methodName, bool expectedIsAsync)
    {
        var method = typeof(PerformanceTestMethodTests).GetMethod(
            methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        var built = PerformanceTestMethodAttribute.TryBuildBody(method, null, [], out var body, out var isAsync);

        Assert.IsTrue(built);
        Assert.AreEqual(expectedIsAsync, isAsync);
        Assert.IsNotNull(body);
    }

    [TestMethod]
    public void ResolveReferenceMethod_Uses_Benchmark_Arguments_When_Signature_Matches()
    {
        var benchmarkMethod = typeof(ReferenceResolutionFixture).GetMethod(
            nameof(ReferenceResolutionFixture.CandidateWithArgs),
            BindingFlags.Instance | BindingFlags.Public)!;

        var (referenceMethod, referenceArgs) = PerformanceTestMethodAttribute.ResolveReferenceMethod(
            benchmarkMethod,
            "Reference",
            [42]);

        Assert.AreEqual(1, referenceMethod.GetParameters().Length);
        Assert.AreEqual(1, referenceArgs.Length);
        Assert.AreEqual(42, referenceArgs[0]);
    }

    [TestMethod]
    public void ResolveReferenceMethod_Falls_Back_To_Parameterless_Reference()
    {
        var benchmarkMethod = typeof(ReferenceResolutionFixture).GetMethod(
            nameof(ReferenceResolutionFixture.CandidateWithArgs),
            BindingFlags.Instance | BindingFlags.Public)!;

        var (referenceMethod, referenceArgs) = PerformanceTestMethodAttribute.ResolveReferenceMethod(
            benchmarkMethod,
            "ZeroOnlyReference",
            [42]);

        Assert.AreEqual(0, referenceMethod.GetParameters().Length);
        Assert.AreEqual(0, referenceArgs.Length);
    }

    [TestMethod]
    public void PerformanceAssert_Run_Passes_When_Performance_Is_Within_Thresholds()
    {
        var result = PerformanceAssert.Run(SimpleWork, new PerformanceAssertionOptions
        {
            Samples = 10,
            WarmupSamples = 5,
            MaxMeanNs = 1_000_000_000,
        });

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Errored);
    }

    [TestMethod]
    public void PerformanceAssert_Run_Fails_When_Mean_Exceeds_Threshold()
    {
        var ex = Assert.ThrowsExactly<AssertFailedException>(() =>
        {
            PerformanceAssert.Run(SlowWork, new PerformanceAssertionOptions
            {
                Samples = 10,
                WarmupSamples = 5,
                MaxMeanNs = 1,
            });
        });

        Assert.IsTrue(ex.Message.Contains("MeanNs"));
    }

    [TestMethod]
    public void PerformanceAssert_Validate_Fails_When_Benchmark_Errored()
    {
        var result = BenchmarkRunner.Instance.Run("ThrowingTest", ThrowingWork, new RunSpec
        {
            Options = MeasurementOptions.Default with { Samples = 3, WarmupSamples = 1 },
        }).Result;

        Assert.IsTrue(result.Errored);

        var ex = Assert.ThrowsExactly<AssertFailedException>(() => { PerformanceAssert.Validate(result); });

        Assert.IsTrue(ex.Message.Contains("Benchmark errored"));
    }

    [TestMethod]
    public void PerformanceAssert_Run_With_Allocations()
    {
        var result = PerformanceAssert.Run(AllocatingWork, new PerformanceAssertionOptions
        {
            Samples = 10,
            WarmupSamples = 3,
            MeasureAllocations = true,
        });

        Assert.IsNotNull(result);
        Assert.IsTrue(result.AllocatedBytesMean > 0);
    }

    [TestMethod]
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

    private static void VoidMethod()
    {
    }

    private static Task TaskMethod() => Task.CompletedTask;
    private static ValueTask ValueTaskMethod() => default;
    private static Task<int> TaskIntMethod() => Task.FromResult(42);
    private static async ValueTask<int> ValueTaskIntMethod() => await Task.FromResult(42);

    private static void SimpleWork()
    {
    }

    private static void SlowWork() => Thread.Sleep(1);
    private static byte[] AllocatingWork() => new byte[1024];
    private static void ThrowingWork() => throw new InvalidOperationException("test failure");

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
