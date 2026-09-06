using NBenchmark.Engine;
using NBenchmark.Workers;

namespace NBenchmark;

/// <summary>
///     Single mode entry point: measure a single piece of code.
///     <para>
///         Bodies are measured in a dedicated worker process by default, because JIT tiering,
///         dynamic PGO, ReadyToRun and GC flavour are fixed when a process starts and can only be
///         chosen for a process that has not started yet. Captured state crosses that boundary when
///         it can be sent faithfully - primitives, strings, arrays, the standard collections under a
///         default comparer, and types marked <c>[BenchmarkState]</c>. Anything else is refused by
///         name: isolation is never faked and captured state is never reconstructed, because doing so
///         was measured to return plausible, silently wrong numbers.
///     </para>
///     <para>
///         A refusal is an error. <see cref="MeasurementOptions.RequireIsolation" /> defaults to
///         <c>true</c>, so a body that cannot be isolated throws rather than being quietly measured
///         here - in-process measurement is something you ask for, not something that happens to
///         you. Set <c>RequireIsolation = false</c> to take the labelled fallback instead: the run
///         continues in this process, says so on stderr, and stamps
///         <see cref="IsolationStatus.InProcessCapturedState" /> on the result.
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
///         Run(() =&gt; Sort(data))</c> shape captures, which isolates only when <c>data</c> is
///         sendable, and hands the same instance to every sample either way - a body that mutates its
///         input then measures an already-mutated one from the second sample onward. Splitting it
///         builds the data in the measuring process, once per benchmark. See
///         <see cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />.
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

    internal static MeasurementOutcome RunRaw(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Measure(action, options, name, progress, cancellationToken);

    internal static MeasurementOutcome RunRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Measure(action, options, name, progress, cancellationToken);

    internal static Task<MeasurementOutcome> RunRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureAsync(action, options, name, progress, cancellationToken);

    internal static Task<MeasurementOutcome> RunRawAsync<T>(Func<Task<T>> action,
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
    /// <param name="setup">
    ///     Per-iteration setup, run outside the timed region, receiving the same prepared value the body
    ///     does. This is where a body that mutates its state undoes the mutation:
    ///     <paramref name="prepare" /> runs <b>once</b>, so without it <c>d =&gt; Array.Sort(d)</c> sorts
    ///     an already-sorted array from the second sample onward and reports the cost of doing nothing.
    ///     <code>
    ///     Benchmark.Run(
    ///         prepare: () =&gt; BuildData(),
    ///         body:    d  =&gt; Array.Sort(d),
    ///         setup:   d  =&gt; Shuffle(d));
    ///     </code>
    ///     Addressed like the body, so it must capture nothing; the worker binds it to the state
    ///     <i>it</i> built, which is the array the body actually reads.
    /// </param>
    /// <param name="teardown">
    ///     Per-iteration teardown, run outside the timed region, on the same terms as
    ///     <paramref name="setup" />.
    /// </param>
    /// <remarks>
    ///     <paramref name="prepare" /> runs <b>once</b>, not per iteration - see
    ///     <paramref name="setup" /> for the reset that makes a mutating body measurable.
    /// </remarks>
    public static BenchmarkResult Run<TState>(Func<TState> prepare, Action<TState> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, body, setup, teardown, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult Run<TState, T>(Func<TState> prepare, Func<TState, T> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, body, setup, teardown, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(prepare, body, setup, teardown, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(prepare, body, setup, teardown, options, name, progress, cancellationToken).ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TState>(Func<TState> prepare, Action<TState> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
                [StateRecipe.For(prepare)], body, state => BindStateSync(state, body), prepare, setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TState, T>(Func<TState> prepare, Func<TState, T> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
                [StateRecipe.For(prepare)], body, state => BindStateValue(state, body), prepare, setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
            [StateRecipe.For(prepare)], body, state => BindStateAsync(state, body), prepare, setup, teardown,
            options, name, progress, cancellationToken);

    /// <inheritdoc cref="Run{TState}(Func{TState}, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
            [StateRecipe.For(prepare)], body, state => BindStateAsyncValue(state, body), prepare, setup, teardown,
            options, name, progress, cancellationToken);

    // ---------- Prepared state, parameterized ----------

    /// <summary>
    ///     Measures <paramref name="body" /> over state built by <paramref name="prepare" /> from
    ///     <paramref name="prepareArgument" />.
    ///     <para>
    ///         This names the recipe's input explicitly rather than letting it be captured. A
    ///         capturing <c>prepare: () =&gt; Build(size)</c> also isolates - the captured value
    ///         travels in the group's receiver table, see <see cref="Workers.AddressedFactory" /> -
    ///         so this overload is a clarity choice, not a workaround. Prefer it when the input is a
    ///         scalar you want visible in the call, or when you want the value bound at the call site
    ///         rather than wherever the local happens to be assigned:
    ///     </para>
    ///     <code>
    ///     Benchmark.Run(
    ///         prepare:         (int size) =&gt; BuildData(size),
    ///         prepareArgument: 100_000,
    ///         body:            d =&gt; Sort(d));
    ///     </code>
    /// </summary>
    /// <param name="prepare">
    ///     Builds the state, once, before warmup, in the process that measures. Receives
    ///     <paramref name="prepareArgument" />. It may additionally capture, on the same faithfulness
    ///     rule a body is held to - what it captures must be reproducible from its serialized
    ///     contents, so a live object is still refused.
    /// </param>
    /// <param name="prepareArgument">
    ///     The value to call <paramref name="prepare" /> with. Sent alongside the address, so it must be
    ///     one of the types <c>TestArgumentCodec</c> carries - a primitive, string, enum, decimal,
    ///     <see cref="DateTime" />, <see cref="DateTimeOffset" />, <see cref="TimeSpan" /> or
    ///     <see cref="Guid" />. Anything larger belongs inside <paramref name="prepare" />.
    /// </param>
    /// <param name="body">The measured code, receiving what <paramref name="prepare" /> returned.</param>
    public static BenchmarkResult Run<TArg, TState>(
        Func<TArg, TState> prepare, TArg prepareArgument, Action<TState> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, prepareArgument, body, setup, teardown, options, name, progress, cancellationToken)
            .Result;

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult Run<TArg, TState, T>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, T> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare, prepareArgument, body, setup, teardown, options, name, progress, cancellationToken)
            .Result;

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TArg, TState>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, Task> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(
                prepare, prepareArgument, body, setup, teardown, options, name, progress, cancellationToken)
            .ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TArg, TState, T>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, Task<T>> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(
                prepare, prepareArgument, body, setup, teardown, options, name, progress, cancellationToken)
            .ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TArg, TState>(
        Func<TArg, TState> prepare, TArg prepareArgument, Action<TState> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
                [StateRecipe.For(prepare, prepareArgument)], body, state => BindStateSync(state, body),
                () => prepare(prepareArgument), setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TArg, TState, T>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, T> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
                [StateRecipe.For(prepare, prepareArgument)], body, state => BindStateValue(state, body),
                () => prepare(prepareArgument), setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TArg, TState>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, Task> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
            [StateRecipe.For(prepare, prepareArgument)], body, state => BindStateAsync(state, body),
            () => prepare(prepareArgument), setup, teardown,
            options, name, progress, cancellationToken);

    /// <inheritdoc cref="Run{TArg, TState}(Func{TArg, TState}, TArg, Action{TState}, Action{TState}?, Action{TState}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TArg, TState, T>(
        Func<TArg, TState> prepare, TArg prepareArgument, Func<TState, Task<T>> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverState(
            [StateRecipe.For(prepare, prepareArgument)], body, state => BindStateAsyncValue(state, body),
            () => prepare(prepareArgument), setup, teardown,
            options, name, progress, cancellationToken);

    // ---------- Two prepared states ----------

    /// <summary>
    ///     Measures <paramref name="body" /> over <b>two</b> independently prepared values.
    ///     <para>
    ///         A body taking two parameters, each filled by its own recipe. The alternative was to
    ///         hand-tuple the pair into one <c>TState</c> and destructure it in the body, which is
    ///         boilerplate that exists only because the address carried a single prepared slot - not
    ///         because a benchmark over two inputs is unusual.
    ///     </para>
    ///     <code>
    ///     Benchmark.Run(
    ///         prepare1: () =&gt; BuildHaystack(),
    ///         prepare2: () =&gt; BuildNeedles(),
    ///         body:     (haystack, needles) =&gt; Search(haystack, needles));
    ///     </code>
    /// </summary>
    /// <remarks>
    ///     Each recipe runs once, before warmup, in the process that measures. Both must capture
    ///     nothing, for the same reason one must.
    /// </remarks>
    public static BenchmarkResult Run<TState1, TState2>(
        Func<TState1> prepare1, Func<TState2> prepare2, Action<TState1, TState2> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare1, prepare2, body, setup, teardown, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult Run<TState1, TState2, T>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, T> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => RunRaw(prepare1, prepare2, body, setup, teardown, options, name, progress, cancellationToken).Result;

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState1, TState2>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, Task> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(
                prepare1, prepare2, body, setup, teardown, options, name, progress, cancellationToken)
            .ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static async Task<BenchmarkResult> RunAsync<TState1, TState2, T>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, Task<T>> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => (await RunRawAsync(
                prepare1, prepare2, body, setup, teardown, options, name, progress, cancellationToken)
            .ConfigureAwait(false)).Result;

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TState1, TState2>(
        Func<TState1> prepare1, Func<TState2> prepare2, Action<TState1, TState2> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverTwoStates(
                body, (first, second) => BindTwoStatesSync(first, second, body), prepare1, prepare2, setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static MeasurementOutcome RunRaw<TState1, TState2, T>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, T> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverTwoStates(
                body, (first, second) => BindTwoStatesValue(first, second, body), prepare1, prepare2, setup, teardown,
                options, name, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TState1, TState2>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, Task> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverTwoStates(
            body, (first, second) => BindTwoStatesAsync(first, second, body), prepare1, prepare2, setup, teardown,
            options, name, progress, cancellationToken);

    /// <inheritdoc cref="Run{TState1, TState2}(Func{TState1}, Func{TState2}, Action{TState1, TState2}, Action{TState1, TState2}?, Action{TState1, TState2}?, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    internal static Task<MeasurementOutcome> RunRawAsync<TState1, TState2, T>(
        Func<TState1> prepare1, Func<TState2> prepare2, Func<TState1, TState2, Task<T>> body,
        Action<TState1, TState2>? setup = null, Action<TState1, TState2>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureOverTwoStates(
            body, (first, second) => BindTwoStatesAsyncValue(first, second, body), prepare1, prepare2, setup, teardown,
            options, name, progress, cancellationToken);

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
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHereOverState(
                state => BindStateSync(state, body), prepare, setup, teardown, options, name, progress,
                cancellationToken)
            .Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static BenchmarkResult RunInProcess<TState, T>(Func<TState> prepare, Func<TState, T> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHereOverState(
                state => BindStateValue(state, body), prepare, setup, teardown, options, name, progress,
                cancellationToken)
            .Result;

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<BenchmarkResult> RunInProcessAsync<TState>(Func<TState> prepare, Func<TState, Task> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHereOverStateAsync(
            state => BindStateAsync(state, body), prepare, setup, teardown, options, name, progress,
            cancellationToken);

    /// <inheritdoc cref="RunInProcess(Action, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    public static Task<BenchmarkResult> RunInProcessAsync<TState, T>(Func<TState> prepare, Func<TState, Task<T>> body,
        Action<TState>? setup = null, Action<TState>? teardown = null,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
        => MeasureHereOverStateAsync(
            state => BindStateAsyncValue(state, body), prepare, setup, teardown, options, name, progress,
            cancellationToken);

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
        // Single mode has always returned synchronously and continues to. The wait is on a worker
        // handshake rather than on measurement scheduling, but from the caller's side nothing about
        // the contract changed.
        => MeasureAsync(action, options, name, progress, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    ///     The per-iteration hooks for one Single-mode benchmark, in both the forms the two paths need.
    /// </summary>
    /// <remarks>
    ///     Two forms of the same pair, because the two paths need different things from them. The
    ///     <b>addressed</b> delegates are the user's own, still taking the prepared value, which is what
    ///     the worker binds to the state <i>it</i> built. The <b>host</b> pair is already bound over the
    ///     state built here, for the fallback. Sending the bound form would be sending a delegate closed
    ///     over a value that exists only in this process - the capturing shape that cannot be addressed.
    /// </remarks>
    private readonly record struct Hooks(
        Delegate? Setup = null,
        Delegate? Teardown = null,
        Action? HostSetup = null,
        Action? HostTeardown = null);

    private static async Task<MeasurementOutcome> MeasureAsync<TDelegate>(
        TDelegate action,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<StateRecipe?>? recipes = null,
        Delegate? inProcessBody = null,
        Hooks hooks = default) where TDelegate : Delegate
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
                () => MeasureHereAsync(here, effective, name, progress, cancellationToken, hooks),
                cancellationToken,
                recipes,
                hooks.Setup,
                hooks.Teardown)
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
        IReadOnlyList<StateRecipe?> recipes,
        TBody body,
        Delegate inProcessBody,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken,
        Hooks hooks = default) where TBody : Delegate
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(body);

        return MeasureAsync(body, options, name, progress, cancellationToken, recipes, inProcessBody, hooks);
    }

    /// <summary>
    ///     Measures a prepared-state body in <b>this</b> process, with its hooks bound over the same
    ///     accessor the body is.
    /// </summary>
    /// <remarks>
    ///     The isolated path binds the hook to the values the worker's own slots resolved to, so opting
    ///     into the host process must not quietly give the hook a private copy of the state. One
    ///     accessor is what keeps the two paths saying the same thing.
    /// </remarks>
    private static MeasurementOutcome MeasureHereOverState<TState>(
        Func<Func<TState>, Delegate> bind,
        Func<TState> prepare,
        Action<TState>? setup,
        Action<TState>? teardown,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);

        var state = LazyState(prepare);

        EmitBuildConfigurationGuidanceOnce(options);

        var outcome = MeasureHereAsync(
                bind(state), options ?? MeasurementOptions.Default, name, progress, cancellationToken,
                StateHooks(state, setup, teardown))
            .GetAwaiter()
            .GetResult();

        return Stamp(outcome, IsolationStatus.InProcessRequested);
    }

    /// <inheritdoc cref="MeasureHereOverState{TState}" />
    private static async Task<BenchmarkResult> MeasureHereOverStateAsync<TState>(
        Func<Func<TState>, Delegate> bind,
        Func<TState> prepare,
        Action<TState>? setup,
        Action<TState>? teardown,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);

        var state = LazyState(prepare);

        EmitBuildConfigurationGuidanceOnce(options);

        var outcome = await MeasureHereAsync(
                bind(state), options ?? MeasurementOptions.Default, name, progress, cancellationToken,
                StateHooks(state, setup, teardown))
            .ConfigureAwait(false);

        return Stamp(outcome, IsolationStatus.InProcessRequested).Result;
    }

    /// <summary>
    ///     Measures a prepared-state body with its hooks, over <b>one</b> accessor for the state.
    /// </summary>
    /// <remarks>
    ///     The accessor is built here, once, and handed to both <paramref name="bind" /> and the hooks -
    ///     so on the host path a <c>setup</c> resets the value the body reads rather than a second one
    ///     built beside it. That is the same guarantee the worker gives, where the hook is bound to the
    ///     values the body's own slots resolved to.
    /// </remarks>
    /// <param name="bind">
    ///     Binds the user's body over the accessor, producing the parameterless delegate the host path
    ///     measures. A lambda per shape rather than an overload per shape, because the shapes differ only
    ///     in their return type.
    /// </param>
    private static Task<MeasurementOutcome> MeasureOverState<TState, TBody>(
        IReadOnlyList<StateRecipe?> recipes,
        TBody body,
        Func<Func<TState>, Delegate> bind,
        Func<TState> prepare,
        Action<TState>? setup,
        Action<TState>? teardown,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TBody : Delegate
    {
        ArgumentNullException.ThrowIfNull(prepare);

        var state = LazyState(prepare);

        return MeasureWithState(
            recipes, body, bind(state), options, name, progress, cancellationToken,
            StateHooks(state, setup, teardown));
    }

    /// <inheritdoc cref="MeasureOverState{TState, TBody}" />
    private static Task<MeasurementOutcome> MeasureOverTwoStates<TState1, TState2, TBody>(
        TBody body,
        Func<Func<TState1>, Func<TState2>, Delegate> bind,
        Func<TState1> prepare1,
        Func<TState2> prepare2,
        Action<TState1, TState2>? setup,
        Action<TState1, TState2>? teardown,
        MeasurementOptions? options,
        string name,
        IBenchmarkProgress? progress,
        CancellationToken cancellationToken) where TBody : Delegate
    {
        ArgumentNullException.ThrowIfNull(prepare1);
        ArgumentNullException.ThrowIfNull(prepare2);

        var first = LazyState(prepare1);
        var second = LazyState(prepare2);

        return MeasureWithState(
            TwoSlots(prepare1, prepare2), body, bind(first, second), options, name, progress,
            cancellationToken, StateHooks(first, second, setup, teardown));
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

    // The binders take the *accessor*, never the raw prepare delegate. Each call to LazyState makes a
    // new one-shot cache, so a body and its hooks built from two separate calls would prepare two
    // states and the setup would reset one the body never reads - the private-copy failure that shared
    // receivers exist to prevent, reintroduced on the host path.
    private static Action BindStateSync<TState>(Func<TState> state, Action<TState> body)
        => () => body(state());

    private static Func<T> BindStateValue<TState, T>(Func<TState> state, Func<TState, T> body)
        => () => body(state());

    private static Func<Task> BindStateAsync<TState>(Func<TState> state, Func<TState, Task> body)
        => () => body(state());

    private static Func<Task<T>> BindStateAsyncValue<TState, T>(Func<TState> state, Func<TState, Task<T>> body)
        => () => body(state());

    /// <summary>
    ///     The two-slot recipe list for a body taking two prepared values.
    /// </summary>
    private static IReadOnlyList<StateRecipe?> TwoSlots(Delegate prepare1, Delegate prepare2)
        => [StateRecipe.For(prepare1), StateRecipe.For(prepare2)];

    /// <summary>
    ///     Packages state-taking per-iteration hooks for both measurement paths.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The hook the worker receives is the user's own, still taking the prepared value: the
    ///         worker binds it to the state <i>it</i> built, so a reset acts on the array the body
    ///         actually reads. The host pair is bound here, over the state built here, through the same
    ///         cached accessor the fallback body uses - so both see one value rather than two.
    ///     </para>
    ///     <para>
    ///         <c>null</c> in, <c>null</c> out: a benchmark with no hooks carries none, and a hook slot
    ///         that exists but is empty is not the same as one that was never asked for.
    ///     </para>
    /// </remarks>
    private static Hooks StateHooks<TState>(
        Func<TState> state, Action<TState>? setup, Action<TState>? teardown)
    {
        if (setup is null && teardown is null)
            return default;

        return new Hooks(
            setup,
            teardown,
            setup is null ? null : () => setup(state()),
            teardown is null ? null : () => teardown(state()));
    }

    /// <inheritdoc cref="StateHooks{TState}(Func{TState}, Action{TState}?, Action{TState}?)" />
    private static Hooks StateHooks<TState1, TState2>(
        Func<TState1> first,
        Func<TState2> second,
        Action<TState1, TState2>? setup,
        Action<TState1, TState2>? teardown)
    {
        if (setup is null && teardown is null)
            return default;

        return new Hooks(
            setup,
            teardown,
            setup is null ? null : () => setup(first(), second()),
            teardown is null ? null : () => teardown(first(), second()));
    }

    // Two accessors, not one: each value is built by its own recipe and is independent of the other,
    // which is exactly what the worker does with the two slots. Sharing one accessor would make the
    // second value's construction depend on the first's, in this process only.

    private static Action BindTwoStatesSync<TState1, TState2>(
        Func<TState1> first, Func<TState2> second, Action<TState1, TState2> body)
        => () => body(first(), second());

    private static Func<T> BindTwoStatesValue<TState1, TState2, T>(
        Func<TState1> first, Func<TState2> second, Func<TState1, TState2, T> body)
        => () => body(first(), second());

    private static Func<Task> BindTwoStatesAsync<TState1, TState2>(
        Func<TState1> first, Func<TState2> second, Func<TState1, TState2, Task> body)
        => () => body(first(), second());

    private static Func<Task<T>> BindTwoStatesAsyncValue<TState1, TState2, T>(
        Func<TState1> first, Func<TState2> second, Func<TState1, TState2, Task<T>> body)
        => () => body(first(), second());

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
        CancellationToken cancellationToken,
        Hooks hooks = default) where TDelegate : Delegate
    {
        var spec = SpecFor(options, progress) with
        {
            IterationSetup = hooks.HostSetup,
            IterationTeardown = hooks.HostTeardown,
        };

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
    ///         reached Single mode has already decided - and explained - why the host is being used:
    ///         either the caller asked for it via <c>RunInProcess</c>, in which case a warning is
    ///         noise, or the body could not be addressed, in which case
    ///         <see cref="SingleModeGuidance" /> has said so in far more actionable terms. Two
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
