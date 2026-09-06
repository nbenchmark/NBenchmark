using System.Diagnostics;
using NBenchmark;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Discovery;

/// <summary>
///     A discovered benchmark is measured through a delegate carrying its own signature, so nothing
///     the harness does to reach the body shows up in the reading.
/// </summary>
/// <remarks>
///     <para>
///         Discovery used to hand the engine a <c>Func&lt;object, object?&gt;</c>. Every
///         value-returning <c>[Benchmark]</c> method therefore boxed its result once per operation,
///         and the report attributed those 24 bytes to the user: <c>samples/Harness</c> declares four
///         bodies that allocate nothing and every one of them reported <c>allocatedBytesMedian = 24</c>.
///     </para>
///     <para>
///         The assertions here are deliberately allocation-first rather than time-first. Allocation
///         is deterministic and a box is exactly 24 bytes, so it can be asserted flatly; the
///         nanoseconds the same change saves cannot be, because an in-process reading on a
///         nanosecond body is bimodal between tiering states and would make the test a coin flip.
///         The one timing assertion here is a floor, not a budget - see
///         <see cref="A_Known_Cost_Body_Is_Not_Optimized_Away" />.
///     </para>
/// </remarks>
public sealed class MonomorphicDispatchTests
{
    /// <summary>
    ///     A return type used by nothing else, so the JIT-elision sink it lands in is not shared with
    ///     another test running in parallel.
    /// </summary>
    public readonly record struct Token(long Value);

    public readonly record struct AsyncToken(long Value);

    private const long Expected = 0x5A5A_0F0F;

    [Fact]
    public async Task Value_Returning_Benchmark_Allocates_Nothing()
    {
        var outcome = await MeasureAsync<StructReturningBenchmarks>();

        Assert.False(outcome.Result.Errored);
        Assert.Equal(0, outcome.Result.AllocatedBytesMedian);
    }

    /// <summary>
    ///     A parameterized benchmark binds its arguments through compiled code rather than a directly
    ///     bound delegate, and that path carried its own <c>Convert(call, typeof(object))</c>.
    /// </summary>
    [Fact]
    public async Task Parameterized_Value_Returning_Benchmark_Allocates_Nothing()
    {
        var outcome = await MeasureAsync<ParameterizedStructBenchmarks>();

        Assert.False(outcome.Result.Errored);
        Assert.Equal(0, outcome.Result.AllocatedBytesMedian);
    }

    /// <summary>
    ///     The control for the two tests above: the same measurement, on a body that really does box,
    ///     has to report the box. Without this, a broken allocation meter would make them pass.
    /// </summary>
    [Fact]
    public void The_Allocation_Meter_Sees_A_Box_When_There_Is_One()
    {
        var seed = 41;

        // Read from a local rather than a constant so the box cannot be folded away.
        var outcome = BenchmarkRunner.Instance.Run<object>(
            "boxing-control", () => seed + 1, Spec());

        Assert.Equal(24, outcome.Result.AllocatedBytesMedian);
    }

    /// <summary>
    ///     The value has to reach the sink, or the JIT is free to delete the call that produced it and
    ///     the benchmark measures an empty loop while reporting a plausible number.
    /// </summary>
    [Fact]
    public async Task Value_Returning_Benchmark_Result_Reaches_The_Elision_Sink()
    {
        await MeasureAsync<StructReturningBenchmarks>();

        Assert.Equal(new Token(Expected), BenchmarkRunner.LastConsumed<Token>());
    }

    [Fact]
    public async Task Async_Value_Returning_Benchmark_Result_Reaches_The_Elision_Sink()
    {
        await MeasureAsync<AsyncStructBenchmarks>();

        Assert.Equal(new AsyncToken(Expected), BenchmarkRunner.LastConsumed<AsyncToken>());
    }

    /// <summary>
    ///     <c>Func&lt;T&gt;</c> is covariant in <c>T</c>, so a <c>Func&lt;Task&lt;T&gt;&gt;</c>
    ///     <i>is</i> a <c>Func&lt;Task&gt;</c>. Dispatching on that match awaits the body and drops
    ///     its result on the floor - the value never reaches the sink, and an async body computing a
    ///     value nobody reads is a body the optimizer may shorten.
    /// </summary>
    [Fact]
    public async Task An_Async_Generic_Body_Is_Not_Dispatched_As_A_Plain_Task_Body()
    {
        var token = new AsyncToken(Expected + 1);

        await DelegateDispatch.MeasureAsync(
            "async-generic", (Func<Task<AsyncToken>>)(() => Task.FromResult(token)), Spec(), CancellationToken.None);

        Assert.Equal(token, BenchmarkRunner.LastConsumed<AsyncToken>());
    }

