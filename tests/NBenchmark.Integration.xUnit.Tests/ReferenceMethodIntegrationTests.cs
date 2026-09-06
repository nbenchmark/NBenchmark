using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class ReferenceMethodIntegrationTests
{
    [Fact]
    public void ReferenceMethod_Resolves_Private_Method()
    {
        var data = PerformanceTestData.FromThresholds(new PerformanceFactAttribute { MaxSlowdownRatio = 1.5 });

        var result = PerformanceTestCase.ValidateResult(
            CreateOkResult("Test.PrivateRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.PrivateReference"),
            CreateSamples(200, 50),
            data);

        Assert.Empty(result);
    }

    [Fact]
    public void ReferenceMethod_With_Async_Returning_Method_Passes()
    {
        var data = PerformanceTestData.FromThresholds(new PerformanceFactAttribute { MaxSlowdownRatio = 1.5 });

        var result = PerformanceTestCase.ValidateResult(
            CreateOkResult("Test.AsyncRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.TaskIntReference"),
            CreateSamples(200, 50),
            data);

        Assert.Empty(result);
    }

    [Fact]
    public void ReferenceMethod_Regression_Fails_When_Candidate_Much_Slower()
    {
        var data = PerformanceTestData.FromThresholds(new PerformanceFactAttribute { MaxSlowdownRatio = 1.2 });

        var violations = PerformanceTestCase.ValidateResult(
            CreateOkResultWithMean("Test.SlowCandidate", 600),
            CreateSamples(600, 100),
            CreateOkResultWithMean("Test.FastReference", 100),
            CreateSamples(100, 100),
            data);

        Assert.Single(violations);
        Assert.Contains("Regression detected", violations[0]);
    }

    [Fact]
    public void ReferenceMethod_Void_Reference_Passes()
    {
        var data = PerformanceTestData.FromThresholds(new PerformanceFactAttribute { MaxSlowdownRatio = 1.5 });

        var result = PerformanceTestCase.ValidateResult(
            CreateOkResult("Test.VoidRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.VoidReference"),
            CreateSamples(200, 50),
            data);

        Assert.Empty(result);
    }

    private static BenchmarkResult CreateOkResult(string name) => CreateOkResultWithMean(name, 100);

    /// <summary>
    ///     Both sides are marked as measured in a worker, which is what a reference-method test
    ///     produces in practice. The ratio gate is only enforced between two such measurements -
    ///     see <see cref="NBenchmark.Integration.Abstractions.PerformanceGate" /> - so a fixture left at the default in-host status
    ///     would be testing the isolation policy rather than the comparison these tests are about.
    /// </summary>
    private static BenchmarkResult CreateOkResultWithMean(string name, double mean) => new()
    {
        Name = name,
        IsolationStatus = IsolationStatus.Isolated,
        Mean = mean,
        Median = mean * 0.9,
        Percentiles = [],
        Min = 50,
        Max = 200,
        StandardDeviation = 10,
        MeasuredIterations = 50,
        WarmupIterations = 10,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 50,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
    };

    private static double[] CreateSamples(double mean, int count)
    {
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = mean + (i % 10 - 5) * 0.05 * mean;
        }

        return samples;
    }
}
