using System.Linq;
using System.Runtime.CompilerServices;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEnvelope> _benchmarks = [];
    private readonly List<string> _categoryFilterExclude = [];
    private readonly List<string> _categoryFilterInclude = [];

    private readonly List<IReporter> _reporters = [];
    private string? _baselineName;
    private ReportDetail _detail;
    private bool _isolated;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private string[]? _pendingCategories;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    private readonly List<ParameterDef> _parameterDefs = [];
    private readonly List<ParameterizedAdd> _parameterizedFactories = [];

    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

    private sealed record ParameterDef(string Name, Type Type, object?[] Values);

    private sealed record ParameterizedAdd(
        string Name,
        IReadOnlyList<string> Categories,
        Func<object?[], string, BenchmarkEnvelope> Factory,
        Type[] ParamTypes);

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

            return await RunInProcessCoreAsync(
                NullBenchmarkProgress.Instance,
                RunOrder.Declaration,
                false,
                false,
                isTarget,
                cancellationToken).ConfigureAwait(false);
        }

        if (_isolated)
        {
            return await RunIsolatedParentAsync(
                    invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        return await RunInProcessCoreAsync(
            _progress,
            _runOrder,
            true,
            true,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunInProcessCoreAsync(
        IBenchmarkProgress progress,
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
        await progress.OnSuiteStarting(envelopeNames, filteredBenchmarks.Count).ConfigureAwait(false);

        _suiteSetup?.Invoke();

        var envelopes = filteredBenchmarks
            .Select(b => b with { IsBaseline = _baselineName is not null && b.OriginalName == _baselineName })
            .ToList();

        List<BenchmarkResult> results;
        Dictionary<string, double[]> rawSamples;

        try
        {
            var effectiveOrder = _parameterDefs.Count > 0 ? RunOrder.Declaration : order;
            (results, rawSamples) = await SuiteRunner.RunAsync(
                envelopes, effectiveOrder, null, _options, 0,
                filteredBenchmarks.Count, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _suiteTeardown?.Invoke();
        }

        ApplyParameterSetsToResults(results, envelopes);

        await progress.OnSuiteCompleted(results).ConfigureAwait(false);

        if (applySignificance)
            ApplyPerParameterSignificance(results, rawSamples);

        if (writeChildPayload)
        {
            await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(results, rawSamples, cancellationToken)
                .ConfigureAwait(false);
        }

        if (applyReporters)
        {
            foreach (var reporter in _reporters)
            {
                await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
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
        };

        var items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

        var results = new List<BenchmarkResult>(filteredBenchmarks.Count);
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
                result = item.Result with { IsBaseline = isBaseline, Description = envelope.Description };
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
            rawSamples[envelope.Name] = raw;

            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        ApplyPerParameterSignificance(results, rawSamples);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
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
                paramSet[i] = new BenchmarkParameter(_parameterDefs[i].Name, combo[i]);

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
                var capturedParamSet = paramSet;

                expanded.Add(envelope with
                {
                    OriginalName = factory.Name,
                    ParameterSet = paramSet,
                    IsBaseline = false,
                    RunAsync = (spec, ct) =>
                    {
                        var task = envelope.RunAsync(spec, ct);
                        return WithParameterSet(task, capturedParamSet);
                    },
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
    {
        if (paramSet.Length == 1)
            return $"{benchmarkName} ({paramSet[0].Name}={BenchmarkParameter.FormatValue(paramSet[0].Value)})";

        var parts = paramSet.Select(p => $"{p.Name}={BenchmarkParameter.FormatValue(p.Value)}");
        return $"{benchmarkName} ({string.Join(", ", parts)})";
    }

    private static string GetParameterSetKey(IReadOnlyList<BenchmarkParameter> paramSet)
    {
        if (paramSet.Count == 0)
            return "";

        return string.Join("\u001F", paramSet.Select(p => $"{p.Name}={p.Value}"));
    }

    private static void ApplyParameterSetsToResults(List<BenchmarkResult> results, List<BenchmarkEnvelope> envelopes)
    {
        var byName = envelopes.ToDictionary(e => e.Name);
        for (var i = 0; i < results.Count; i++)
        {
            if (byName.TryGetValue(results[i].Name, out var match) && match.ParameterSet.Count > 0)
                results[i] = results[i] with { ParameterSet = match.ParameterSet };
        }
    }

    private void ApplyPerParameterSignificance(List<BenchmarkResult> results, Dictionary<string, double[]> rawSamples)
    {
        if (!results.Any(r => r.ParameterSet.Count > 0))
        {
            Significance.ApplyIfEnabled(results, rawSamples, _options);
            return;
        }

        var indexedResults = results
            .Select((r, idx) => (Result: r, Index: idx))
            .ToList();

        var groups = indexedResults
            .GroupBy(ri => GetParameterSetKey(ri.Result.ParameterSet))
            .ToList();

        foreach (var group in groups)
        {
            var groupList = group.ToList();
            var groupResults = groupList.Select(ri => ri.Result).ToList();
            var groupRaw = new Dictionary<string, double[]>();
            foreach (var ri in groupList)
            {
                if (rawSamples.TryGetValue(ri.Result.Name, out var samples))
                    groupRaw[ri.Result.Name] = samples;
            }

            Significance.ApplyIfEnabled(groupResults, groupRaw, _options);

            for (var j = 0; j < groupList.Count; j++)
                results[groupList[j].Index] = groupResults[j];
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
            .GroupBy(e => GetParameterSetKey(e.ParameterSet))
            .ToList();

        var ordered = new List<BenchmarkEnvelope>(expanded.Count);
        var seed = Random.Shared.Next();

        foreach (var group in parameterGroups)
        {
            var shuffled = ShuffleEnvelopes(group.ToList(), seed ^ group.Key.GetHashCode());
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

    private static async Task<MeasurementOutcome> WithParameterSet(Task<MeasurementOutcome> task, BenchmarkParameter[] paramSet)
    {
        var outcome = await task.ConfigureAwait(false);
        return new MeasurementOutcome
        {
            Result = outcome.Result with { ParameterSet = paramSet },
            RawSamples = outcome.RawSamples,
        };
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
}