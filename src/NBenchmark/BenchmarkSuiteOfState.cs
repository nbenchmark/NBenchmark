using System.Diagnostics;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

/// <summary>
///     A suite whose benchmarks are measured over prepared state, obtained from
///     <see cref="BenchmarkSuite.Over{TState}" />.
/// </summary>
/// <remarks>
///     <para>
///         A separate type rather than extra overloads on <see cref="BenchmarkSuite" />, because
///         <c>Add&lt;T&gt;(string, Action&lt;T&gt;)</c> there already means <i>parameterized</i> - a
///         state-taking two-argument <c>Add</c> on the same type would be ambiguous with it, and which
///         one a call bound to would depend on whether <c>T</c> happened to match a registered parameter
///         type.
///     </para>
///     <para>
///         The <c>With*</c> methods people chain after <c>Add</c> are re-declared here purely to return
///         this type, so the chain does not decay to the base and lose the typed <c>Add</c>. They add no
///         behaviour; each one calls the base and returns <c>this</c>.
///     </para>
/// </remarks>
/// <typeparam name="TState">The prepared value each benchmark body receives.</typeparam>
public sealed class BenchmarkSuite<TState> : BenchmarkSuite
{
    private readonly Func<TState> _prepare;

    internal BenchmarkSuite(string name, Func<TState> prepare) : base(name) => _prepare = prepare;

    // --- Typed Add overloads ---

    /// <summary>Adds a benchmark receiving the prepared state.</summary>
    /// <param name="setup">
    ///     Per-iteration setup, run outside the timed region. The place to undo a mutation the body makes
    ///     to the shared state - a body like <c>d =&gt; Array.Sort(d)</c> otherwise measures an
    ///     already-sorted array from the second sample onward.
    /// </param>
    /// <param name="prepare">
    ///     This benchmark's own state recipe, in place of the suite's. For the suite whose members
    ///     measure the same operation over <i>different</i> inputs - the ordinary reason to write a
    ///     comparison - where the alternative was one suite per input, or a <c>TState</c> holding every
    ///     input at once with each body reaching for its own.
    /// </param>
    public BenchmarkSuite<TState> Add(string name, Action<TState> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null,
        Func<TState>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var recipe = prepare ?? _prepare;

        // The state is bound lazily and cached, so preparation happens once per benchmark and outside
        // the timed region - matching what the worker does when this body is isolated instead.
        var state = Deferred(recipe);

        AddWithState(name, recipe, action,
            (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(name, () => action(state()),
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)),
            setup, teardown, categories);

        return this;
    }

    /// <inheritdoc cref="Add(string, Action{TState}, Action?, Action?, IReadOnlyList{string}?, Func{TState}?)" />
    public BenchmarkSuite<TState> Add<TResult>(string name, Func<TState, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null,
        Func<TState>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var recipe = prepare ?? _prepare;
        var state = Deferred(recipe);

        AddWithState(name, recipe, action,
            (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(name, () => action(state()),
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)),
            setup, teardown, categories);

        return this;
    }

    /// <inheritdoc cref="Add(string, Action{TState}, Action?, Action?, IReadOnlyList{string}?, Func{TState}?)" />
    public BenchmarkSuite<TState> AddAsync(string name, Func<TState, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null,
        Func<TState>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var recipe = prepare ?? _prepare;
        var state = Deferred(recipe);

        AddWithState(name, recipe, action,
            async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(name, () => action(state()),
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false),
            setup, teardown, categories);

        return this;
    }

    /// <inheritdoc cref="Add(string, Action{TState}, Action?, Action?, IReadOnlyList{string}?, Func{TState}?)" />
    public BenchmarkSuite<TState> AddAsync<TResult>(string name, Func<TState, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null,
        Func<TState>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var recipe = prepare ?? _prepare;
        var state = Deferred(recipe);

        AddWithState(name, recipe, action,
            async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(name, () => action(state()),
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false),
            setup, teardown, categories);

        return this;
    }

