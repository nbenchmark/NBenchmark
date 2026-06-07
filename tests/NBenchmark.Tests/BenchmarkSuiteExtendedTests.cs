using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteExtendedTests
{
    [Fact]
    public async Task RunAsync_With_Async_Benchmarks()
    {
        var results = await new BenchmarkSuite("async-suite")
            .Add("async-fast", async () => { await Task.Yield(); })
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.False(results[0].Errored);
    }

    [Fact]
    public async Task RunAsync_With_Generic_Func_Benchmark()
    {
        var results = await new BenchmarkSuite("generic-suite")
            .Add("returns-value", () => 42)
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.False(results[0].Errored);
    }

    [Fact]
    public async Task RunAsync_With_Generic_Async_Benchmark()
    {
        var results = await new BenchmarkSuite("generic-async-suite")
            .Add("async-returns-value", async () =>
            {
                await Task.Yield();
                return 42;
            })
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.False(results[0].Errored);
    }

    [Fact]
    public async Task RunAsync_With_Memory_Enabled()
    {
        var results = await new BenchmarkSuite("memory-suite")
            .Add("alloc", () => { _ = new byte[1024]; })
            .WithWarmup(1)
            .WithIterations(10)
            .WithMemory()
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.NotNull(results[0].MeanAllocatedBytes);
        Assert.True(results[0].MeanAllocatedBytes >= 1024);
    }

    [Fact]
    public async Task RunAsync_With_ConfidenceLevel()
    {
        var results = await new BenchmarkSuite("confidence-suite")
            .Add("a", () => Thread.SpinWait(100))
            .WithWarmup(1)
            .WithIterations(10)
            .WithConfidenceLevel(0.99)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(0.99, results[0].ConfidenceLevel);
    }

    [Fact]
    public async Task RunAsync_With_Suite_Setup_And_Teardown()
    {
        var setupCount = 0;
        var teardownCount = 0;

        var results = await new BenchmarkSuite("lifecycle")
            .Add("work", () => Thread.SpinWait(100))
            .WithSuiteSetup(() => Interlocked.Increment(ref setupCount))
            .WithSuiteTeardown(() => Interlocked.Increment(ref teardownCount))
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(1, setupCount);
        Assert.Equal(1, teardownCount);
    }

    [Fact]
    public async Task RunAsync_With_Iteration_Setup_And_Teardown()
    {
        var setupCount = 0;
        var teardownCount = 0;

        var results = await new BenchmarkSuite("iter-lifecycle")
            .Add("work", () => Thread.SpinWait(100),
                () => Interlocked.Increment(ref setupCount),
                () => Interlocked.Increment(ref teardownCount))
            .WithWarmup(2)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(7, setupCount);
        Assert.Equal(7, teardownCount);
    }

    [Fact]
    public async Task RunAsync_With_Reporter_Invokes_Reporter()
    {
        var reported = false;

        var results = await new BenchmarkSuite("reporter-suite")
            .Add("a", () => { })
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(new StubReporter(r => { reported = true; }))
            .RunAsync();

        Assert.True(reported);
    }

    [Fact]
    public async Task RunAsync_With_Significance_Disabled()
    {
        var results = await new BenchmarkSuite("no-sig")
            .Add("baseline", () => Thread.SpinWait(500))
            .Add("other", () => Thread.SpinWait(1000))
            .WithBaseline("baseline")
            .WithWarmup(1)
            .WithIterations(20)
            .WithSignificance(false)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Null(results[0].PValue);
        Assert.Null(results[0].IsSignificant);
    }

    [Fact]
    public async Task RunAsync_With_Declaration_RunOrder_Preserves_Order()
    {
        var results = await new BenchmarkSuite("ordered")
            .Add("first", () => Thread.SpinWait(100))
            .Add("second", () => Thread.SpinWait(100))
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal("first", results[0].Name);
        Assert.Equal("second", results[1].Name);
    }

    private class StubReporter : IReporter
    {
        private readonly Action<IReadOnlyList<BenchmarkResult>> _callback;

        public StubReporter(Action<IReadOnlyList<BenchmarkResult>> callback)
        {
            _callback = callback;
        }

        public string Name => "stub";

        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default)
        {
            _callback(results);
            return Task.CompletedTask;
        }
    }
}