    /// <summary>
    ///     The guard the pivot's risk list asks for: a body of known cost has to measure roughly that
    ///     cost. Over-optimizing the dispatch path is not a free win - in probing, a trivial body
    ///     reached through a monomorphic typed delegate measured 0.33 ns/op because the JIT had
    ///     devirtualized and then deleted it, with nothing in the output to say so.
    /// </summary>
    /// <remarks>
    ///     The floor is the real assertion. The ceiling is deliberately loose: a spin loop on a shared
    ///     CI runner can be preempted, and a flaky ceiling would get this test deleted, which would
    ///     cost more than the ceiling is worth.
    /// </remarks>
    [Fact]
    public async Task A_Known_Cost_Body_Is_Not_Optimized_Away()
    {
        var outcome = await MeasureAsync<KnownCostBenchmarks>();

        Assert.False(outcome.Result.Errored);

        Assert.InRange(
            outcome.Result.MedianNs,
            KnownCostBenchmarks.TargetNanoseconds * 0.7,
            KnownCostBenchmarks.TargetNanoseconds * 4.0);
    }

    /// <summary>
    ///     A body reached through <see cref="ArgumentBinder" /> - a parameter sweep's value, or prepared
    ///     state - allocates nothing per operation and still reaches the elision sink.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Binding wraps the body as <c>() =&gt; body(argument)</c>. That closure is created once,
    ///         before warmup, and boxing the argument to pass it through reflection happens there too -
    ///         so neither belongs to a reading. This asserts that: had the binder instead adapted through
    ///         a <c>Func&lt;object&gt;</c>, or rebuilt the wrapper per invocation, the box would land
    ///         inside the loop and show up here as 24 bytes.
    ///     </para>
    ///     <para>
    ///         The shape is identical to what an in-process parameterized suite has always measured, which
    ///         is the property that makes an isolated reading comparable with a host one at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task An_Argument_Bound_Body_Allocates_Nothing_And_Reaches_The_Sink()
    {
        var expected = new Token(Expected + 7);

        Assert.True(ArgumentBinder.TryBind(
            (Func<long, Token>)(value => new Token(value)),
            [expected.Value],
            out var bound,
            out var error));

        Assert.Null(error);

        var outcome = await DelegateDispatch.MeasureAsync(
            "argument-bound", bound, Spec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored, outcome.Result.ErrorMessage);
        Assert.Equal(0, outcome.Result.AllocatedBytesMedian);

        // The bound argument really reached the body, and the body's result really reached the sink.
        Assert.Equal(expected, BenchmarkRunner.LastConsumed<Token>());
    }

    private static async Task<MeasurementOutcome> MeasureAsync<T>() where T : new()
    {
        var suite = new BenchmarkDiscoverer().Discover(typeof(T)).Single();
        var envelope = BenchmarkEnvelope.FromDiscovered(suite.Benchmarks.First(), typeof(T).Name, () => new T());

        return await envelope.RunAsync(Spec(), CancellationToken.None);
    }

    /// <summary>
    ///     One operation per sample, so a per-operation box is reported as 24 bytes rather than
    ///     divided by an auto-calibrated batch size.
    /// </summary>
    private static RunSpec Spec() => new()
    {
        Options = new MeasurementOptions
        {
            Samples = 20,
            WarmupSamples = 2,
            OpsPerSample = 1,
            OutlierMode = OutlierMode.None,
        },
    };

    public sealed class StructReturningBenchmarks
    {
        [Benchmark]
        public Token Compute() => new(Expected);
    }

    public sealed class ParameterizedStructBenchmarks
    {
        [Benchmark]
        [Arguments(Expected)]
        public Token Compute(long value) => new(value);
    }

    public sealed class AsyncStructBenchmarks
    {
        [Benchmark]
        public Task<AsyncToken> ComputeAsync() => Task.FromResult(new AsyncToken(Expected));
    }

    public sealed class KnownCostBenchmarks
    {
        public const double TargetNanoseconds = 200_000.0;

        [Benchmark]
        public long Spin()
        {
            var ticks = (long)(TargetNanoseconds * Stopwatch.Frequency / 1_000_000_000.0);
            var start = Stopwatch.GetTimestamp();
            long spins = 0;

            while (Stopwatch.GetTimestamp() - start < ticks)
            {
                spins++;
            }

            return spins;
        }
    }
}