    /// <summary>
    ///     A per-benchmark accessor that builds the state on first use and reuses it after.
    /// </summary>
    /// <remarks>
    ///     One accessor per <c>Add</c> call, so each benchmark gets its own pristine state rather than
    ///     whatever the previous one left behind - the same rule the isolated path follows, where the
    ///     worker invokes the factory once per body. Deferred rather than eager so an isolated run, which
    ///     prepares in the worker, does not also prepare here for a delegate nothing measures.
    /// </remarks>
    private static Func<TState> Deferred(Func<TState> prepare)
    {
        var built = false;
        TState value = default!;

        return () =>
        {
            if (built)
                return value;

            value = prepare();
            built = true;

            return value;
        };
    }

    // --- Fluent surface, re-typed ---
    //
    // Behaviourless forwarders. They exist because `BenchmarkSuite.Over("x", f).Add(...)
    // .WithBaseline("a").Add(...)` must keep compiling: without them the chain returns the base type
    // after the first With* call and the typed Add is no longer in scope.

    /// <inheritdoc cref="BenchmarkSuite.WithBaseline" />
    public new BenchmarkSuite<TState> WithBaseline(string name) => Chain(() => base.WithBaseline(name));

    /// <inheritdoc cref="BenchmarkSuite.WithIterations" />
    public new BenchmarkSuite<TState> WithIterations(int iterations) => Chain(() => base.WithIterations(iterations));

    /// <inheritdoc cref="BenchmarkSuite.WithWarmup" />
    public new BenchmarkSuite<TState> WithWarmup(int iterations) => Chain(() => base.WithWarmup(iterations));

    /// <inheritdoc cref="BenchmarkSuite.WithLaunchCount" />
    public new BenchmarkSuite<TState> WithLaunchCount(int count) => Chain(() => base.WithLaunchCount(count));

    /// <inheritdoc cref="BenchmarkSuite.WithOpsPerSample" />
    public new BenchmarkSuite<TState> WithOpsPerSample(int opsPerSample)
        => Chain(() => base.WithOpsPerSample(opsPerSample));

    /// <inheritdoc cref="BenchmarkSuite.WithOutlierMode" />
    public new BenchmarkSuite<TState> WithOutlierMode(OutlierMode mode) => Chain(() => base.WithOutlierMode(mode));

    /// <inheritdoc cref="BenchmarkSuite.WithRunOrder" />
    public new BenchmarkSuite<TState> WithRunOrder(RunOrder order) => Chain(() => base.WithRunOrder(order));

    /// <inheritdoc cref="BenchmarkSuite.WithSeed" />
    public new BenchmarkSuite<TState> WithSeed(int seed) => Chain(() => base.WithSeed(seed));

    /// <inheritdoc cref="BenchmarkSuite.WithDetail" />
    public new BenchmarkSuite<TState> WithDetail(ReportDetail detail) => Chain(() => base.WithDetail(detail));

    /// <inheritdoc cref="BenchmarkSuite.WithReporter" />
    public new BenchmarkSuite<TState> WithReporter(IReporter reporter) => Chain(() => base.WithReporter(reporter));

    /// <inheritdoc cref="BenchmarkSuite.WithProgress" />
    public new BenchmarkSuite<TState> WithProgress(IBenchmarkProgress progress)
        => Chain(() => base.WithProgress(progress));

    /// <inheritdoc cref="BenchmarkSuite.WithMeasurementProfile" />
    public new BenchmarkSuite<TState> WithMeasurementProfile(MeasurementProfile profile)
        => Chain(() => base.WithMeasurementProfile(profile));

    /// <inheritdoc cref="BenchmarkSuite.WithRuntimeProfile" />
    public new BenchmarkSuite<TState> WithRuntimeProfile(RuntimeProfile profile)
        => Chain(() => base.WithRuntimeProfile(profile));

