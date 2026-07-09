using NBenchmark.Attributes;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class ParametricHarnessIntegrationTests
{
    [Fact]
    public async Task RunAsync_Produces_One_Result_Per_BenchmarkCase()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.*", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(5, results.Count);

        Assert.Contains(results, r => r.Name == "ParametricHarnessBenchmarks.Compute(n=100)");
        Assert.Contains(results, r => r.Name == "ParametricHarnessBenchmarks.Compute(n=1000)");
    }

    [Fact]
    public async Task RunAsync_Produces_One_Result_Per_BenchmarkCases_Tuple()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.*", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Contains(results, r => r.Name == "ParametricHarnessBenchmarks.Multiply(a=2, b=3)");
        Assert.Contains(results, r => r.Name == "ParametricHarnessBenchmarks.Multiply(a=5, b=7)");
        Assert.Contains(results, r => r.Name == "ParametricHarnessBenchmarks.Multiply(a=10, b=20)");
    }

    [Fact]
    public async Task Filter_Matches_Argument_Values()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.Compute(n=100)", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("ParametricHarnessBenchmarks.Compute(n=100)", results[0].Name);
    }

    [Fact]
    public async Task Filter_Matches_Named_Tuple_Values()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.Multiply(a=10, b=20)", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("ParametricHarnessBenchmarks.Multiply(a=10, b=20)", results[0].Name);
    }

    [Fact]
    public async Task List_Shows_All_Parametric_Names()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.*", "--list"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Compute(n=100)", stdout);
        Assert.Contains("Compute(n=1000)", stdout);
        Assert.Contains("Multiply(a=2, b=3)", stdout);
        Assert.Contains("Multiply(a=5, b=7)", stdout);
        Assert.Contains("Multiply(a=10, b=20)", stdout);
    }

    [Fact]
    public async Task RunAsync_Populates_ParameterSet_On_Results()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "ParametricHarnessBenchmarks.*", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<ParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        var compute100 = results.First(r => r.Name == "ParametricHarnessBenchmarks.Compute(n=100)");
        Assert.Single(compute100.ParameterSet);
        Assert.Equal("n", compute100.ParameterSet[0].Name);
        Assert.Equal(100, compute100.ParameterSet[0].Value);

        var compute1000 = results.First(r => r.Name == "ParametricHarnessBenchmarks.Compute(n=1000)");
        Assert.Single(compute1000.ParameterSet);
        Assert.Equal("n", compute1000.ParameterSet[0].Name);
        Assert.Equal(1000, compute1000.ParameterSet[0].Value);

        var multiply = results.First(r => r.Name == "ParametricHarnessBenchmarks.Multiply(a=2, b=3)");
        Assert.Equal(2, multiply.ParameterSet.Count);
        Assert.Equal("a", multiply.ParameterSet[0].Name);
        Assert.Equal(2, multiply.ParameterSet[0].Value);
        Assert.Equal("b", multiply.ParameterSet[1].Name);
        Assert.Equal(3, multiply.ParameterSet[1].Value);
    }

    [Fact]
    public async Task RunAsync_All_Baseline_Cases_Have_IsBaseline_True()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "BaselineParametricHarnessBenchmarks.*", "--iterations", "5", "--warmup", "2", "--launch-count", "1"])
                .AddFromAssembly<BaselineParametricHarnessBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.IsBaseline));
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return sw.ToString();
    }

    private static async Task<T> CaptureConsoleOutputAsync<T>(Func<Task<T>> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            return await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

public class ParametricHarnessBenchmarks
{
    [BenchmarkCase(100)]
    [BenchmarkCase(1000)]
    [Benchmark]
    public int Compute(int n) => n;

    [BenchmarkCases(nameof(MultiplyCases))]
    [Benchmark]
    public int Multiply(int a, int b) => a * b;

    public static IEnumerable<(int a, int b)> MultiplyCases()
    {
        yield return (2, 3);
        yield return (5, 7);
        yield return (10, 20);
    }
}

public class BaselineParametricHarnessBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [Benchmark(Baseline = true)]
    public int Compute(int n) => n;

    [BenchmarkCases(nameof(Sizes))]
    [Benchmark(Baseline = true)]
    public int Multiply(int a, int b) => a * b;

    public static IEnumerable<(int a, int b)> Sizes()
    {
        yield return (2, 3);
        yield return (5, 7);
    }
}
