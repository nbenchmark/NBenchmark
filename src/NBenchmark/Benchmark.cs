using System.Runtime.CompilerServices;
using NBenchmark.Engine;

namespace NBenchmark;

/// <summary>
///     Quick mode entry point: measure a single piece of code. The four overloads
///     are thin adapters on top of <see cref="BenchmarkRunner" />.
/// </summary>
public static class Benchmark
{
    public static BenchmarkResult Run(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static BenchmarkResult Run<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static async Task<BenchmarkResult> RunAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static MeasurementOutcome RunRaw(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static MeasurementOutcome RunRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    /// <summary>
    ///     Runs a quick benchmark in a fresh child process, avoiding cross-contamination
    ///     from the current process runtime state.
    /// </summary>
    /// <remarks>
    ///     Child replay targets this invocation using caller metadata and isolated-call
    ///     invocation order. Reordering isolated calls in a startup path can change which
    ///     invocation is replayed in the child process.
    ///     <para>
    ///         If multiple <c>RunIsolated*</c> callsites execute on the same child startup
    ///         path, only the requested invocation runs as the isolated target. Non-target
    ///         callsites still execute in-process in that child CLR and can influence that
    ///         child's runtime state.
    ///     </para>
    ///     <para>
    ///         This synchronous overload blocks the calling thread until child completion.
    ///     </para>
    /// </remarks>
    public static BenchmarkResult RunIsolated(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => RunIsolatedCoreAsync(
                name,
                options,
                progress,
                cancellationToken,
                callerFilePath,
                callerLineNumber,
                callerMemberName,
                (resolvedOptions, resolvedProgress, ct) => Task.FromResult(
                    RunRaw(action, resolvedOptions, name, resolvedProgress, ct)),
                (resolvedOptions, resolvedProgress, ct) => Task.FromResult(
                    Run(action, resolvedOptions, name, resolvedProgress, ct)))
            .GetAwaiter()
            .GetResult();

    /// <summary>
    ///     Runs a value-returning quick benchmark in a fresh child process, avoiding
    ///     cross-contamination from the current process runtime state.
    /// </summary>
    public static BenchmarkResult RunIsolated<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => RunIsolatedCoreAsync(
                name,
                options,
                progress,
                cancellationToken,
                callerFilePath,
                callerLineNumber,
                callerMemberName,
                (resolvedOptions, resolvedProgress, ct) => Task.FromResult(
                    RunRaw(action, resolvedOptions, name, resolvedProgress, ct)),
                (resolvedOptions, resolvedProgress, ct) => Task.FromResult(
                    Run(action, resolvedOptions, name, resolvedProgress, ct)))
            .GetAwaiter()
            .GetResult();

    /// <summary>
    ///     Runs an async quick benchmark in a fresh child process, avoiding
    ///     cross-contamination from the current process runtime state.
    /// </summary>
    public static Task<BenchmarkResult> RunIsolatedAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => RunIsolatedCoreAsync(
            name,
            options,
            progress,
            cancellationToken,
            callerFilePath,
            callerLineNumber,
            callerMemberName,
            (resolvedOptions, resolvedProgress, ct) =>
                RunRawAsync(action, resolvedOptions, name, resolvedProgress, ct),
            (resolvedOptions, resolvedProgress, ct) =>
                RunAsync(action, resolvedOptions, name, resolvedProgress, ct));

    /// <summary>
    ///     Runs an async value-returning quick benchmark in a fresh child process,
    ///     avoiding cross-contamination from the current process runtime state.
    /// </summary>
    public static Task<BenchmarkResult> RunIsolatedAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => RunIsolatedCoreAsync(
            name,
            options,
            progress,
            cancellationToken,
            callerFilePath,
            callerLineNumber,
            callerMemberName,
            (resolvedOptions, resolvedProgress, ct) =>
                RunRawAsync(action, resolvedOptions, name, resolvedProgress, ct),
            (resolvedOptions, resolvedProgress, ct) =>
                RunAsync(action, resolvedOptions, name, resolvedProgress, ct));

    private static Task<BenchmarkResult> RunIsolatedCoreAsync(
        string name,
        MeasurementOptions? options,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        Func<MeasurementOptions, IBenchmarkProgress, CancellationToken, Task<MeasurementOutcome>> runRawAsync,
        Func<MeasurementOptions, IBenchmarkProgress, CancellationToken, Task<BenchmarkResult>> runInProcessAsync)
        => IsolatedRunContext.WithCurrentRequestAsync(async () =>
        {
            var resolvedProgress = progress ?? NullBenchmarkProgress.Instance;
            var resolvedOptions = IsolatedRunContext.ResolveOptions(options ?? MeasurementOptions.Default);
            var invocationOrdinal = IsolatedRunContext.NextInvocationOrdinal(IsolatedRunMode.Quick);

            if (IsolatedRunContext.IsRequestMatch(
                    IsolatedRunMode.Quick,
                    invocationOrdinal,
                    callerFilePath,
                    callerLineNumber,
                    callerMemberName,
                    name))
            {
                var outcome = await runRawAsync(resolvedOptions, resolvedProgress, cancellationToken)
                    .ConfigureAwait(false);

                await IsolatedRunContext.WriteChildOutcomeIfRequestedAsync(outcome, cancellationToken)
                    .ConfigureAwait(false);

                return outcome.Result;
            }

            if (IsolatedRunContext.IsActive)
            {
                if (IsolatedRunContext.IsRequestedInvocation(IsolatedRunMode.Quick, invocationOrdinal)
                    && IsolatedRunContext.TryGetActiveRequest(out var request))
                {
                    var mismatch = IsolatedRunContext.BuildCallsiteMismatchOutcome(
                        name,
                        resolvedOptions,
                        request,
                        callerFilePath,
                        callerLineNumber,
                        callerMemberName,
                        invocationOrdinal);

                    await IsolatedRunContext.WriteChildOutcomeIfRequestedAsync(mismatch, cancellationToken)
                        .ConfigureAwait(false);

                    return mismatch.Result;
                }

                return await runInProcessAsync(resolvedOptions, resolvedProgress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var requestPayload = new IsolatedRunRequest
            {
                Mode = IsolatedRunMode.Quick,
                InvocationOrdinal = invocationOrdinal,
                CallerFilePath = callerFilePath,
                CallerLineNumber = callerLineNumber,
                CallerMemberName = callerMemberName,
                BenchmarkName = name,
                Options = resolvedOptions,
            };

            var isolatedOutcome = await IsolatedRunContext
                .RunInIsolatedProcessAsync(requestPayload, cancellationToken)
                .ConfigureAwait(false);

            return isolatedOutcome.Result;
        });
}
