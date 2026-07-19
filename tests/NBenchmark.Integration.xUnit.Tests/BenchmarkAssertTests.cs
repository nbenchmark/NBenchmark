using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class BenchmarkAssertTests
{
    public BenchmarkAssertTests()
    {
        BenchmarkAssert.ResetHostAssessment();
    }

    [Fact]
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

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_Returns_Violation_When_Mean_Exceeds_Threshold()
    {
        var result = CreateResult(1500);
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
        var result = CreateResult(100000);
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
        var result = CreateResult(2000, 3000, 10000);

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = 1000,
            MaxP95Ns = 1500,
            MaxAllocatedBytes = 5000,
        };

        var violations = BenchmarkAssert.Validate(result, thresholds);

        Assert.Equal(3, violations.Count);
    }

    [Fact]
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

        Assert.Empty(violations);
    }

    [Fact]
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

        Assert.Single(violations);
        Assert.Contains("relaxed to 625", violations[0]);
    }

    [Fact]
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

        Assert.Single(violations);
        Assert.DoesNotContain("relaxed", violations[0]);
    }

    [Fact]
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

        Assert.Single(violations);
        Assert.DoesNotContain("relaxed", violations[0]);
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
            Percentiles = [new PercentileEntry(0.95, p95)],
            Min = mean * 0.5,
            Max = p95 * 1.5,
            StandardDeviation = mean * 0.1,
            MeanAllocatedBytes = allocations,
            MeasuredIterations = 100,
            WarmupIterations = 25,
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
        };
    }
}
