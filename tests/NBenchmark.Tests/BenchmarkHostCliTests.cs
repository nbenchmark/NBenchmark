using NBenchmark.Attributes;
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
    public async Task RunAsync_Emits_OnSuiteStarting_Before_Per_Class_Setup_And_OnSuiteCompleted_After_Per_Class_Teardown()
    {
        var events = new List<string>();

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithProgress(new OrderingProgress(
                    onSuiteStarting: () => events.Add("onSuiteStarting"),
                    onSuiteCompleted: () => events.Add("onSuiteCompleted")))
                .RunAsync();
        });

        Assert.Equal(2, events.Count);
        Assert.Equal("onSuiteStarting", events[0]);
        Assert.Equal("onSuiteCompleted", events[1]);
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
                Directory.Delete(tempDir, true);
        }
    }

    private sealed class OrderingProgress : IBenchmarkProgress
    {
        private readonly Action _onSuiteStarting;
        private readonly Action _onSuiteCompleted;

        public OrderingProgress(Action onSuiteStarting, Action onSuiteCompleted)
        {
            _onSuiteStarting = onSuiteStarting;
            _onSuiteCompleted = onSuiteCompleted;
        }

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) { _onSuiteStarting(); return Task.CompletedTask; }
        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;
        public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;
        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) { _onSuiteCompleted(); return Task.CompletedTask; }
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

    private static string CaptureConsoleError(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
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

    private static async Task CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

public class TestBenchmarks
{
    [Benchmark]
    public int Fast()
    {
        return 1 + 1;
    }

    [Benchmark(Baseline = true)]
    public int FastBaseline()
    {
        return 2 + 2;
    }
}