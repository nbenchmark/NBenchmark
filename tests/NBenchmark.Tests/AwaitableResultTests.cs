using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     A <c>ValueTask</c>-returning body is refused rather than measured on the synchronous path.
/// </summary>
/// <remarks>
///     <para>
///         The parameterless path already refused these, with a wrap-it hint;
///         <see cref="ArgumentBinder.TryDelegateTypeFor" /> did not, so a
///         <c>Func&lt;TState, ValueTask&gt;</c> bound as <c>Func&lt;ValueTask&gt;</c> and
///         <c>DelegateDispatch</c> took the <b>synchronous</b> branch with <c>T = ValueTask</c>. The
///         task was never awaited: the benchmark measured the part of the body that ran before its
///         first incomplete await and reported that as the whole thing.
///     </para>
///     <para>
///         The host fallback had the same hole, so an isolated number and an in-process number agreed
///         with each other - on the wrong answer. Two paths agreeing is normally the evidence that
///         addressing worked, which is what made this one hard to see.
///     </para>
/// </remarks>
public sealed class AwaitableResultTests
{
    private static RunSpec Spec => new()
    {
        Options = MeasurementOptions.Default with { Samples = 1, WarmupSamples = 0, OpsPerSample = 1 },
        Progress = NullBenchmarkProgress.Instance,
    };

    /// <summary>
    ///     The engine's own synchronous entry point refuses an awaitable result type.
    /// </summary>
    /// <remarks>
    ///     Checked here rather than on each of the dozen <c>Add</c>/<c>Run</c> overloads that infer their
    ///     result type from a lambda, because this is the single funnel they all reach and one of them
    ///     would inevitably have been missed.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ValueTask))]
    [InlineData(typeof(Task))]
    public void Run_RefusesAnAwaitableResultType(Type resultType)
    {
        Assert.True(AwaitableResult.IsAwaitable(resultType));
        Assert.Contains("asynchronous entry point", AwaitableResult.Refusal("body", resultType));
    }

    [Fact]
    public void RunOfValueTask_Throws_RatherThanMeasuringTheSynchronousPrefix()
    {
        var error = Assert.Throws<ArgumentException>(
            () => BenchmarkRunner.Instance.Run("vt", static () => default(ValueTask), Spec));

        Assert.Contains("ValueTask", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOfTask_Throws_RatherThanMeasuringTheSynchronousPrefix()
    {
        var error = Assert.Throws<ArgumentException>(
            () => BenchmarkRunner.Instance.Run("t", static () => Task.CompletedTask, Spec));

        Assert.Contains("RunAsync", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A suite <c>Add</c> whose lambda returns a <c>ValueTask</c> errors the row instead of
    ///     reporting a number for the synchronous prefix.
    /// </summary>
    /// <remarks>
    ///     This is the shape a user actually writes: <c>Add("x", s =&gt; s.WorkAsync())</c> binds the
    ///     synchronous <c>Add&lt;TResult&gt;</c> overload with <c>TResult = ValueTask</c>, because
    ///     <c>Add</c> is a different method name and overload resolution has no reason to prefer it.
    /// </remarks>
    [Fact]
    public async Task Suite_AddOverAValueTaskBody_ErrorsRatherThanMeasuringNothing()
    {
        var results = await new BenchmarkSuite("vt")
            .WithIsolation(Isolation.Off)
            .WithSamples(1)
            .WithWarmupSamples(0)
            .Add("work", static () => default(ValueTask))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.True(result.Errored, "expected the row to error rather than report a synchronous prefix.");
        Assert.Contains("ValueTask", result.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The parameterized binder refuses it too, so the isolated path cannot reintroduce the hole.
    /// </summary>
    /// <remarks>
    ///     The two sides agreeing was the whole problem: this check exists in
    ///     <c>BodyResolver.TryDelegateType</c> for a parameterless body and was absent here, which is how
    ///     a prepared-state body reached the synchronous branch in the worker as well as in the host.
    /// </remarks>
    [Fact]
    public void ArgumentBinder_RefusesAValueTaskReturningParameterizedBody()
    {
        var body = static (int spins) => default(ValueTask);

        Assert.False(ArgumentBinder.TryDelegateTypeFor(body.Method, out _, out var error));
        Assert.Contains("ValueTask", error!, StringComparison.Ordinal);
        Assert.Contains("AsTask", error, StringComparison.Ordinal);
    }
}
