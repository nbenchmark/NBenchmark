using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteExtendedTests
{
    [Fact]
    public async Task RunAsync_With_Async_Benchmarks()
    {
        var results = await new BenchmarkSuite("async-suite").WithIsolation(Isolation.Preferred)
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
        var results = await new BenchmarkSuite("generic-suite").WithIsolation(Isolation.Preferred)
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
        var results = await new BenchmarkSuite("generic-async-suite").WithIsolation(Isolation.Preferred)
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
        var results = await new BenchmarkSuite("memory-suite").WithIsolation(Isolation.Preferred)
            .Add("alloc", () => { _ = new byte[1024]; })
            .WithWarmup(1)
            .WithIterations(10)
            .WithAllocations()
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.NotNull(results[0].MeanAllocatedBytes);
        Assert.True(results[0].MeanAllocatedBytes >= 1024);
    }

    [Fact]
    public async Task RunAsync_With_ConfidenceLevel()
    {
        var results = await new BenchmarkSuite("confidence-suite").WithIsolation(Isolation.Preferred)
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

        var results = await new BenchmarkSuite("lifecycle").WithIsolation(Isolation.Preferred)
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

        var results = await new BenchmarkSuite("iter-lifecycle").WithIsolation(Isolation.Preferred)
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

        var results = await new BenchmarkSuite("reporter-suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(new StubReporter(r => { reported = true; }))
            .RunAsync();

        Assert.True(reported);
    }

    [Fact]
    public async Task RunAsync_With_Detail_Advanced_Propagates_To_Reporter()
    {
        var stub = new StubReporter(_ => { });

        var results = await new BenchmarkSuite("detail-suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => Thread.SpinWait(100))
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(stub)
            .WithDetail(ReportDetail.Advanced)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(ReportDetail.Advanced, stub.CapturedDetail);
    }

    [Fact]
    public async Task RunAsync_With_Detail_Set_Before_Reporter_Still_Propagates()
    {
        var stub = new StubReporter(_ => { });

        var results = await new BenchmarkSuite("detail-order-suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => Thread.SpinWait(100))
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .WithDetail(ReportDetail.Advanced)
            .WithReporter(stub)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(ReportDetail.Advanced, stub.CapturedDetail);
    }

    [Fact]
    public async Task RunAsync_With_Significance_Disabled()
    {
        var results = await new BenchmarkSuite("no-sig").WithIsolation(Isolation.Preferred)
            .Add("baseline", () => Thread.SpinWait(500))
            .Add("other", () => Thread.SpinWait(1000))
            .WithBaseline("baseline")
            .WithWarmup(1)
            .WithIterations(20)
            .WithSignificance(false)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Null(results[0].PValue);
        Assert.Equal(SignificanceVerdict.NotTested, results[0].SignificanceVerdict);
    }

    [Fact]
    public async Task RunAsync_With_Declaration_RunOrder_Preserves_Order()
    {
        var results = await new BenchmarkSuite("ordered").WithIsolation(Isolation.Preferred)
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

        public ReportDetail CapturedDetail => Detail;

        public string Name => "stub";
        public ReportDetail Detail { get; set; } = ReportDetail.Simple;

        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default)
        {
            _callback(results);
            return Task.CompletedTask;
        }
    }
}
