using System.Diagnostics;
using NBenchmark.Attributes;
using NBenchmark.Discovery;
using Xunit;

namespace NBenchmark.Tests.Discovery;

/// <summary>
///     A <c>ValueTask</c>-returning benchmark must be awaited, not boxed.
///     <para>
///         <c>ValueTask</c> is a struct and so is not assignable to <c>Task</c>. Discovery used to
///         test awaitability with <c>typeof(Task).IsAssignableFrom(returnType)</c>, which classified
///         an <c>async ValueTask</c> benchmark as synchronous and boxed the returned struct rather
///         than awaiting it - stopping the measurement at the body's first <c>await</c>. On a body
///         that delays 50 ms this reported 1 ms: not an error, just a confidently wrong number, in
///         the return type that is now idiomatic for hot paths.
///     </para>
/// </summary>
public sealed class ValueTaskDiscoveryTests
{
    private const int DelayMs = 50;

    /// <summary>The lower bound below which the delay was clearly never awaited.</summary>
    private const int AwaitedFloorMs = 25;

    private static BenchmarkMethodDefinition Single<T>()
        => new BenchmarkDiscoverer().Discover(typeof(T)).Single().Benchmarks.Single();

    [Fact]
    public async Task ValueTask_IsAwaited_NotBoxed()
    {
        var benchmark = Single<ValueTaskBenchmarks>();

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.Null(benchmark.SyncDelegate);

        var elapsed = await TimeAsync(benchmark, new ValueTaskBenchmarks());

        Assert.True(
            elapsed >= AwaitedFloorMs,
            $"a {DelayMs} ms body completed in {elapsed} ms, so the await was skipped");
    }

    [Fact]
    public async Task GenericValueTask_IsAwaited_NotBoxed()
    {
        var benchmark = Single<GenericValueTaskBenchmarks>();

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.Null(benchmark.SyncDelegate);

        var elapsed = await TimeAsync(benchmark, new GenericValueTaskBenchmarks());

        Assert.True(
            elapsed >= AwaitedFloorMs,
            $"a {DelayMs} ms body completed in {elapsed} ms, so the await was skipped");
    }

    /// <summary>
    ///     Parameterized <c>ValueTask</c> benchmarks take a different delegate-building path, so the
    ///     conversion has to be present on both.
    /// </summary>
    [Fact]
    public async Task ParameterizedValueTask_IsAwaited_NotBoxed()
    {
        var benchmark = new BenchmarkDiscoverer()
            .Discover(typeof(ParameterizedValueTaskBenchmarks))
            .Single()
            .Benchmarks
            .First();

        Assert.NotNull(benchmark.AsyncDelegate);

        var elapsed = await TimeAsync(benchmark, new ParameterizedValueTaskBenchmarks());

        Assert.True(
            elapsed >= AwaitedFloorMs,
            $"a {DelayMs} ms body completed in {elapsed} ms, so the await was skipped");
    }

    /// <summary>A plain <c>Task</c> benchmark keeps working, so the fix did not trade one for the other.</summary>
    [Fact]
    public async Task Task_StillAwaited()
    {
        var benchmark = Single<TaskBenchmarks>();

        Assert.NotNull(benchmark.AsyncDelegate);

        var elapsed = await TimeAsync(benchmark, new TaskBenchmarks());

        Assert.True(elapsed >= AwaitedFloorMs, $"a {DelayMs} ms body completed in {elapsed} ms");
    }

    /// <summary>A synchronous benchmark must not be pushed onto the async path by the widened check.</summary>
    [Fact]
    public void SyncMethod_StaysSynchronous()
    {
        var benchmark = Single<SyncBenchmarks>();

        Assert.Null(benchmark.AsyncDelegate);
        Assert.NotNull(benchmark.SyncDelegate);
    }

    private static async Task<long> TimeAsync(BenchmarkMethodDefinition benchmark, object instance)
    {
        var stopwatch = Stopwatch.StartNew();

        if (benchmark.AsyncDelegate is { } asyncDelegate)
            await asyncDelegate(instance);
        else
            benchmark.SyncDelegate!(instance);

        stopwatch.Stop();

        return stopwatch.ElapsedMilliseconds;
    }

    public class ValueTaskBenchmarks
    {
        [Benchmark]
        public async ValueTask DelayAsync() => await Task.Delay(DelayMs);
    }

    public class GenericValueTaskBenchmarks
    {
        [Benchmark]
        public async ValueTask<int> DelayAsync()
        {
            await Task.Delay(DelayMs);

            return 42;
        }
    }

    public class ParameterizedValueTaskBenchmarks
    {
        [Benchmark]
        [BenchmarkCase(DelayMs)]
        public async ValueTask DelayAsync(int ms) => await Task.Delay(ms);
    }

    public class TaskBenchmarks
    {
        [Benchmark]
        public async Task DelayAsync() => await Task.Delay(DelayMs);
    }

    public class SyncBenchmarks
    {
        [Benchmark]
        public int Compute() => 42;
    }
}
