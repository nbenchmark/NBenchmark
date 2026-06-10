using NBenchmark.Extensions.Abstractions;
using Xunit;

namespace NBenchmark.Extensions.xUnit.Tests;

public sealed class BenchmarkAssertTests
{
    [Fact]
    public void Validate_Returns_No_Violations_When_All_Thresholds_Are_Met()
    {
        var result = CreateResult(mean: 500, p95: 800, allocations: 1000);
        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 1000,
            MaxP95Ns = 1500,
            MaxAllocatedBytes = 2000,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_Returns_Violation_When_Mean_Exceeds_Threshold()
    {
        var result = CreateResult(mean: 1500);
        var thresholds = new PerformanceThresholds { MaxMeanNs = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Single(violations);
        Assert.Contains("Mean 1500", violations[0]);
        Assert.Contains("1000", violations[0]);
    }

    [Fact]
    public void Validate_Returns_Violation_When_P95_Exceeds_Threshold()
    {
        var result = CreateResult(p95: 2000);
        var thresholds = new PerformanceThresholds { MaxP95Ns = 1500 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Single(violations);
        Assert.Contains("P95 2000", violations[0]);
    }

    [Fact]
    public void Validate_Returns_Violation_When_Allocations_Exceed_Threshold()
    {
        var result = CreateResult(allocations: 5000);
        var thresholds = new PerformanceThresholds { MaxAllocatedBytes = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Single(violations);
        Assert.Contains("5000", violations[0]);
    }

    [Fact]
    public void Validate_Returns_No_Violations_When_Thresholds_Are_Null()
    {
        var result = CreateResult(mean: 100000);
        var thresholds = new PerformanceThresholds();

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_Returns_No_Allocation_Violation_When_Result_Has_No_Allocation_Data()
    {
        var result = CreateResult(allocations: null);
        var thresholds = new PerformanceThresholds { MaxAllocatedBytes = 1000 };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_Returns_Multiple_Violations_When_Several_Thresholds_Exceeded()
    {
        var result = CreateResult(mean: 2000, p95: 3000, allocations: 10000);
        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 1000,
            MaxP95Ns = 1500,
            MaxAllocatedBytes = 5000,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Equal(3, violations.Count);
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
            MeanAllocatedBytes = allocations,
            MeasuredIterations = 100,
            WarmupIterations = 25,
        };
    }
}
