using System.Text.Json;
using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class RegressionBaselineTests
{
    [Fact]
    public void Check_Returns_Violation_When_File_Does_Not_Exist()
    {
        var result = CreateResult("MyBenchmark", 500);

        var violations = RegressionBaseline.Check(result, "/nonexistent/baseline.json", 1.2);

        Assert.Single(violations);
        Assert.Contains("not found", violations[0]);
    }

    [Fact]
    public void Check_Returns_Violation_When_Benchmark_Not_Found_In_Baseline()
    {
        var baselinePath = WriteBaselineJson("OtherBenchmark", 400);
        var result = CreateResult("MyBenchmark", 500);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Single(violations);
        Assert.Contains("not found in baseline", violations[0]);
    }

    [Fact]
    public void Check_Passes_When_Slowdown_Within_Ratio()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 400);
        var result = CreateResult("MyBenchmark", 440);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Fails_When_Slowdown_Exceeds_Ratio()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 400);
        var result = CreateResult("MyBenchmark", 600);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Single(violations);
        Assert.Contains("Regression detected", violations[0]);
        Assert.Contains("1.50x", violations[0]);
    }

    [Fact]
    public void Check_Returns_Violation_When_Json_Is_Invalid()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not valid json");

        var result = CreateResult("MyBenchmark", 500);
        var violations = RegressionBaseline.Check(result, path, 1.2);

        Assert.Single(violations);
        Assert.Contains("Failed to parse", violations[0]);

        File.Delete(path);
    }

    [Fact]
    public void Check_Fails_When_Baseline_Mean_Is_Zero_And_Current_Mean_Is_Positive()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 0);
        var result = CreateResult("MyBenchmark", 500);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Single(violations);
        Assert.Contains("Regression detected", violations[0]);
    }

    [Fact]
    public void Check_Passes_When_Baseline_And_Current_Mean_Are_Zero()
    {
        var baselinePath = WriteBaselineJson("MyBenchmark", 0);
        var result = CreateResult("MyBenchmark", 0);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Uses_Case_Insensitive_Name_Matching()
    {
        var baselinePath = WriteBaselineJson("mybenchmark", 400);
        var result = CreateResult("MyBenchmark", 420);

        var violations = RegressionBaseline.Check(result, baselinePath, 1.2);

        Assert.Empty(violations);
    }

    private static string WriteBaselineJson(string name, double mean)
    {
        var path = Path.GetTempFileName();

        var json = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new { name, mean },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        File.WriteAllText(path, json);
        return path;
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
