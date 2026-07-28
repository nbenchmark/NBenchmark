using NBenchmark.Engine;
using NBenchmark.Workers;

namespace NBenchmark;

/// <summary>
///     Single mode entry point: measure a single piece of code.
///     <para>
///         Bodies are measured in a dedicated worker process by default, because JIT tiering,
///         dynamic PGO, ReadyToRun and GC flavour are fixed when a process starts and can only be
///         chosen for a process that has not started yet. A body that cannot be addressed across
///         that boundary - most often because it captures a local - is measured here instead, said
///         so on stderr, and stamped
///         <see cref="IsolationStatus.InProcessCapturedState" /> on the result. Isolation is never
///         faked and captured state is never reconstructed: doing so was measured to return
///         plausible, silently wrong numbers.
///     </para>
///     <para>
///         All eight overloads keep their original signatures, including the synchronous return of
///         <see cref="Run(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />.
///         Use the <c>RunInProcess</c> family when measuring the current process is the point -
///         cold-start cost, or a body that must observe host state.
///     </para>
/// </summary>
public static class Benchmark
{
    public static BenchmarkResult Run(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(action, options, name, progress, cancellationToken).Result;

    public static BenchmarkResult Run<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(action, options, name, progress, cancellationToken).Result;

    public static async Task<BenchmarkResult> RunAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(action, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    public static async Task<BenchmarkResult> RunAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(action, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    public static MeasurementOutcome RunRaw(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Measure(action, options, name, progress, cancellationToken);

    public static MeasurementOutcome RunRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Measure(action, options, name, progress, cancellationToken);

    public static Task<MeasurementOutcome> RunRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureAsync(action, options, name, progress, cancellationToken);

    public static Task<MeasurementOutcome> RunRawAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureAsync(action, options, name, progress, cancellationToken);

    /// <summary>
    ///     Measures in <b>this</b> process, deliberately and without a warning.
    ///     <para>
    ///         This is the right choice - not a fallback - when the current process is the subject:
    ///         cold-start and first-call cost, a body that must observe host state such as a warm
    ///         cache or an open connection, or a comparison against a number produced before workers
    ///         existed. The result is stamped <see cref="IsolationStatus.InProcessRequested" /> and
    ///         reports the host's runtime configuration, so it is never silently compared against an
    ///         isolated measurement.
    ///     </para>
    /// </summary>
    public static BenchmarkResult RunInProcess(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHere(action, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult RunInProcess<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHere(action, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunInProcessAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = SpecFor(options, progress);
        EmitBuildConfigurationGuidanceOnce(options);

        var outcome = await BenchmarkRunner.Instance
            .RunAsync(name, action, spec, cancellationToken)
            .ConfigureAwait(false);

        return Stamp(outcome, IsolationStatus.InProcessRequested).Result;
    }

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunInProcessAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = SpecFor(options, progress);
        EmitBuildConfigurationGuidanceOnce(options);

        var outcome = await BenchmarkRunner.Instance
            .RunAsync(name, action, spec, cancellationToken)
            .ConfigureAwait(false);

        return Stamp(outcome, IsolationStatus.InProcessRequested).Result;
    }

    /// <summary>
    ///     Starts a measurement worker in the background so the first
    ///     <see cref="Run(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    ///     does not pay for the launch.
    ///     <para>
    ///         Optional. A worker costs roughly 70 ms to start against a per-benchmark floor of about
    ///         600 ms, so this is worth calling only when that first launch would land somewhere
    ///         visible - at the start of a script, or before a timed section of a tool.
    ///     </para>
    /// </summary>
    public static void Warmup(MeasurementOptions? options = null)
    {
        var profile = (options ?? MeasurementOptions.Default).RuntimeProfile;

        // Fire and forget by design: the caller asked to hide a cost, not to wait for it. A failure
        // here is not worth reporting, because the run that needs the worker will report it far
        // better, with the context of what it was trying to measure.
        _ = Task.Run(async () =>
        {
            try
            {
                await WorkerPrewarm.PrimeAsync(profile).ConfigureAwait(false);
            }
            catch
            {
                // See above.
            }
        });
    }

    private static MeasurementOutcome Measure<TDelegate>(
        TDelegate action,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TDelegate : Delegate
        // Simple mode has always returned synchronously and continues to. The wait is on a worker
        // handshake rather than on measurement scheduling, but from the caller's side nothing about
        // the contract changed.
        => MeasureAsync(action, options, name, progress, cancellationToken).GetAwaiter().GetResult();

    private static async Task<MeasurementOutcome> MeasureAsync<TDelegate>(
        TDelegate action,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(action);

        var effective = options ?? MeasurementOptions.Default;
        EmitBuildConfigurationGuidanceOnce(options);

        var (outcome, status) = await SingleBodyRunner.RunAsync(
                name,
                action,
                effective,
                progress ?? NullBenchmarkProgress.Instance,
                () => MeasureHereAsync(action, effective, name, progress, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        return Stamp(outcome, status);
    }

    private static MeasurementOutcome MeasureHere<TDelegate>(
        TDelegate action,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TDelegate : Delegate
    {
        EmitBuildConfigurationGuidanceOnce(options);

        var outcome = MeasureHereAsync(action, options ?? MeasurementOptions.Default, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

        return Stamp(outcome, IsolationStatus.InProcessRequested);
    }

    /// <summary>
    ///     Runs the body through the engine in this process, selecting the overload that matches the
    ///     delegate's real shape so a value-returning body is never boxed on its way in.
    /// </summary>
    private static Task<MeasurementOutcome> MeasureHereAsync<TDelegate>(
        TDelegate action,
        MeasurementOptions options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TDelegate : Delegate
    {
        var spec = SpecFor(options, progress);

        return action switch
        {
            Action sync => Task.FromResult(BenchmarkRunner.Instance.Run(name, sync, spec, cancellationToken)),
            Func<Task> asyncVoid => BenchmarkRunner.Instance.RunAsync(name, asyncVoid, spec, cancellationToken),
            _ => DelegateDispatch.MeasureAsync(name, action, spec, cancellationToken),
        };
    }

    /// <summary>
    ///     Builds the spec for a measurement taken in <b>this</b> process.
    ///     <para>
    ///         The generic runtime-profile guidance is suppressed here, because by the time this is
    ///         reached Simple mode has already decided - and explained - why the host is being used:
    ///         either the caller asked for it via <c>RunInProcess</c>, in which case a warning is
    ///         noise, or the body could not be addressed, in which case
    ///         <see cref="SimpleModeGuidance" /> has said so in far more actionable terms. Two
    ///         messages about the same fact teach people to read neither.
    ///     </para>
    ///     <para>
    ///         Suppressing the message never suppresses the provenance: the result is still stamped
    ///         <c>host</c> and carries its <see cref="IsolationStatus" />.
    ///     </para>
    /// </summary>
    private static RunSpec SpecFor(MeasurementOptions? options, IBenchmarkProgress? progress) => new()
    {
        Options = (options ?? MeasurementOptions.Default) with { SuppressRuntimeProfileWarning = true },
        Progress = progress ?? NullBenchmarkProgress.Instance,
    };

    /// <summary>
    ///     Records where the measurement ran, on the result. The stamp is applied here rather than
    ///     deeper in the engine because the engine measures whatever process it is in and has no way
    ///     to know whether that process was chosen or inherited.
    /// </summary>
    private static MeasurementOutcome Stamp(MeasurementOutcome outcome, IsolationStatus status)
        => outcome with { Result = outcome.Result with { IsolationStatus = status } };

    /// <summary>
    ///     Emits the always-on Debug-build / debugger-attached warning once per process.
    ///     Single-method mode does not go through <see cref="EnvironmentControl.Apply" />
    ///     (which emits it for Suite and Harness mode), so the facade calls it directly.
    ///     The once-per-process guard inside <see cref="EnvironmentControl" /> prevents
    ///     double emission when <see cref="Benchmark.Run" /> is called from inside a
    ///     Suite or Harness process that already warned via <c>Apply</c>.
    /// </summary>
    private static void EmitBuildConfigurationGuidanceOnce(MeasurementOptions? options)
        => EnvironmentControl.EmitBuildConfigurationGuidance(options?.Environment);
}