    /// <inheritdoc cref="BenchmarkSuite.WithSuiteSetup" />
    public new BenchmarkSuite<TState> WithSuiteSetup(Action setup) => Chain(() => base.WithSuiteSetup(setup));

    /// <inheritdoc cref="BenchmarkSuite.WithSuiteTeardown" />
    public new BenchmarkSuite<TState> WithSuiteTeardown(Action teardown)
        => Chain(() => base.WithSuiteTeardown(teardown));

    /// <inheritdoc cref="BenchmarkSuite.WithIsolation" />
    public new BenchmarkSuite<TState> WithIsolation(Isolation isolation) => Chain(() => base.WithIsolation(isolation));

    // --- The rest of the base's fluent surface ---
    //
    // Re-declared for one reason: to return this type, so the chain does not decay to the base and
    // take the typed Add out of scope with it. There is a parity test asserting that this list is
    // complete, because the failure mode when it is not has no diagnostic at all - the call compiles,
    // returns BenchmarkSuite, and the next Add(string, Action<TState>) simply cannot infer its
    // parameter.

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithAllocations(bool enabled = true)
        => Chain(() => base.WithAllocations(enabled));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithAutoTune(AutoTuneOptions autoTune)
        => Chain(() => base.WithAutoTune(autoTune));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithCategories(params string[] categories)
        => Chain(() => base.WithCategories(categories));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithCategoryFilter(IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
        => Chain(() => base.WithCategoryFilter(include, exclude));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithConfidenceLevel(double level)
        => Chain(() => base.WithConfidenceLevel(level));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithDedicatedHostGuidance(bool enabled = true)
        => Chain(() => base.WithDedicatedHostGuidance(enabled));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithDiagnostics(DiagnosticsOptions diagnostics)
        => Chain(() => base.WithDiagnostics(diagnostics));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithDriftCanary(DriftCanaryOptions driftCanary)
        => Chain(() => base.WithDriftCanary(driftCanary));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithDriftCanary(bool enabled)
        => Chain(() => base.WithDriftCanary(enabled));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithHardwareAffinity(params int[] cores)
        => Chain(() => base.WithHardwareAffinity(cores));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithInterferenceFilter(bool enabled = true)
        => Chain(() => base.WithInterferenceFilter(enabled));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithMinimumPracticalEffect(double minimumDelta)
        => Chain(() => base.WithMinimumPracticalEffect(minimumDelta));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithMinimumRelativeShift(double minimumRelativeShift)
        => Chain(() => base.WithMinimumRelativeShift(minimumRelativeShift));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithOptions(MeasurementOptions options)
        => Chain(() => base.WithOptions(options));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithObserver(IMeasurementObserver observer)
        => Chain(() => base.WithObserver(observer));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithOutlierDetector(Func<IOutlierDetector> factory)
        => Chain(() => base.WithOutlierDetector(factory));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithProcessPriority(ProcessPriorityClass priority)
        => Chain(() => base.WithProcessPriority(priority));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithRuntimes(params RuntimeMoniker[] runtimes)
        => Chain(() => base.WithRuntimes(runtimes));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithSignificance(bool enabled)
        => Chain(() => base.WithSignificance(enabled));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithSignificanceLevel(double level)
        => Chain(() => base.WithSignificanceLevel(level));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithSignificanceTest(Func<ISignificanceTest> factory)
        => Chain(() => base.WithSignificanceTest(factory));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithSuppressBuildConfigurationWarning(bool suppress = true)
        => Chain(() => base.WithSuppressBuildConfigurationWarning(suppress));

    /// <inheritdoc />
    public new BenchmarkSuite<TState> WithThreadControl(bool enabled = true)
        => Chain(() => base.WithThreadControl(enabled));


    private BenchmarkSuite<TState> Chain(Func<BenchmarkSuite> configure)
    {
        configure();

        return this;
    }
}
