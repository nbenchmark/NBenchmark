using NBenchmark.Discovery;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class HarnessSelectionTests
{
    private const string TestBenchmarksFullName = "NBenchmark.Tests.TestBenchmarks";

    [Fact]
    public async Task Create_Parameterless_With_Typed_Builders_Runs_Filtered_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create()
                .AddFromAssembly<TestBenchmarks>()
                .WithFilter("TestBenchmarks.*")
                .WithIterations(1)
                .WithWarmup(0)
                .WithOutlierMode(OutlierMode.None)
                .WithConfidenceLevel(0.90)
                .WithLaunchCount(1)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));

        // The typed builders write through to the measurement options: the reported interval
        // uses the confidence level set programmatically rather than the 0.95 default.
        Assert.All(results, r => Assert.Equal(0.90, r.ConfidenceLevel, 3));
    }

    [Fact]
    public async Task WithSelection_Restricts_Run_To_Selected_Benchmark()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create()
                .AddFromAssembly<TestBenchmarks>()
                .WithFilter("TestBenchmarks.*")
                .WithSelection([new BenchmarkSelection(TestBenchmarksFullName, "Fast")])
                .WithIterations(1)
                .WithWarmup(0)
                .WithLaunchCount(1)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync());

        Assert.Single(results);
        Assert.Equal("TestBenchmarks.Fast", results[0].Name);
    }

    [Fact]
    public async Task WithSelection_Without_Filter_Does_Not_Force_Multi_Runtime()
    {
        // The test assembly contains classes decorated with [Runtimes]. Selecting a benchmark
        // that declares no runtimes must narrow the runtime aggregation too, so the run stays
        // in-process instead of being forced cross-runtime by the unrelated discovered classes.
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create()
                .AddFromAssembly<TestBenchmarks>()
                .WithSelection([new BenchmarkSelection(TestBenchmarksFullName, "Fast")])
                .WithIterations(1)
                .WithWarmup(0)
                .WithLaunchCount(1)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync());

        Assert.Single(results);
        Assert.Equal("TestBenchmarks.Fast", results[0].Name);

        // An empty RuntimeMoniker confirms the result came from the in-process runner rather
        // than a cross-runtime child (which stamps the target framework).
        Assert.Empty(results[0].RuntimeMoniker);
    }

    [Fact]
    public async Task WithSelection_Unknown_Identity_Reports_Validation_Error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaptureConsoleOutputAsync(async () =>
                await BenchmarkHarness.Create()
                    .AddFromAssembly<TestBenchmarks>()
                    .WithFilter("TestBenchmarks.*")
                    .WithSelection([new BenchmarkSelection(TestBenchmarksFullName, "DoesNotExist")])
                    .WithIterations(1)
                    .WithWarmup(0)
                    .WithLaunchCount(1)
                    .WithIsolation(false)
                    .RunAsync()));

        Assert.Contains("WithSelection requested", ex.Message);
        Assert.Contains("DoesNotExist", ex.Message);
    }

    [Fact]
    public void WithSelection_Blank_Identity_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            BenchmarkHarness.Create()
                .WithSelection([new BenchmarkSelection(" ", "Fast")]));
    }

    [Fact]
    public async Task WithFilter_Alone_Restricts_Discovered_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create()
                .AddFromAssembly<TestBenchmarks>()
                .WithFilter("TestBenchmarks.Fast")
                .WithIterations(1)
                .WithWarmup(0)
                .WithLaunchCount(1)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync());

        Assert.Single(results);
        Assert.Equal("TestBenchmarks.Fast", results[0].Name);
    }

    [Fact]
    public void Discovery_Exposes_Public_Isolation_Intent_And_Runtimes()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(TestBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(TestBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(BenchmarkIsolationIntent.HarnessDefault, b.Isolation));
        Assert.Empty(suite.Runtimes);
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
