using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkHostCliTests
{
    [Fact]
    public void Help_Flag_Returns_Empty_And_Prints_Help()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            var host = BenchmarkHost.Create(["--help"]);
            host.RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    public void Unknown_Flag_Sets_ExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;
        try
        {
            CaptureConsoleOutput(() =>
            {
                var host = BenchmarkHost.Create(["--bogus-flag"]);
                host.RunAsync().GetAwaiter().GetResult();
            });

            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Threshold_Pct_Sets_ExitCode_And_Prints_Not_Implemented()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;
        try
        {
            var stderr = CaptureConsoleError(() =>
            {
                var host = BenchmarkHost.Create(["--threshold-pct", "5"]);
                host.RunAsync().GetAwaiter().GetResult();
            });

            Assert.Contains("not yet implemented", stderr);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public async Task RunAsync_Discovers_And_Executes_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
    }

    [Fact]
    public async Task RunAsync_Applies_Output_Directory_To_File_Reporters()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-{Guid.NewGuid():N}");
        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .RunAsync();
            });

            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(new JsonReporter(tempDir))
                    .RunAsync();
            });

            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    private static string CaptureConsoleError(Action action)
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try { action(); }
        finally { Console.SetError(original); }
        return sw.ToString();
    }

    private static async Task<T> CaptureConsoleOutputAsync<T>(Func<Task<T>> action)
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try { return await action(); }
        finally { Console.SetOut(original); }
    }

    private static async Task CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try { await action(); }
        finally { Console.SetOut(original); }
    }
}

public class TestBenchmarks
{
    [Benchmark]
    public int Fast() => 1 + 1;

    [Benchmark(Baseline = true)]
    public int FastBaseline() => 2 + 2;
}
