using System.Diagnostics;
using System.Runtime.CompilerServices;
using NBenchmark.Diagnostics;
using NBenchmark.Engine;
using NBenchmark.Observers;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEnvelope> _benchmarks = [];
    private readonly List<string> _categoryFilterExclude = [];
    private readonly List<string> _categoryFilterInclude = [];
    private readonly List<IMeasurementObserver> _observers = [];

    private readonly List<ParameterDef> _parameterDefs = [];
    private readonly List<ParameterizedAdd> _parameterizedFactories = [];

    private readonly List<IReporter> _reporters = [];
    private string? _baselineName;
    private ReportDetail _detail;
    private bool _isolated;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private string[]? _pendingCategories;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private IReadOnlyList<RuntimeMoniker> _runtimes = [];
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Internal accessor for the configured <see cref="EnvironmentOptions" />. Exposed
    ///     so tests can verify the fluent <c>With*</c> builders without running a suite.
    /// </summary>
    internal EnvironmentOptions? Environment => _options.Environment;

    // --- Parameter-free Add overloads ---

    public BenchmarkSuite Add(string name, Action action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), (spec, ct) =>
            Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), async (spec, ct) =>
            await BenchmarkRunner.Instance.RunAsync(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), (spec, ct) =>
            Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), async (spec, ct) =>
            await BenchmarkRunner.Instance.RunAsync(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));

    // --- Parameterized Add overloads: arity 1 ---

    public BenchmarkSuite Add<T>(string name, Action<T> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var val = (T)values[0]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(val),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T)]));

        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<T, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var val = (T)values[0]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        async () => await action(val).ConfigureAwait(false),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T)]));

        return this;
    }

    public BenchmarkSuite Add<T, TResult>(string name, Func<T, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var val = (T)values[0]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(val),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T)]));

        return this;
    }

    public BenchmarkSuite Add<T, TResult>(string name, Func<T, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var val = (T)values[0]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        () => action(val),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T)]));

        return this;
    }

    // --- Parameterized Add overloads: arity 2 ---

    public BenchmarkSuite Add<T1, T2>(string name, Action<T1, T2> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T1), typeof(T2)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2>(string name, Func<T1, T2, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        async () => await action(v1, v2).ConfigureAwait(false),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T1), typeof(T2)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, TResult>(string name, Func<T1, T2, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T1), typeof(T2)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, TResult>(string name, Func<T1, T2, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        () => action(v1, v2),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T1), typeof(T2)]));

        return this;
    }

    // --- Parameterized Add overloads: arity 3 ---

    public BenchmarkSuite Add<T1, T2, T3>(string name, Action<T1, T2, T3> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;
                var v3 = (T3)values[2]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2, v3),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T1), typeof(T2), typeof(T3)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3>(string name, Func<T1, T2, T3, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;
                var v3 = (T3)values[2]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        async () => await action(v1, v2, v3).ConfigureAwait(false),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T1), typeof(T2), typeof(T3)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;
                var v3 = (T3)values[2]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) => Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2, v3),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));
            },
            [typeof(T1), typeof(T2), typeof(T3)]));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var v1 = (T1)values[0]!;
                var v2 = (T2)values[1]!;
                var v3 = (T3)values[2]!;

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) => await BenchmarkRunner.Instance.RunAsync(displayName,
                        () => action(v1, v2, v3),
                        spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));
            },
            [typeof(T1), typeof(T2), typeof(T3)]));

        return this;
    }

    // --- Fluent parameter registration ---

    public BenchmarkSuite WithParameter<T>(string name, params T[] values)
    {
        ValidateParameterType(name, values);
        _parameterDefs.Add(new ParameterDef(name, typeof(T), values.Cast<object?>().ToArray()));
        return this;
    }

    public BenchmarkSuite WithParameter<T1, T2>(
        string name1, T1[] values1,
        string name2, T2[] values2)
    {
        ValidateParameterType(name1, values1);
        ValidateParameterType(name2, values2);
        _parameterDefs.Add(new ParameterDef(name1, typeof(T1), values1.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name2, typeof(T2), values2.Cast<object?>().ToArray()));
        return this;
    }

    public BenchmarkSuite WithParameter<T1, T2, T3>(
        string name1, T1[] values1,
        string name2, T2[] values2,
        string name3, T3[] values3)
    {
        ValidateParameterType(name1, values1);
        ValidateParameterType(name2, values2);
        ValidateParameterType(name3, values3);
        _parameterDefs.Add(new ParameterDef(name1, typeof(T1), values1.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name2, typeof(T2), values2.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name3, typeof(T3), values3.Cast<object?>().ToArray()));
        return this;
    }

    // --- Private helpers ---

    private BenchmarkSuite AddEnvelope(
        string name,
        IReadOnlyList<string> categories,
        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEnvelope(name, "", null, false, categories, runAsync));
        return this;
    }

    private void EnsureUniqueName(string name)
    {
        if (_benchmarks.Any(b => b.Name == name) || _parameterizedFactories.Any(f => f.Name == name))
        {
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique - significance testing keys raw samples by name.",
                nameof(name));
        }
    }

    private void EnsureAddNameUnique(string name) => EnsureUniqueName(name);

    public BenchmarkSuite WithBaseline(string name)
    {
        _baselineName = name;
        return this;
    }

    /// <summary>
    ///     Repeats every benchmark in this suite across multiple launches and aggregates
    ///     the per-launch results into cross-launch summary statistics.
    ///     Pass 1 (the default) for standard single-launch measurement.
    /// </summary>
    public BenchmarkSuite WithLaunchCount(int count)
    {
        _options = _options with { LaunchCount = count };
        return this;
    }

    /// <summary>
    ///     Pins an exact measured-sample count, overriding the default confidence-interval-driven
    ///     auto-detection. Pass <c>0</c> for a dry-run.
    /// </summary>
    public BenchmarkSuite WithIterations(int iterations)
    {
        _options = _options with { Iterations = iterations };
        return this;
    }

    /// <summary>
    ///     Pins an exact warmup-sample count, overriding the default plateau-driven auto-detection.
    ///     Pass <c>0</c> to skip warmup.
    /// </summary>
    public BenchmarkSuite WithWarmup(int iterations)
    {
        _options = _options with { WarmupIterations = iterations };
        return this;
    }

    /// <summary>
    ///     Tunes the adaptive measurement loop (warmup plateau, CI-width sample count, and
    ///     ops-per-sample calibration). Use <see cref="AutoTuneOptions.Quick" /> for fast feedback
    ///     or <see cref="AutoTuneOptions.Thorough" /> for tighter intervals.
    /// </summary>
    public BenchmarkSuite WithAutoTune(AutoTuneOptions autoTune)
    {
        ArgumentNullException.ThrowIfNull(autoTune);
        _options = _options with { AutoTune = autoTune };
        return this;
    }

    /// <summary>Selects an adaptive-tuning preset (Default, Quick, or Thorough).</summary>
    public BenchmarkSuite WithAutoTune(AutoTunePreset preset)
    {
        _options = _options with { AutoTune = AutoTuneOptions.FromPreset(preset) };
        return this;
    }

    /// <summary>
    ///     Pins the number of back-to-back body invocations timed as one sample (<c>K</c>),
    ///     overriding auto-calibration. Honoured even with per-iteration setup/teardown.
    /// </summary>
    public BenchmarkSuite WithOpsPerSample(int opsPerSample)
    {
        _options = _options with { OpsPerSample = opsPerSample };
        return this;
    }

    public BenchmarkSuite WithAllocations(bool enabled = true)
    {
        _options = _options with { MeasureAllocationsOverride = enabled };
        return this;
    }

    /// <summary>Configures runtime diagnostics (GC counts, heap info, exceptions, CPU time).</summary>
    public BenchmarkSuite WithDiagnostics(DiagnosticsOptions diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _options = _options with { Diagnostics = diagnostics };
        return this;
    }

    /// <summary>Selects a diagnostics mode (None, Gc, GcAndCpu, All).</summary>
    public BenchmarkSuite WithDiagnostics(DiagnosticsMode mode)
    {
        _options = _options with { Diagnostics = DiagnosticsOptions.FromMode(mode) };
        return this;
    }

    /// <summary>
    ///     Sets the measurement profile, which bundles per-iteration GC, between-benchmark GC, and
    ///     allocation tracking. <see cref="MeasurementProfile.Realistic" /> (the default) keeps natural
    ///     GC pressure in the timing; <see cref="MeasurementProfile.Independent" /> isolates iterations
    ///     for pure-CPU measurement.
    /// </summary>
    public BenchmarkSuite WithMeasurementProfile(MeasurementProfile profile)
    {
        _options = _options with { Profile = profile };
        return this;
    }

    /// <summary>
    ///     Sets the runtime-startup configuration to measure under - JIT tiering, dynamic PGO,
    ///     ReadyToRun and GC flavour. Defaults to <see cref="RuntimeProfile.SteadyState" />.
    ///     <para>
    ///         This is the setting that requires a child process to exist: the runtime reads these
    ///         knobs once at startup, so they can only be applied to a process being launched.
    ///         Benchmarks that run in the host process report <c>"host"</c> and inherit its
    ///         configuration.
    ///     </para>
    /// </summary>
    public BenchmarkSuite WithRuntimeProfile(RuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _options = _options with { RuntimeProfile = profile };
        return this;
    }


    public BenchmarkSuite WithOutlierMode(OutlierMode mode)
    {
        _options = _options with { OutlierMode = mode };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="IOutlierDetector" /> for trimming, overriding
    ///     <see cref="WithOutlierMode" />. Pass one of the built-ins from
    ///     <see cref="OutlierDetectors" /> or your own implementation.
    /// </summary>
    public BenchmarkSuite WithOutlierDetector(IOutlierDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _options = _options with { OutlierDetector = detector };
        return this;
    }

    public BenchmarkSuite WithConfidenceLevel(double level)
    {
        _options = _options with { ConfidenceLevel = level };
        return this;
    }

    public BenchmarkSuite WithSignificance(bool enabled)
    {
        _options = _options with { EnableSignificance = enabled };
        return this;
    }

    public BenchmarkSuite WithSignificanceLevel(double level)
    {
        _options = _options with { SignificanceLevel = level };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="ISignificanceTest" /> strategy, overriding the engine
    ///     default (Mann-Whitney U for two groups, Kruskal-Wallis for three or more). Pass
    ///     one of the built-ins from <see cref="NBenchmark.Stats" /> or your own implementation.
    /// </summary>
    public BenchmarkSuite WithSignificanceTest(ISignificanceTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        _options = _options with { SignificanceTest = test };
        return this;
    }

    /// <summary>
    ///     Requires a minimum strategy-defined practical effect in [0, 1] for a candidate
    ///     to be considered practically significant. Values below the threshold are reported
    ///     as NotSignificant with a <c>neg</c> magnitude label.
    /// </summary>
    public BenchmarkSuite WithMinimumPracticalEffect(double minimumDelta)
    {
        _options = _options with { MinimumPracticalEffect = minimumDelta };
        return this;
    }

    /// <summary>
    ///     Pins the benchmark process to the specified logical CPU cores for the duration
    ///     of the run, removing inter-core migration noise. Cores are zero-based and
    ///     logical (as reported by the OS). The prior affinity is restored when the run
    ///     completes. Call <see cref="WithDedicatedHostGuidance" /> alongside this to
    ///     surface a warning when the host looks unsuitable.
    /// </summary>
    public BenchmarkSuite WithHardwareAffinity(params int[] cores)
    {
        ArgumentNullException.ThrowIfNull(cores);

        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { CpuAffinity = cores },
        };

        return this;
    }

    /// <summary>
    ///     Requests the specified process priority for the duration of the run, reducing
    ///     preemption by unrelated OS work. <see cref="System.Diagnostics.ProcessPriorityClass.High" />
    ///     is the recommended value for dedicated benchmark hosts. The prior priority is
    ///     restored when the run completes. A refused elevation (common on locked-down CI
    ///     runners) is surfaced as a warning, not an error.
    /// </summary>
    public BenchmarkSuite WithProcessPriority(ProcessPriorityClass priority)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { ProcessPriority = priority },
        };

        return this;
    }

    /// <summary>
    ///     Enables a non-fatal pre-run probe that warns when the host looks like a
    ///     shared or otherwise noisy benchmark environment: a low CPU core count,
    ///     an unraisable process priority, or (on macOS) unobservable frequency scaling
    ///     and thermal throttling. The run still proceeds - this is guidance, not a gate.
    /// </summary>
    public BenchmarkSuite WithDedicatedHostGuidance(bool enabled = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { DedicatedHostGuidance = enabled },
        };

        return this;
    }

    /// <summary>
    ///     Suppresses the always-on Debug-build / debugger-attached guidance warning for
    ///     this suite. Use when measuring Debug behavior is intentional.
    /// </summary>
    public BenchmarkSuite WithSuppressBuildConfigurationWarning(bool suppress = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with
            {
                SuppressBuildConfigurationWarning = suppress,
            },
        };

        return this;
    }

    public BenchmarkSuite WithRunOrder(RunOrder order)
    {
        _runOrder = order;
        return this;
    }

    public BenchmarkSuite WithSuiteSetup(Action setup)
    {
        _suiteSetup = setup;
        return this;
    }

    public BenchmarkSuite WithSuiteTeardown(Action teardown)
    {
        _suiteTeardown = teardown;
        return this;
    }

    public BenchmarkSuite WithReporter(IReporter reporter)
    {
        reporter.Detail = _detail;
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkSuite WithDetail(ReportDetail detail)
    {
        _detail = detail;

        foreach (var reporter in _reporters)
        {
            reporter.Detail = detail;
        }

        return this;
    }

    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    /// <summary>
    ///     Attaches a non-perturbing measurement observer that receives live per-sample,
    ///     per-detector, and phase-transition events during the adaptive measurement loop.
    ///     The observer MUST return immediately from each callback - never block, allocate on
    ///     the hot path, or do I/O - because the loop calls it between samples on the
    ///     measurement thread. The default is <see cref="NullMeasurementObserver" />.
    ///     Repeatable: each call adds another observer, and all attached observers receive
    ///     every event through a <see cref="CompositeMeasurementObserver" /> fan-out.
    /// </summary>
    public BenchmarkSuite WithObserver(IMeasurementObserver observer)
    {
        if (observer is not null && observer != NullMeasurementObserver.Instance)
            _observers.Add(observer);

        return this;
    }

    /// <summary>
    ///     Resolves the attached observer list to the single <see cref="IMeasurementObserver" />
    ///     the engine should see. Composes three sources: programmatic instances added via
    ///     <see cref="WithObserver(IMeasurementObserver)" />, CLI-supplied names resolved
    ///     through <see cref="ObserverRegistry" /> (in harness-hosted children), and
    ///     auto-attached observers registered via
    ///     <see cref="ObserverRegistry.RegisterAutoAttach" />. Dedup is by name across all
    ///     three sources so <c>.WithObserver(new StudioLiveObserver())</c> and the auto-attached
    ///     <c>studio</c> registration produce one <c>studio</c> stream, not two. An empty
    ///     result collapses to <see cref="NullMeasurementObserver.Instance" /> so the hot-path
    ///     guard stays false and the loop pays no dispatch cost.
    /// </summary>
    private IMeasurementObserver ResolveObserver()
    {
        // Dedup by Name (last wins) so two programmatic .WithObserver(...) calls for the
        // same named observer fire the later (more deliberate) instance, not both.
        // Anonymous observers (Name = null) are always kept. Mirrors the harness-side
        // ResolveObserver dedup.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programmatic = new List<IMeasurementObserver>(_observers.Count);

        for (var i = _observers.Count - 1; i >= 0; i--)
        {
            var observer = _observers[i];

            if (observer == NullMeasurementObserver.Instance)
                continue;

            if (!string.IsNullOrEmpty(observer.Name))
            {
                if (!seenNames.Add(observer.Name))
                    continue;
            }

            programmatic.Add(observer);
        }

        programmatic.Reverse();

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var observer in programmatic)
        {
            if (!string.IsNullOrEmpty(observer.Name))
                explicitNames.Add(observer.Name);
        }

        var autoAttached = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);

        if (programmatic.Count == 0 && autoAttached.Count == 0)
            return NullMeasurementObserver.Instance;

        if (programmatic.Count == 1 && autoAttached.Count == 0)
            return programmatic[0];

        var all = new List<IMeasurementObserver>(programmatic.Count + autoAttached.Count);
        all.AddRange(programmatic);
        all.AddRange(autoAttached);

        return all.Count switch
        {
            0 => NullMeasurementObserver.Instance,
            1 => all[0],
            _ => new CompositeMeasurementObserver(all),
        };
    }

    /// <summary>
    ///     Resolves observer names forwarded by a parent (via <see cref="IsolatedRunRequest.ObserverNames" />)
    ///     into a single <see cref="IMeasurementObserver" /> for an isolated suite child. The child
    ///     re-runs the entry assembly, so <c>[ModuleInitializer]</c> self-registration populates
    ///     <see cref="ObserverRegistry" /> identically and the names resolve to the same factories.
    ///     Auto-attached observers also fire in children (dedup'd against the forwarded explicit
    ///     names so <c>--observer studio</c> does not double-attach). An empty list collapses to
    ///     <see cref="NullMeasurementObserver.Instance" />.
    /// </summary>
    private static IMeasurementObserver ResolveChildObservers(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            // No explicit names forwarded, but auto-attached observers still fire in the
            // child (e.g. a live-streaming observer referenced by the parent's entry
            // assembly). Resolve them with an empty dedup set.
            var autoAttachedOnly = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            return autoAttachedOnly.Count switch
            {
                0 => NullMeasurementObserver.Instance,
                1 => autoAttachedOnly[0],
                _ => new CompositeMeasurementObserver(autoAttachedOnly),
            };
        }

        var resolved = new List<IMeasurementObserver>(names.Count);

        foreach (var name in names)
        {
            if (ObserverRegistry.TryCreate(name, out var observer)
                && observer != NullMeasurementObserver.Instance)
                resolved.Add(observer);
        }

        // Auto-attached observers also fire in children. EnsureExtensionsLoaded (called by
        // CreateAutoAttachedObservers) has loaded NBenchmark.* assemblies (including
        // NBenchmark.Studio, if referenced) and their [ModuleInitializer]s have registered
        // auto-attached observers. Dedup against the request's explicit observer names.
        var explicitNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var autoAttached = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);
        resolved.AddRange(autoAttached);

        return resolved.Count switch
        {
            0 => NullMeasurementObserver.Instance,
            1 => resolved[0],
            _ => new CompositeMeasurementObserver(resolved),
        };
    }

    /// <summary>
    ///     Tags every subsequent benchmark added to the suite with the supplied categories.
    ///     <c>.WithCategories()</c> does not affect benchmarks already added.
    /// </summary>
    public BenchmarkSuite WithCategories(params string[] categories)
    {
        _pendingCategories = NormalizeCategories(categories, nameof(categories));
        return this;
    }

    /// <summary>
    ///     Filters the suite by category before running. Include rules are OR: a benchmark
    ///     runs if it has any included category. Exclude rules are also OR: a benchmark is
    ///     removed if it has any excluded category. Untagged benchmarks are excluded when
    ///     any include filter is set.
    /// </summary>
    public BenchmarkSuite WithCategoryFilter(IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
    {
        if (include is not null)
            AddCategories(_categoryFilterInclude, include, nameof(include));

        if (exclude is not null)
            AddCategories(_categoryFilterExclude, exclude, nameof(exclude));

        return this;
    }

    /// <summary>
    ///     Runs the whole suite in a dedicated child process for a clean-room reading,
    ///     rather than in the current process. The suite's setup, every benchmark, and the
    ///     suite's teardown all execute together in that one child; the parent process
    ///     reads the per-benchmark samples back and computes significance and reports as
    ///     usual. Defaults to enabled when called with no argument.
    /// </summary>
    public BenchmarkSuite WithIsolation(bool enabled = true)
    {
        _isolated = enabled;
        return this;
    }

    /// <summary>
    ///     Runs the suite benchmarks under each specified runtime and compares results.
    ///     Cross-runtime execution always uses child processes (each runtime is built via
    ///     <c>dotnet build -f &lt;tfm&gt;</c> and run in a separate child process),
    ///     regardless of the <see cref="WithIsolation" /> setting.
    /// </summary>
    public BenchmarkSuite WithRuntimes(params RuntimeMoniker[] runtimes)
    {
        _runtimes = runtimes;
        return this;
    }

    /// <summary>
    ///     Runs every benchmark in the suite and returns their results. When
    ///     <see cref="WithIsolation" /> is enabled the suite runs in a dedicated child
    ///     process; otherwise it runs in the current process.
    /// </summary>
    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => IsolatedRunContext.WithCurrentRequestAsync(() =>
            RunCoreAsync(callerFilePath, callerLineNumber, callerMemberName, cancellationToken));

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        ValidateBaseline();

        var invocationOrdinal = IsolatedRunContext.NextSuiteInvocationOrdinal();

        if (IsolatedRunContext.IsActive)
        {
            var isTarget = IsolatedRunContext.IsSuiteRequestMatch(
                invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, Name);

            // Resolve observers the parent forwarded by name (registry-resolvable only; the
            // suite's programmatic observers are instances and cannot cross a process
            // boundary). An empty list collapses to NullMeasurementObserver.Instance.
            var childObserver = IsolatedRunContext.TryGetActiveRequest(out var childRequest)
                ? ResolveChildObservers(childRequest.ObserverNames)
                : NullMeasurementObserver.Instance;

            var results = await RunInProcessCoreAsync(
                NullBenchmarkProgress.Instance,
                childObserver,
                RunOrder.Declaration,
                false,
                false,
                isTarget,
                cancellationToken).ConfigureAwait(false);

            // Stamp RuntimeMoniker on child results before returning/writing payload.
            if (childRequest is not null && childRequest.RuntimeMoniker is { } runtimeMoniker)
            {
                var tfm = runtimeMoniker.ToTargetFramework();
                results = results.Select(r => r with { RuntimeMoniker = tfm }).ToList();
            }

            return results;
        }

        // When runtimes are specified, delegate to the multi-runtime orchestrator.
        if (_runtimes.Count > 0)
        {
            return await RunMultiRuntimeSuiteAsync(
                    invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_isolated)
        {
            return await RunIsolatedParentAsync(
                    invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        using var observer = ResolveObserver();

        return await RunInProcessCoreAsync(
            _progress,
            observer,
            _runOrder,
            true,
            true,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunInProcessCoreAsync(
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        RunOrder order,
        bool applySignificance,
        bool applyReporters,
        bool writeChildPayload,
        CancellationToken cancellationToken)
    {
        var expanded = ExpandEnvelopes();
        var ordered = ApplyExecutionOrder(expanded, order);
        var filteredBenchmarks = ApplyCategoryFilter(ordered);
        var envelopeNames = filteredBenchmarks.Select(b => b.Name).ToList();

        NBenchmarkDiagnostics.OnSuiteStarting(Name, filteredBenchmarks.Count, _options.Profile.ToString(),
            _runtimes.Count > 0 ? string.Join(",", _runtimes.Select(r => r.ToTargetFramework())) : null, runOrder: _runOrder.ToString());

        List<BenchmarkResult> results = [];
        var sentinelEmitted = false;

        try
        {
            await progress.OnSuiteStarting(envelopeNames, filteredBenchmarks.Count).ConfigureAwait(false);

            // Apply opt-in hardware/OS controls for the duration of the in-process run. The
            // scope restores the prior process state on dispose. Isolated suite children
            // re-run this same entry point and re-derive _options, so they apply the same
            // settings themselves; Host-mode children receive settings via MeasurementOverrides.
            using var _ = EnvironmentControl.Apply(_options.Environment);

            _suiteSetup?.Invoke();

            var envelopes = filteredBenchmarks
                .Select(b => b with { IsBaseline = _baselineName is not null && b.OriginalName == _baselineName })
                .ToList();

            Dictionary<string, double[]> rawSamples;

            try
            {
                var effectiveOrder = _parameterDefs.Count > 0 ? RunOrder.Declaration : order;

                if (_options.LaunchCount > 1)
                {
                    var allLaunchResults = new List<IReadOnlyList<BenchmarkResult>>();
                    var allLaunchSamples = new List<Dictionary<string, double[]>>();

                    for (var launchIdx = 0; launchIdx < _options.LaunchCount; launchIdx++)
                    {
                        var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;

                        var (launchResults, launchSamples) = await SuiteRunner.RunAsync(
                            envelopes, effectiveOrder, null, _options, 0,
                            filteredBenchmarks.Count, NullBenchmarkProgress.Instance, cancellationToken,
                            null, launchObserver).ConfigureAwait(false);

                        allLaunchResults.Add(launchResults);
                        allLaunchSamples.Add(launchSamples);
                    }

                    (results, rawSamples) = AggregateSuiteLaunches(allLaunchResults, allLaunchSamples);
                }
                else
                {
                    (results, rawSamples) = await SuiteRunner.RunAsync(
                        envelopes, effectiveOrder, null, _options, 0,
                        filteredBenchmarks.Count, progress, cancellationToken,
                        null, observer).ConfigureAwait(false);
                }
            }
            finally
            {
                _suiteTeardown?.Invoke();
            }

            await progress.OnSuiteCompleted(results).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true. A
            // live-streaming observer treats this as the authoritative run-end signal.
            observer.OnPhase(new MeasurementPhaseEvent(
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
                Succeeded: true));

            sentinelEmitted = true;

            // SuiteRunner keys raw samples by benchmark name; the significance path needs the
            // composite name+runtime key so multi-runtime results don't collide.
            rawSamples = RawSampleKey.ToComposite(results, rawSamples);

            if (applySignificance)
                ApplyPerParameterSignificance(results, rawSamples);

            if (writeChildPayload)
            {
                await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(
                        results,
                        r => rawSamples.GetValueOrDefault(RawSampleKey.For(r), []),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (applyReporters)
                await InvokeReportersAsync(results, cancellationToken).ConfigureAwait(false);

            return results;
        }
        finally
        {
            // If the try block did not reach its success-path emit (a suite-level
            // exception prevented it), emit the sentinel here with Succeeded = false so a
            // live-streaming observer can finalise the run as Failed rather than leaving it
            // stuck as Running until the idle timeout fires.
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    string.Empty,
                    MeasurementPhase.SuiteCompleted,
                    PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(results);
        }
    }

    private static (List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples) AggregateSuiteLaunches(
        IReadOnlyList<IReadOnlyList<BenchmarkResult>> allLaunchResults,
        IReadOnlyList<Dictionary<string, double[]>> allLaunchSamples)
    {
        if (allLaunchResults.Count == 0)
            return ([], []);

        var names = allLaunchResults[0].Select(r => r.Name).ToList();
        var aggregated = new List<BenchmarkResult>(names.Count);
        var rawSamples = new Dictionary<string, double[]>(names.Count);
        var pooledSamples = PoolRawSamplesByName(allLaunchSamples);

        foreach (var name in names)
        {
            var perLaunch = allLaunchResults
                .Select(launch => launch.FirstOrDefault(r => r.Name == name))
                .Where(r => r is not null)
                .Cast<BenchmarkResult>()
                .ToList();

            if (perLaunch.Count == 0)
                continue;

            var stats = LaunchAggregator.Aggregate(perLaunch);
            var best = LaunchAggregator.BestLaunch(perLaunch);
            aggregated.Add(best with { LaunchStatistics = stats });

            if (pooledSamples.TryGetValue(name, out var samples))
                rawSamples[RawSampleKey.For(name, best.RuntimeMoniker)] = samples;
        }

        return (aggregated, rawSamples);
    }

    private static Dictionary<string, double[]> PoolRawSamplesByName(
        IReadOnlyList<Dictionary<string, double[]>> perLaunchSamples)
    {
        var pooled = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var launch in perLaunchSamples)
        {
            foreach (var (name, samples) in launch)
            {
                if (!pooled.TryGetValue(name, out var bucket))
                {
                    bucket = [];
                    pooled[name] = bucket;
                }

                bucket.AddRange(samples);
            }
        }

        return pooled.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunIsolatedParentAsync(
        int invocationOrdinal,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        var expanded = ExpandEnvelopes();
        var filteredBenchmarks = ApplyCategoryFilter(expanded);
        var displayNames = filteredBenchmarks.Select(b => b.Name).ToList();

        NBenchmarkDiagnostics.OnSuiteStarting(Name, filteredBenchmarks.Count, _options.Profile.ToString(),
            _runtimes.Count > 0 ? string.Join(",", _runtimes.Select(r => r.ToTargetFramework())) : null, runOrder: _runOrder.ToString());

        var results = new List<BenchmarkResult>(filteredBenchmarks.Count);
        using var observer = ResolveObserver();
        var sentinelEmitted = false;

        try
        {
            await _progress.OnSuiteStarting(displayNames, filteredBenchmarks.Count).ConfigureAwait(false);

            var request = new IsolatedRunRequest
            {
                Kind = IsolatedRunKind.Suite,
                InvocationOrdinal = invocationOrdinal,
                CallerFilePath = callerFilePath,
                CallerLineNumber = callerLineNumber,
                CallerMemberName = callerMemberName,
                SuiteName = Name,
                BenchmarkDisplayNames = displayNames,
                Timeout = ChildProcessLauncher.ComputeTimeout(_options, displayNames.Count),
                RuntimeProfile = _options.RuntimeProfile,
            };

            IReadOnlyList<IsolatedResultItem> items;

            if (_options.LaunchCount > 1)
            {
                var allLaunchItems = new List<IReadOnlyList<IsolatedResultItem>>();

                for (var launchIdx = 0; launchIdx < _options.LaunchCount; launchIdx++)
                {
                    var launchItems = await ChildProcessLauncher.LaunchAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                    allLaunchItems.Add(launchItems);
                }

                items = AggregateIsolatedLaunches(allLaunchItems, displayNames, filteredBenchmarks);
            }
            else
                items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);

            var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

            var rawSamples = new Dictionary<string, double[]>(filteredBenchmarks.Count);

            for (var i = 0; i < filteredBenchmarks.Count; i++)
            {
                var envelope = filteredBenchmarks[i];
                var isBaseline = _baselineName is not null && envelope.OriginalName == _baselineName;

                await _progress.OnBenchmarkStarting(envelope.Name, i + 1, filteredBenchmarks.Count).ConfigureAwait(false);

                BenchmarkResult result;
                double[] raw;

                if (byName.TryGetValue(envelope.Name, out var item))
                {
                    // Re-attach display samples the child stripped from its serialized result.
                    // For launch-aggregated items, prefer the representative launch samples kept
                    // on Result so TrimmedOrdinals stay aligned with the shown distribution.
                    result = item.Result with
                    {
                        IsBaseline = isBaseline,
                        Description = envelope.Description,
                        RawSamples = ResolveResultRawSamples(item),
                    };
                    raw = item.RawSamples;
                }
                else
                {
                    var message = $"Isolated child did not return a result for '{envelope.Name}'.";

                    result = OutcomeBuilder.Build(
                        new RunOutcome.Errored(new InvalidOperationException(message), message),
                        envelope.Name, envelope.ClassName, envelope.Description, isBaseline,
                        _options, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                        envelope.Categories).Result;

                    raw = [];
                }

                result = result with { ParameterSet = envelope.ParameterSet };
                results.Add(result);
                rawSamples[RawSampleKey.For(envelope.Name, result.RuntimeMoniker)] = raw;

                await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
                observer.OnResult(result);
            }

            await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true.
            observer.OnPhase(new MeasurementPhaseEvent(
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
                Succeeded: true));

            sentinelEmitted = true;

            ApplyPerParameterSignificance(results, rawSamples);

            await InvokeReportersAsync(results, cancellationToken).ConfigureAwait(false);

            return results;
        }
        finally
        {
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    string.Empty,
                    MeasurementPhase.SuiteCompleted,
                    PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(results);
        }
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunMultiRuntimeSuiteAsync(
        int invocationOrdinal,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        Console.WriteLine($"Building for runtimes: {string.Join(", ", _runtimes.Select(r => r.ToTargetFramework()))}");

        var builds = await MultiRuntimeOrchestrator
            .BuildForRuntimesAsync(_runtimes, cancellationToken).ConfigureAwait(false);

        var failedBuilds = builds.Where(b => b.Error is not null).ToList();

        foreach (var failed in failedBuilds)
        {
            Console.Error.WriteLine($"  {failed.Moniker.ToTargetFramework()}: {failed.Error}");
        }

        var successfulBuilds = builds.Where(b => b.DllPath is not null).ToList();

        if (successfulBuilds.Count == 0)
        {
            Console.Error.WriteLine("All runtime builds failed.");
            return [];
        }

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var expanded = ExpandEnvelopes();
        var filteredBenchmarks = ApplyCategoryFilter(expanded);
        var envelopeNames = filteredBenchmarks.Select(b => b.Name).ToList();

        NBenchmarkDiagnostics.OnSuiteStarting(Name, filteredBenchmarks.Count, _options.Profile.ToString(),
            _runtimes.Count > 0 ? string.Join(",", _runtimes.Select(r => r.ToTargetFramework())) : null, runOrder: _runOrder.ToString());

        using var observer = ResolveObserver();
        var sentinelEmitted = false;

        try
        {
            await _progress.OnSuiteStarting(envelopeNames, filteredBenchmarks.Count).ConfigureAwait(false);

            // Apply opt-in hardware/OS controls to the parent process for the duration of the
            // multi-runtime run, mirroring the single-runtime suite path. Each spawned child
            // re-runs this entry point and re-derives _options, so it applies the same settings
            // itself; this scope covers the parent's own launch/aggregation work.
            using var _ = EnvironmentControl.Apply(_options.Environment);

            _suiteSetup?.Invoke();

            try
            {
                foreach (var build in successfulBuilds)
                {
                    var tfm = build.Moniker.ToTargetFramework();

                    try
                    {
                        Console.WriteLine($"  Running under {tfm}...");

                        var request = new IsolatedRunRequest
                        {
                            Kind = IsolatedRunKind.Suite,
                            InvocationOrdinal = invocationOrdinal,
                            CallerFilePath = callerFilePath,
                            CallerLineNumber = callerLineNumber,
                            CallerMemberName = callerMemberName,
                            SuiteName = Name,
                            BenchmarkDisplayNames = envelopeNames,
                            RuntimeMoniker = build.Moniker,
                            EntryAssemblyPath = build.DllPath,
                            Timeout = ChildProcessLauncher.ComputeTimeout(_options, envelopeNames.Count),
                            RuntimeProfile = _options.RuntimeProfile,
                        };

                        IReadOnlyList<IsolatedResultItem> items;

                        if (_options.LaunchCount > 1)
                        {
                            var allLaunchItems = new List<IReadOnlyList<IsolatedResultItem>>();

                            for (var launchIdx = 0; launchIdx < _options.LaunchCount; launchIdx++)
                            {
                                var launchItems = await ChildProcessLauncher.LaunchAsync(request, cancellationToken)
                                    .ConfigureAwait(false);

                                allLaunchItems.Add(launchItems);
                            }

                            items = AggregateIsolatedLaunches(allLaunchItems, envelopeNames, filteredBenchmarks);
                        }
                        else
                        {
                            items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

                        for (var i = 0; i < filteredBenchmarks.Count; i++)
                        {
                            var envelope = filteredBenchmarks[i];
                            var isBaseline = _baselineName is not null && envelope.OriginalName == _baselineName;

                            await _progress.OnBenchmarkStarting(envelope.Name, i + 1, filteredBenchmarks.Count)
                                .ConfigureAwait(false);

                            BenchmarkResult result;

                            if (byName.TryGetValue(envelope.Name, out var item))
                            {
                                result = item.Result with
                                {
                                    IsBaseline = isBaseline,
                                    Description = envelope.Description,
                                    RuntimeMoniker = tfm,
                                    RawSamples = ResolveResultRawSamples(item),
                                };

                                if (item.RawSamples.Length > 0)
                                    rawSamples[RawSampleKey.For(envelope.Name, tfm)] = item.RawSamples;
                            }
                            else
                            {
                                var message = $"Isolated child did not return a result for '{envelope.Name}'.";

                                result = OutcomeBuilder.Build(
                                        new RunOutcome.Errored(new InvalidOperationException(message), message),
                                        envelope.Name, envelope.ClassName, envelope.Description, isBaseline,
                                        _options, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                                        envelope.Categories).Result with
                                {
                                    RuntimeMoniker = tfm,
                                };
                            }

                            result = result with { ParameterSet = envelope.ParameterSet };
                            allResults.Add(result);

                            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
                            observer.OnResult(result);
                        }
                    }
                    finally
                    {
                        MultiRuntimeOrchestrator.TryDeleteBuildOutput(build.OutputDirectory);
                    }
                }
            }
            finally
            {
                _suiteTeardown?.Invoke();
            }

            await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true.
            observer.OnPhase(new MeasurementPhaseEvent(
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
                Succeeded: true));

            sentinelEmitted = true;

            ApplyPerParameterSignificance(allResults, rawSamples);

            await InvokeReportersAsync(allResults, cancellationToken).ConfigureAwait(false);

            return allResults;
        }
        finally
        {
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    string.Empty,
                    MeasurementPhase.SuiteCompleted,
                    PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(allResults);
        }
    }

    private static IReadOnlyList<IsolatedResultItem> AggregateIsolatedLaunches(
        IReadOnlyList<IReadOnlyList<IsolatedResultItem>> allLaunchItems,
        IReadOnlyList<string> displayNames,
        IReadOnlyList<BenchmarkEnvelope> filteredBenchmarks)
    {
        if (allLaunchItems.Count == 0)
            return [];

        var aggregated = new List<IsolatedResultItem>();

        foreach (var name in displayNames)
        {
            var perLaunchResults = new List<BenchmarkResult>();

            foreach (var launchItems in allLaunchItems)
            {
                var match = launchItems.FirstOrDefault(item => item.Result.Name == name);

                if (match is not null)
                    perLaunchResults.Add(match.Result with { RawSamples = match.RawSamples });
            }

            if (perLaunchResults.Count == 0)
            {
                var envelope = filteredBenchmarks.FirstOrDefault(e => e.Name == name);

                if (envelope is not null)
                {
                    var message = $"Isolated child did not return a result for '{name}' in any launch.";

                    aggregated.Add(new IsolatedResultItem
                    {
                        Result = OutcomeBuilder.Build(
                            new RunOutcome.Errored(new InvalidOperationException(message), message),
                            name, envelope.ClassName, envelope.Description, envelope.IsBaseline,
                            new MeasurementOptions(), TimeSpan.Zero, TimeSpan.Zero, 0, null,
                            envelope.Categories).Result,
                        RawSamples = [],
                    });
                }

                continue;
            }

            var stats = LaunchAggregator.Aggregate(perLaunchResults);
            var best = LaunchAggregator.BestLaunch(perLaunchResults);
            var rawSamples = allLaunchItems
                .SelectMany(launchItems => launchItems.Where(item => item.Result.Name == name))
                .SelectMany(item => item.RawSamples)
                .ToArray();

            // Keep representative-launch samples on the displayed result so statistical fields
            // and TrimmedOrdinals remain aligned; pooled samples still travel alongside for
            // significance calculations.
            var aggregatedResult = best with { LaunchStatistics = stats };

            aggregated.Add(new IsolatedResultItem
            {
                Result = aggregatedResult,
                RawSamples = rawSamples,
            });
        }

        return aggregated;
    }

    private static IReadOnlyList<double> ResolveResultRawSamples(IsolatedResultItem item)
    {
        return item.Result.RawSamples.Count > 0 ? item.Result.RawSamples : item.RawSamples;
    }

    // --- Parameter expansion ---

    private List<BenchmarkEnvelope> ExpandEnvelopes()
    {
        if (_parameterDefs.Count == 0)
        {
            if (_parameterizedFactories.Count > 0)
            {
                throw new InvalidOperationException(
                    "Parameterized benchmarks were registered but no WithParameter call was made. " +
                    "Add parameter values with WithParameter before running the suite, " +
                    "or register benchmarks without typed lambda parameters.");
            }

            return [.. _benchmarks];
        }

        if (_parameterizedFactories.Count == 0)
        {
            throw new InvalidOperationException(
                "WithParameter was called but no parameterized benchmarks (Add with typed lambda) were registered.");
        }

        var combinations = ComputeParameterCombinations();
        var usedNames = new HashSet<string>(_benchmarks.Select(b => b.Name));
        var expanded = new List<BenchmarkEnvelope>();

        var parameterTypes = _parameterDefs.Select(d => d.Type).ToArray();
        var compatibleFactories = new List<ParameterizedAdd>();

        foreach (var factory in _parameterizedFactories)
        {
            if (factory.ParamTypes.Length != _parameterDefs.Count)
                continue;

            if (!AreTypesCompatible(factory.ParamTypes, parameterTypes))
            {
                throw new InvalidOperationException(
                    $"Benchmark '{factory.Name}' parameter types ({string.Join(", ", factory.ParamTypes.Select(t => t.Name))}) " +
                    $"do not match the registered WithParameter types ({string.Join(", ", _parameterDefs.Select(d => d.Type.Name))}).");
            }

            compatibleFactories.Add(factory);
        }

        foreach (var combo in combinations)
        {
            var paramSet = new BenchmarkParameter[combo.Length];

            for (var i = 0; i < combo.Length; i++)
            {
                paramSet[i] = new BenchmarkParameter(_parameterDefs[i].Name, combo[i]);
            }

            foreach (var factory in compatibleFactories)
            {
                var displayName = FormatParamDisplayName(factory.Name, paramSet);

                if (!usedNames.Add(displayName))
                {
                    throw new ArgumentException(
                        $"Duplicate benchmark name after parameter expansion: '{displayName}'. " +
                        "Ensure parameter values produce unique display names.");
                }

                var envelope = factory.Factory(combo.ToArray(), displayName);

                expanded.Add(envelope with
                {
                    OriginalName = factory.Name,
                    ParameterSet = paramSet,
                    IsBaseline = false,
                });
            }
        }

        if (expanded.Count == 0)
        {
            throw new InvalidOperationException(
                "No benchmarks matched the registered parameters. " +
                "Ensure the typed lambda parameters match the WithParameter type arguments.");
        }

        return [.. _benchmarks, .. expanded];
    }

    private List<object?[]> ComputeParameterCombinations()
    {
        var result = new List<object?[]>();
        result.Add([]);

        foreach (var def in _parameterDefs)
        {
            var next = new List<object?[]>();

            foreach (var existing in result)
            {
                foreach (var value in def.Values)
                {
                    var combined = new object?[existing.Length + 1];
                    Array.Copy(existing, combined, existing.Length);
                    combined[^1] = value;
                    next.Add(combined);
                }
            }

            result = next;
        }

        return result;
    }

    private static string FormatParamDisplayName(string benchmarkName, BenchmarkParameter[] paramSet)
        => BenchmarkParameter.FormatDisplayName(benchmarkName, paramSet);

    private async Task InvokeReportersAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
    {
        await ReporterRegistry.InvokeReportersAsync(_reporters, _detail, results, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplyPerParameterSignificance(List<BenchmarkResult> results, Dictionary<string, double[]> rawSamples)
    {
        static string RawKey(BenchmarkResult r)
        {
            return RawSampleKey.For(r);
        }

        if (!results.Any(r => r.ParameterSet.Count > 0))
        {
            // Significance only makes sense within the same runtime; net8 vs net10 is not
            // a meaningful comparison for p-value purposes.
            foreach (var runtimeGroup in results.GroupBy(ComparisonGroup.KeyFor))
            {
                var runtimeList = runtimeGroup.ToList();
                var runtimeRaw = new Dictionary<string, double[]>();

                foreach (var r in runtimeList)
                {
                    if (rawSamples.TryGetValue(RawKey(r), out var samples))
                        runtimeRaw[r.Name] = samples;
                }

                var indices = results
                    .Select((res, idx) => (res, idx))
                    .Where(x => ComparisonGroup.KeyFor(x.res) == runtimeGroup.Key)
                    .Select(x => x.idx)
                    .ToList();

                Significance.ApplyIfEnabled(runtimeList, runtimeRaw, _options);

                for (var j = 0; j < runtimeList.Count; j++)
                {
                    results[indices[j]] = runtimeList[j];
                }
            }

            return;
        }

        var indexedResults = results
            .Select((r, idx) => (Result: r, Index: idx))
            .ToList();

        var groups = indexedResults
            .GroupBy(ri => BenchmarkParameter.GetKey(ri.Result.ParameterSet))
            .ToList();

        foreach (var group in groups)
        {
            foreach (var runtimeGroup in group.GroupBy(ri => ComparisonGroup.KeyFor(ri.Result)))
            {
                var groupList = runtimeGroup.ToList();
                var groupResults = groupList.Select(ri => ri.Result).ToList();
                var groupRaw = new Dictionary<string, double[]>();

                foreach (var ri in groupList)
                {
                    if (rawSamples.TryGetValue(RawKey(ri.Result), out var samples))
                        groupRaw[ri.Result.Name] = samples;
                }

                Significance.ApplyIfEnabled(groupResults, groupRaw, _options);

                for (var j = 0; j < groupList.Count; j++)
                {
                    results[groupList[j].Index] = groupResults[j];
                }
            }
        }
    }

    private static bool AreTypesCompatible(Type[] factoryTypes, Type[] parameterTypes)
    {
        if (factoryTypes.Length != parameterTypes.Length)
            return false;

        for (var i = 0; i < factoryTypes.Length; i++)
        {
            if (factoryTypes[i] != parameterTypes[i])
                return false;
        }

        return true;
    }

    private List<BenchmarkEnvelope> ApplyExecutionOrder(IReadOnlyList<BenchmarkEnvelope> expanded, RunOrder order)
    {
        if (order == RunOrder.Declaration || _parameterDefs.Count == 0)
            return [.. expanded];

        // Group by parameter set, then shuffle within each group.
        var parameterGroups = expanded
            .GroupBy(e => BenchmarkParameter.GetKey(e.ParameterSet))
            .ToList();

        var ordered = new List<BenchmarkEnvelope>(expanded.Count);
        var groupSeedRng = new Random(Random.Shared.Next());

        foreach (var group in parameterGroups)
        {
            var shuffled = ShuffleEnvelopes(group.ToList(), groupSeedRng.Next());
            ordered.AddRange(shuffled);
        }

        return ordered;
    }

    private static List<BenchmarkEnvelope> ShuffleEnvelopes(List<BenchmarkEnvelope> items, int seed)
    {
        var rng = new Random(seed);
        var list = items.ToList();

        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    // --- Validation ---

    private static void ValidateParameterType<T>(string name, T[] values)
    {
        foreach (var value in values)
        {
            if (!IsSupportedParameterType(value))
            {
                throw new ArgumentException(
                    $"Parameter values must be primitives, enums, strings, or null. " +
                    $"Value of type '{value?.GetType().FullName ?? "null"}' for parameter '{name}' is not supported.",
                    name);
            }
        }
    }

    private static bool IsSupportedParameterType(object? value) => value switch
    {
        null => true,
        bool => true,
        byte or sbyte or short or ushort or int or uint or long or ulong => true,
        float or double or decimal => true,
        char or string => true,
        Enum => true,
        _ => false,
    };

    private IReadOnlyList<BenchmarkEnvelope> ApplyCategoryFilter(IReadOnlyList<BenchmarkEnvelope> benchmarks)
    {
        if (_categoryFilterInclude.Count == 0 && _categoryFilterExclude.Count == 0)
            return benchmarks;

        return benchmarks
            .Where(b => CategoryFilter.Matches(b.Categories, _categoryFilterInclude, _categoryFilterExclude, _categoryFilterInclude.Count > 0))
            .ToList();
    }

    private static IReadOnlyList<string> ResolveAddCategories(
        IReadOnlyList<string>? explicitCategories,
        IReadOnlyList<string>? pendingCategories)
    {
        if (explicitCategories is not null)
            return NormalizeCategories(explicitCategories, "categories");

        if (pendingCategories is null)
            return [];

        return pendingCategories.ToArray();
    }

    private static string[] NormalizeCategories(IEnumerable<string> categories, string paramName)
    {
        var normalized = new List<string>();

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category names cannot be null, empty, or whitespace.", paramName);

            var trimmed = category.Trim();

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                normalized.Add(trimmed);
        }

        return [.. normalized];
    }

    private static void AddCategories(List<string> target, IEnumerable<string> source, string paramName)
    {
        foreach (var category in NormalizeCategories(source, paramName))
        {
            if (!target.Contains(category, StringComparer.OrdinalIgnoreCase))
                target.Add(category);
        }
    }

    private void ValidateBaseline()
    {
        if (_baselineName is null)
            return;

        var allNames = new HashSet<string>(_benchmarks.Select(b => b.Name));
        allNames.UnionWith(_parameterizedFactories.Select(f => f.Name));

        if (!allNames.Contains(_baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", allNames));
        }
    }

    private sealed record ParameterDef(string Name, Type Type, object?[] Values);

    private sealed record ParameterizedAdd(
        string Name,
        IReadOnlyList<string> Categories,
        Func<object?[], string, BenchmarkEnvelope> Factory,
        Type[] ParamTypes);
}
