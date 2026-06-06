using NBenchmark.Attributes;
using NBenchmark.Discovery;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkDiscovererTests
{
    [Fact]
    public void Discovers_Public_Benchmark_Methods()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);

        var suite = suites.FirstOrDefault(s => s.Type == typeof(PublicBenchmarks));
        Assert.NotNull(suite);
        Assert.Equal(2, suite!.Benchmarks.Count);
    }

    [Fact]
    public void Discovers_Internal_Only_Benchmark_Class()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InternalBenchmarksMarker).Assembly);

        Assert.Contains(suites, s => s.Type == typeof(InternalBenchmarks));
    }

    [Fact]
    public void Caches_Delegates_For_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First();

        Assert.NotNull(benchmark.SyncDelegate);
    }

    [Fact]
    public void Caches_Sync_Delegate_For_Void_Returning_Method()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsNothing");

        Assert.NotNull(benchmark.SyncDelegate);
        var result = benchmark.SyncDelegate!(new PublicBenchmarks());
        Assert.Null(result);
    }

    [Fact]
    public void Caches_Sync_Delegate_For_Value_Returning_Method()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsInt");

        var result = benchmark.SyncDelegate!(new PublicBenchmarks());
        Assert.Equal(42, result);
    }

    [Fact]
    public void Discovers_Setup_And_Teardown_Delegates_Without_Throwing()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(LifecycleBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(LifecycleBenchmarks));

        Assert.NotNull(suite.SetupDelegate);
        Assert.NotNull(suite.TeardownDelegate);

        var benchmark = suite.Benchmarks.First();
        Assert.NotNull(benchmark.IterationSetupDelegate);
        Assert.NotNull(benchmark.IterationTeardownDelegate);

        var instance = new LifecycleBenchmarks();
        suite.SetupDelegate!(instance);
        benchmark.IterationSetupDelegate!(instance);
        benchmark.IterationTeardownDelegate!(instance);
        suite.TeardownDelegate!(instance);

        Assert.Equal(1, instance.SetupCount);
        Assert.Equal(1, instance.IterationSetupCount);
        Assert.Equal(1, instance.IterationTeardownCount);
        Assert.Equal(1, instance.TeardownCount);
    }

    [Fact]
    public async Task Caches_Async_Delegate_And_Result_Extractor()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(AsyncBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(AsyncBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsValueAsync");

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.NotNull(benchmark.ResultExtractor);

        var instance = new AsyncBenchmarks();
        var task = benchmark.AsyncDelegate!(instance);
        await task;
        Assert.Equal(7, benchmark.ResultExtractor!(task));
    }

    [Fact]
    public async Task Caches_Async_Delegate_For_NonGeneric_Task()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(AsyncBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(AsyncBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsTask");

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.Null(benchmark.ResultExtractor);

        await benchmark.AsyncDelegate!(new AsyncBenchmarks());
    }
}

public class PublicBenchmarks
{
    [Benchmark]
    public void ReturnsNothing() { }

    [Benchmark]
    public int ReturnsInt() => 42;
}

public class LifecycleBenchmarks
{
    public int SetupCount;
    public int TeardownCount;
    public int IterationSetupCount;
    public int IterationTeardownCount;

    [BenchmarkSetup]
    public void Setup() => SetupCount++;

    [BenchmarkTeardown]
    public void Teardown() => TeardownCount++;

    [BenchmarkIterationSetup]
    public void IterationSetup() => IterationSetupCount++;

    [BenchmarkIterationTeardown]
    public void IterationTeardown() => IterationTeardownCount++;

    [Benchmark]
    public int Work() => 1;
}

public class AsyncBenchmarks
{
    [Benchmark]
    public async Task<int> ReturnsValueAsync()
    {
        await Task.Yield();
        return 7;
    }

    [Benchmark]
    public Task ReturnsTask() => Task.CompletedTask;
}

internal class InternalBenchmarks
{
    [Benchmark]
    internal void Hidden() { }
}

internal static class InternalBenchmarksMarker { }
