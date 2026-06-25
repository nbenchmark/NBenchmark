using System.Text.Json;
using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class RegressionBaselineTests
{
    [TestMethod]
    public void Check_Returns_Violation_When_File_Does_Not_Exist()
    {
        var result = CreateResult("MyBenchmark", 500);
        var samples = GenerateSamples(500, 100);

        var violations = RegressionBaseline.Check(result, samples, "/nonexistent/baseline.json", 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("not found"));
    }

    [TestMethod]
    public void Check_Returns_Violation_When_Benchmark_Not_Found_In_Baseline()
    {
        var baselinePath = WriteBaselineJson("OtherBenchmark", 400);
        var result = CreateResult("MyBenchmark", 500);
        var samples = GenerateSamples(500, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("not found in baseline"));
    }

    [TestMethod]
    public void Check_Passes_When_Slowdown_Within_Ratio()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 400);
        var result = CreateResult("MyBenchmark", 440);
        var samples = GenerateSamples(440, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Check_Fails_When_Slowdown_Exceeds_Ratio()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 400);
        var result = CreateResult("MyBenchmark", 600);
        var samples = GenerateSamples(600, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("Regression detected"));
        Assert.IsTrue(violations[0].Contains("1.50x"));
    }

    [TestMethod]
    public void Check_Returns_Violation_When_Json_Is_Invalid()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not valid json");

        var result = CreateResult("MyBenchmark", 500);
        var samples = GenerateSamples(500, 100);
        var violations = RegressionBaseline.Check(result, samples, path, 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("Failed to parse"));

        File.Delete(path);
    }

    [TestMethod]
    public void Check_Fails_When_Baseline_Mean_Is_Zero_And_Current_Mean_Is_Positive()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 0);
        var result = CreateResult("MyBenchmark", 500);
        var samples = GenerateSamples(500, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("Regression detected"));
    }

    [TestMethod]
    public void Check_Passes_When_Baseline_And_Current_Mean_Are_Zero()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 0);
        var result = CreateResult("MyBenchmark", 0);
        var samples = GenerateSamples(0, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Check_Uses_Case_Insensitive_Name_Matching()
    {
        var baselinePath = WriteBaselineJson("mybenchmark", 400);
        var result = CreateResult("MyBenchmark", 420);
        var samples = GenerateSamples(420, 100);

        var violations = RegressionBaseline.Check(result, samples, baselinePath, 1.2);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Check_Returns_Violation_When_Current_Samples_Are_Empty()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 400);
        var result = CreateResult("MyBenchmark", 500);

        var violations = RegressionBaseline.Check(result, [], baselinePath, 1.2);

        Assert.AreEqual(1, violations.Count);
        Assert.IsTrue(violations[0].Contains("no raw samples"));
    }

    [TestMethod]
    public void Check_Passes_When_Significant_But_Ratio_Within_Gate()
    {
        var baselineSamples = GenerateSamples(400, 100);
        var baselinePath = WriteBaselineJson("MyBenchmark", 400, baselineSamples);
        var result = CreateResult("MyBenchmark", 420);
        var currentSamples = GenerateSamples(420, 100);

        var violations = RegressionBaseline.Check(result, currentSamples, baselinePath, 1.2);

        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public void Check_Passes_When_Noisy_But_Not_Significant()
    {
        var baselineSamples = GenerateSamples(400, 100);
        var baselinePath = WriteBaselineJson("MyBenchmark", 400, baselineSamples);
        var result = CreateResult("MyBenchmark", 600);
        var currentSamples = GenerateSamples(400, 100);

        var violations = RegressionBaseline.Check(result, currentSamples, baselinePath, 1.2);

        Assert.AreEqual(0, violations.Count);
    }

    private static string WriteBaselineJson(string name, double mean, double[]? samples = null)
    {
        var path = Path.GetTempFileName();

        var entry = new
        {
            name,
            mean,
            median = mean * 0.9,
            samples = samples ?? GenerateSamples(mean, 100),
        };

        var json = JsonSerializer.Serialize(new
        {
            results = new[] { entry },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        File.WriteAllText(path, json);
        return path;
    }

    private static double[] GenerateSamples(double mean, int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
            samples[i] = mean + (i % 10 - 5) * 0.05 * mean;
        return samples;
    }

    private static BenchmarkResult CreateResult(string name, double mean)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = mean,
            Median = mean * 0.9,
            Percentiles = [],
            Min = mean * 0.5,
            Max = mean * 2,
            StandardDeviation = mean * 0.1,
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
    }
}
