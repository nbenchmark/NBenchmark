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

    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

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
        if (_benchmarks.Any(b => b.Name == name))
        {
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique - significance testing keys raw samples by name.",
                nameof(name));
        }
    }

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

        // Inside an isolated child: run the suite in-process and quietly. Only the suite
        // call the parent requested writes its samples back; any other suite call sharing
        // this child runs without emitting output or a payload. The child re-applies the
        // category filter on top of the display-name list the parent already filtered;
        // this is safe because the suite builder state is rebuilt deterministically in
        // the child before this point.
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
        var filteredBenchmarks = ApplyCategoryFilter(_benchmarks);
        var envelopeNames = filteredBenchmarks.Select(b => b.Name).ToList();
        await progress.OnSuiteStarting(envelopeNames, filteredBenchmarks.Count).ConfigureAwait(false);

        _suiteSetup?.Invoke();

        var envelopes = filteredBenchmarks
            .Select(b => b with { IsBaseline = _baselineName is not null && b.Name == _baselineName })
            .ToList();

        List<BenchmarkResult> results;
        Dictionary<string, double[]> rawSamples;

        try
        {
            (results, rawSamples) = await SuiteRunner.RunAsync(
                envelopes, order, null, _options, 0,
                filteredBenchmarks.Count, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Teardown is guaranteed once setup has succeeded - including on
            // cancellation - mirroring host mode's per-class lifecycle.
            _suiteTeardown?.Invoke();
        }

        await progress.OnSuiteCompleted(results).ConfigureAwait(false);

        if (writeChildPayload)
        {
            await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(results, rawSamples, cancellationToken)
                .ConfigureAwait(false);
        }

        if (applySignificance)
            Significance.ApplyIfEnabled(results, rawSamples, _options);

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

        var filteredBenchmarks = ApplyCategoryFilter(_benchmarks);
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
            var isBaseline = _baselineName is not null && envelope.Name == _baselineName;

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

            results.Add(result);
            rawSamples[envelope.Name] = raw;

            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        Significance.ApplyIfEnabled(results, rawSamples, _options);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

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
        if (_baselineName is not null && _benchmarks.All(b => b.Name != _baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));
        }
    }
}
