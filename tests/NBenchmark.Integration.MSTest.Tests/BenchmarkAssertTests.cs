using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class BenchmarkAssertTests
{
    [TestInitialize]
    public void TestInitialize() => BenchmarkAssert.ResetHostAssessment();

    [TestMethod]
    public void Validate_Returns_No_Violations_When_All_Thresholds_Are_Met()
    {
        var result = CreateResult(500, 800, 1000);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 1000,
            MaxP95Ns = 1500,
            MaxAllocatedBytes = 2000,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Validate_Returns_Violation_When_Mean_Exceeds_Threshold()
    {
        var result = CreateResult(1500);
        var thresholds = new PerformanceThresholds { MaxMeanNs = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("MeanNs 1500"));
        Assert.IsTrue(violations[0].Contains("1000"));
    }

    [TestMethod]
    public void Validate_Returns_Violation_When_P95_Exceeds_Threshold()
    {
        var result = CreateResult(p95: 2000);
        var thresholds = new PerformanceThresholds { MaxP95Ns = 1500 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("P95 2000"));
    }

    [TestMethod]
    public void Validate_Returns_Violation_When_Allocations_Exceed_Threshold()
    {
        var result = CreateResult(allocations: 5000);
        var thresholds = new PerformanceThresholds { MaxAllocatedBytes = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("5000"));
    }

    [TestMethod]
    public void Validate_Returns_No_Violations_When_Thresholds_Are_Null()
    {
        var result = CreateResult(100000);
        var thresholds = new PerformanceThresholds();

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Validate_Returns_No_Allocation_Violation_When_Result_Has_No_Allocation_Data()
    {
        var result = CreateResult(allocations: null);
        var thresholds = new PerformanceThresholds { MaxAllocatedBytes = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Validate_Returns_Multiple_Violations_When_Several_Thresholds_Exceeded()
    {
        var result = CreateResult(2000, 3000, 10000);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 1000,
            MaxP95Ns = 1500,
            MaxAllocatedBytes = 5000,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(3, violations.Count);
    }

    [TestMethod]
    public void Validate_Applies_Tolerance_On_Shared_Runner()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        var result = CreateResult(610);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 500,
            MaxAbsoluteThresholdTolerance = 1.25,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Validate_Fails_When_Tolerance_Not_Enough_On_Shared_Runner()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        var result = CreateResult(700);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 500,
            MaxAbsoluteThresholdTolerance = 1.25,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("relaxed to 625"));
    }

    [TestMethod]
    public void Validate_Does_Not_Apply_Tolerance_On_Dedicated_Host()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(8, false, false));

        var result = CreateResult(610);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 500,
            MaxAbsoluteThresholdTolerance = 1.25,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsFalse(violations[0].Contains("relaxed"));
    }

    [TestMethod]
    public void Validate_Does_Not_Apply_Tolerance_When_Tolerance_Is_Default()
    {
        BenchmarkAssert.SetHostAssessment(new HostAssessment(2, false, true));

        var result = CreateResult(610);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 500,
            MaxAbsoluteThresholdTolerance = 1.0,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.AreEqual(1, violations.Count);
        Assert.IsFalse(violations[0].Contains("relaxed"));
    }

    private static BenchmarkResult CreateResult(
        double mean = 100,
        double p95 = 150,
        long? allocations = 0)
    {
        return new BenchmarkResult
        {
            Name = "TestBenchmark",
            MeanNs = mean,
            MedianNs = mean * 0.9,
            Percentiles = [new PercentileEntry(0.95, p95)],
            MinNs = mean * 0.5,
            MaxNs = p95 * 1.5,
            StandardDeviationNs = mean * 0.1,
            AllocatedBytesMean = allocations,
            SampleCount = 100,
            WarmupSamples = 25,
            Q1Ns = mean * 0.7,
            Q3Ns = mean * 1.2,
            InterquartileRangeNs = mean * 0.5,
            OutliersRemoved = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = allocations,
            AllocatedBytesP95 = allocations,
            AllocatedBytesMax = allocations,
        };
    }
}
