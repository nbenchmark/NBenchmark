using Microsoft.VisualStudio.TestTools.UnitTesting;
using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class BenchmarkAssertTests
{
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
        Assert.IsTrue(violations[0].Contains("Mean 1500"));
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

    private static BenchmarkResult CreateResult(
        double mean = 100,
        double p95 = 150,
        long? allocations = 0)
    {
        return new BenchmarkResult
        {
            Name = "TestBenchmark",
            Mean = mean,
            Median = mean * 0.9,
            P95 = p95,
            P99 = p95 * 1.1,
            Min = mean * 0.5,
            Max = p95 * 1.5,
            StandardDeviation = mean * 0.1,
            Q1 = mean * 0.7,
            Q3 = mean * 1.2,
            InterquartileRange = mean * 0.5,
            OutliersRemoved = 0,
            N = 100,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = allocations,
            AllocP95 = allocations,
            AllocMax = allocations,
            MeanAllocatedBytes = allocations,
            MeasuredIterations = 100,
            WarmupIterations = 25,
        };
    }
}
