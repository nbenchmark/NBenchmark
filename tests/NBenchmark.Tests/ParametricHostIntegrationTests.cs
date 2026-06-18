using NBenchmark.Attributes;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class ParametricHostIntegrationTests
{
    [Fact]
    public async Task RunAsync_Produces_One_Result_Per_BenchmarkCase()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "ParametricHostBenchmarks.*", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<ParametricHostBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(5, results.Count);

        Assert.Contains(results, r => r.Name == "ParametricHostBenchmarks.Compute(100)");
        Assert.Contains(results, r => r.Name == "ParametricHostBenchmarks.Compute(1000)");
    }

    [Fact]
    public async Task RunAsync_Produces_One_Result_Per_BenchmarkCases_Tuple()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "ParametricHostBenchmarks.*", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<ParametricHostBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Contains(results, r => r.Name == "ParametricHostBenchmarks.Multiply(a=2, b=3)");
        Assert.Contains(results, r => r.Name == "ParametricHostBenchmarks.Multiply(a=5, b=7)");
        Assert.Contains(results, r => r.Name == "ParametricHostBenchmarks.Multiply(a=10, b=20)");
    }

    [Fact]
    public async Task Filter_Matches_Argument_Values()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "ParametricHostBenchmarks.Compute(100)", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<ParametricHostBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("ParametricHostBenchmarks.Compute(100)", results[0].Name);
    }

    [Fact]
    public async Task Filter_Matches_Named_Tuple_Values()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "ParametricHostBenchmarks.Multiply(a=10, b=20)", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<ParametricHostBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("ParametricHostBenchmarks.Multiply(a=10, b=20)", results[0].Name);
    }

    [Fact]
    public async Task List_Shows_All_Parametric_Names()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHost.Create(["--filter", "ParametricHostBenchmarks.*", "--list"])
                .AddFromAssembly<ParametricHostBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Compute(100)", stdout);
        Assert.Contains("Compute(1000)", stdout);
        Assert.Contains("Multiply(a=2, b=3)", stdout);
        Assert.Contains("Multiply(a=5, b=7)", stdout);
        Assert.Contains("Multiply(a=10, b=20)", stdout);
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

public class ParametricHostBenchmarks
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
