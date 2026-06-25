using NBenchmark.Integration.Abstractions;
using NUnit.Framework;

namespace NBenchmark.Integration.NUnit.Tests;

public sealed class ReferenceMethodIntegrationTests
{
    [Test]
    public void ReferenceMethod_Resolves_Private_Method()
    {
        var violations = PerformanceCommand.ValidateResult(
            CreateOkResult("Test.PrivateRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.PrivateReference"),
            CreateSamples(200, 50),
            new PerformanceAttribute { MaxSlowdownRatio = 1.5 });

        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void ReferenceMethod_With_Async_Returning_Method_Passes()
    {
        var violations = PerformanceCommand.ValidateResult(
            CreateOkResult("Test.AsyncRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.TaskIntReference"),
            CreateSamples(200, 50),
            new PerformanceAttribute { MaxSlowdownRatio = 1.5 });

        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void ReferenceMethod_Regression_Fails_When_Candidate_Much_Slower()
    {
        var violations = PerformanceCommand.ValidateResult(
            CreateOkResultWithMean("Test.SlowCandidate", 600),
            CreateSamples(600, 100),
            CreateOkResultWithMean("Test.FastReference", 100),
            CreateSamples(100, 100),
            new PerformanceAttribute { MaxSlowdownRatio = 1.2 });

        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0], Does.Contain("Regression detected"));
    }

    [Test]
    public void ReferenceMethod_Void_Reference_Passes()
    {
        var violations = PerformanceCommand.ValidateResult(
            CreateOkResult("Test.VoidRef"),
            CreateSamples(100, 50),
            CreateOkResult("Test.VoidReference"),
            CreateSamples(200, 50),
            new PerformanceAttribute { MaxSlowdownRatio = 1.5 });

        Assert.That(violations, Is.Empty);
    }

    private static BenchmarkResult CreateOkResult(string name) => CreateOkResultWithMean(name, 100);

    private static BenchmarkResult CreateOkResultWithMean(string name, double mean) => new()
    {
        Name = name,
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
            samples[i] = mean + (i % 10 - 5) * 0.05 * mean;
        return samples;
    }
}