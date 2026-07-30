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
///         Every original overload keeps its signature, including the synchronous return of
///         <see cref="Run(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />.
///         Use the <c>RunInProcess</c> family when measuring the current process is the point -
///         cold-start cost, or a body that must observe host state.
///     </para>
///     <para>
///         For a benchmark over prepared data, pass the preparation as its own delegate:
///         <c>Run(prepare: () =&gt; BuildData(), body: d =&gt; Sort(d))</c>. The <c>var data = Build();
///         Run(() =&gt; Sort(data))</c> shape captures, so it can only be refused - splitting it makes
///         both halves addressable and the worker builds the data itself. See
///         <see cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />.
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

    // ---------- Prepared state ----------

    /// <summary>
    ///     Measures <paramref name="body" /> over state built by <paramref name="prepare" />.
    ///     <para>
    ///         This is the isolatable form of the shape everyone writes first:
    ///         <c>var data = Build(); Run(() =&gt; Sort(data));</c>. That lambda <i>captures</i>
    ///         <c>data</c>, and a capture can only be refused - the value exists in this process and
    ///         nowhere else, and fabricating it in a worker was measured to return plausible, silently
    ///         wrong numbers. Splitting it in two makes both halves non-capturing, so the worker gets a
    ///         recipe for the data rather than a value it cannot have:
    ///     </para>
    ///     <code>
    ///     Benchmark.Run(
    ///         prepare: () =&gt; BuildData(),
    ///         body:    d  =&gt; Sort(d));
    ///     </code>
    /// </summary>
    /// <param name="prepare">
    ///     Builds the state, once, before warmup, in the process that measures - so the cost of building
    ///     it is never inside a reading. Must capture nothing itself, for the same reason the body must.
    /// </param>
    /// <param name="body">The measured code, receiving what <paramref name="prepare" /> returned.</param>
    /// <remarks>
    ///     <paramref name="prepare" /> runs <b>once</b>, not per iteration. A body that mutates its state
    ///     therefore sees the mutation on every iteration after the first - <c>d =&gt; Array.Sort(d)</c>
    ///     sorts an already-sorted array from the second sample onward. Where that matters, reset it with
    ///     the per-iteration hooks on <see cref="BenchmarkSuite" />, which run outside the timed region.
    /// </remarks>
    public static BenchmarkResult Run<TState>(Func<TState> prepare, Action<TState> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, body, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult Run<TState, T>(Func<TState> prepare, Func<TState, T> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, body, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(prepare, body, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(prepare, body, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static MeasurementOutcome RunRaw<TState>(Func<TState> prepare, Action<TState> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureWithState(
                prepare, body, BindStateSync(prepare, body), options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static MeasurementOutcome RunRaw<TState, T>(Func<TState> prepare, Func<TState, T> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureWithState(
                prepare, body, BindStateValue(prepare, body), options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<MeasurementOutcome> RunRawAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureWithState(
            prepare, body, BindStateAsync(prepare, body), options, name, progress, cancellationToken);

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<MeasurementOutcome> RunRawAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureWithState(
            prepare, body, BindStateAsyncValue(prepare, body), options, name, progress, cancellationToken);

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

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult RunInProcess<TState>(Func<TState> prepare, Action<TState> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHere(BindStateSync(prepare, body), options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult RunInProcess<TState, T>(Func<TState> prepare, Func<TState, T> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHere(BindStateValue(prepare, body), options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<BenchmarkResult> RunInProcessAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunInProcessAsync(BindStateAsync(prepare, body), options, name, progress, cancellationToken);

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<BenchmarkResult> RunInProcessAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunInProcessAsync(BindStateAsyncValue(prepare, body), options, name, progress, cancellationToken);

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
        CancellationToken cancellationToken,
        Delegate? stateFactory = null,
        Delegate? inProcessBody = null) where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(action);

        var effective = options ?? MeasurementOptions.Default;
        EmitBuildConfigurationGuidanceOnce(options);

        // The isolated path measures `action` with the state bound in the worker; the fallback measures
        // an equivalent delegate with the state bound here. Two delegates rather than one because the
        // worker must receive the body *unbound* - a body already closed over a value built in this
        // process is exactly the capturing shape that cannot be addressed.
        var here = inProcessBody ?? action;

        var (outcome, status) = await SingleBodyRunner.RunAsync(
                name,
                action,
                effective,
                progress ?? NullBenchmarkProgress.Instance,
                () => MeasureHereAsync(here, effective, name, progress, cancellationToken),
                cancellationToken,
                stateFactory)
            .ConfigureAwait(false);

        return Stamp(outcome, status);
    }

    /// <summary>
    ///     Measures a prepared-state body: isolated when both delegates can be addressed, and in this
    ///     process - with the state built here - when they cannot.
    /// </summary>
    /// <remarks>
    ///     Two delegates travel, not one. The worker must receive the body <b>unbound</b>, because a body
    ///     already closed over a value built in this process is precisely the capturing shape that cannot
    ///     be addressed; the pre-bound <paramref name="inProcessBody" /> exists only for the fallback.
    /// </remarks>
    private static Task<MeasurementOutcome> MeasureWithState<TBody>(
        Delegate prepare,
        TBody body,
        Delegate inProcessBody,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TBody : Delegate
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(body);

        return MeasureAsync(body, options, name, progress, cancellationToken, prepare, inProcessBody);
    }

    /// <summary>
    ///     A one-shot accessor for the prepared state, built on first use and reused after.
    /// </summary>
    /// <remarks>
    ///     Deferred rather than eager so <paramref name="prepare" /> runs only if the in-process path is
    ///     actually taken - an isolated run builds its state in the worker, and building it here as well
    ///     would run the user's preparation twice, once for a delegate nothing measures. Cached rather
    ///     than per-call because the engine invokes the body thousands of times, and rebuilding would put
    ///     the cost of preparation inside every reading. Both match what the worker does: once, before
    ///     warmup.
    /// </remarks>
    private static Func<TState> LazyState<TState>(Func<TState> prepare)
    {
        var built = false;
        TState state = default!;

        return () =>
        {
            if (built)
                return state;

            state = prepare();
            built = true;

            return state;
        };
    }

    private static Action BindStateSync<TState>(Func<TState> prepare, Action<TState> body)
    {
        var state = LazyState(prepare);

        return () => body(state());
    }

    private static Func<T> BindStateValue<TState, T>(Func<TState> prepare, Func<TState, T> body)
    {
        var state = LazyState(prepare);

        return () => body(state());
    }

    private static Func<Task> BindStateAsync<TState>(Func<TState> prepare, Func<TState, Task> body)
    {
        var state = LazyState(prepare);

        return () => body(state());
    }

    private static Func<Task<T>> BindStateAsyncValue<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body)
    {
        var state = LazyState(prepare);

        return () => body(state());
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
