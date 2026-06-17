using System.Diagnostics;
using System.Reflection;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkHost
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [];
    private readonly List<string> _categoryFilterInclude = [];
    private readonly List<string> _categoryFilterExclude = [];
    private CliArgs _cliArgs = new();
    private ReportDetail _detail;
    private Func<Type, InstanceHandle>? _instanceFactory;
    private InstanceLifetime _defaultInstanceLifetime = InstanceLifetime.PerMethod;
    private bool _isolationEnabled = true;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;

    private BenchmarkHost()
    {
    }

    internal Action? PostSuiteCleanup { get; set; }

    public static BenchmarkHost Create(string[] args)
    {
        var cliArgs = CliArgs.Parse(args);
        var host = new BenchmarkHost();
        host._cliArgs = cliArgs;
        host._detail = cliArgs.Detail;

        foreach (var name in cliArgs.ReporterNames)
        {
            if (ReporterRegistry.TryCreate(name, null, cliArgs.Detail, out var reporter))
                host._reporters.Add(reporter);
        }

        return host;
    }

    public BenchmarkHost AddFromAssembly<T>()
    {
        _assemblies.Add(typeof(T).Assembly);
        return this;
    }

    public BenchmarkHost AddFromAssembly(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    public BenchmarkHost WithReporter(IReporter reporter)
    {
        reporter.Detail = _detail;
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkHost WithOptions(MeasurementOptions options)
    {
        _options = options;
        return this;
    }

    public BenchmarkHost WithRunOrder(RunOrder order)
    {
        _runOrder = order;
        return this;
    }

    public BenchmarkHost WithDetail(ReportDetail detail)
    {
        _detail = detail;

        foreach (var reporter in _reporters)
        {
            reporter.Detail = detail;
        }

        return this;
    }

    public BenchmarkHost WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    public BenchmarkHost WithInstanceFactory(Func<Type, object> factory)
    {
        _instanceFactory = type => InstanceHandle.NoTeardown(factory(type));
        return this;
    }

    internal BenchmarkHost WithInstanceFactory(Func<Type, InstanceHandle> factory)
    {
        _instanceFactory = factory;
        return this;
    }

    /// <summary>
    ///     Requires a minimum strategy-defined practical effect in [0, 1] for a candidate
    ///     to be considered practically significant. Values below the threshold are reported
    ///     as NotSignificant with a <c>neg</c> magnitude label.
    /// </summary>
    public BenchmarkHost WithMinimumPracticalEffect(double minimumDelta)
    {
        _options = _options with { MinimumPracticalEffect = minimumDelta };
        return this;
    }

    /// <summary>
    ///     Sets the measurement profile, which bundles per-iteration GC, between-benchmark GC, and
    ///     allocation tracking. <see cref="MeasurementProfile.Realistic" /> (the default) keeps natural
    ///     GC pressure in the timing; <see cref="MeasurementProfile.Independent" /> isolates iterations
    ///     for pure-CPU measurement.
    /// </summary>
    public BenchmarkHost WithMeasurementProfile(MeasurementProfile profile)
    {
        _options = _options with { Profile = profile };
        return this;
    }

    /// <summary>
    ///     Tunes the adaptive measurement loop (warmup plateau, CI-width sample count, and
    ///     ops-per-sample calibration). Use <see cref="AutoTuneOptions.Quick" /> for fast feedback
    ///     or <see cref="AutoTuneOptions.Thorough" /> for tighter intervals.
    /// </summary>
    public BenchmarkHost WithAutoTune(AutoTuneOptions autoTune)
    {
        ArgumentNullException.ThrowIfNull(autoTune);
        _options = _options with { AutoTune = autoTune };
        return this;
    }

    /// <summary>Selects an adaptive-tuning preset (Default, Quick, or Thorough).</summary>
    public BenchmarkHost WithAutoTune(AutoTunePreset preset)
    {
        _options = _options with { AutoTune = AutoTuneOptions.FromPreset(preset) };
        return this;
    }

    /// <summary>
    ///     Pins the number of back-to-back body invocations timed as one sample (<c>K</c>),
    ///     overriding auto-calibration. Honoured even with per-iteration setup/teardown.
    /// </summary>
    public BenchmarkHost WithOpsPerSample(int opsPerSample)
    {
        _options = _options with { OpsPerSample = opsPerSample };
        return this;
    }

    /// <summary>
    ///     Controls Host mode's isolated-by-default execution. When enabled (the default),
    ///     each discovered class runs in its own clean-room child process unless a benchmark
    ///     or its class opts out with <c>[InProcess]</c>. When disabled, every benchmark
    ///     runs in the host process - equivalent to passing <c>--in-process</c> on the CLI.
    /// </summary>
    public BenchmarkHost WithIsolation(bool enabled = true)
    {
        _isolationEnabled = enabled;
        return this;
    }

    public BenchmarkHost WithInstanceLifetime(InstanceLifetime lifetime)
    {
        _defaultInstanceLifetime = lifetime;
        return this;
    }

    /// <summary>
    ///     Filters discovered benchmarks by category. Include rules are OR: a benchmark runs if
    ///     it has any included category. Exclude rules are also OR: a benchmark is removed if it
    ///     has any excluded category. Untagged benchmarks are excluded when any include filter
    ///     is set. This programmatic filter composes with the <c>--category</c> and
    ///     <c>--exclude-category</c> CLI flags.
    /// </summary>
    public BenchmarkHost WithCategoryFilter(IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
    {
        if (include is not null)
            AddCategories(_categoryFilterInclude, include, nameof(include));

        if (exclude is not null)
            AddCategories(_categoryFilterExclude, exclude, nameof(exclude));

        return this;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        return await IsolatedRunContext
            .WithCurrentRequestAsync(() => RunCoreAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(CancellationToken cancellationToken)
    {
        // Isolated child entry: serve only the class the parent requested, write its
        // samples back, and return. A child belonging to a sibling suite (not this host)
        // does nothing here, so it neither recurses nor duplicates output.
        if (IsolatedRunContext.TryGetActiveRequest(out var activeRequest))
        {
            return activeRequest.Kind == IsolatedRunKind.Host
                ? await RunHostChildAsync(activeRequest, cancellationToken).ConfigureAwait(false)
                : Array.Empty<BenchmarkResult>();
        }

        if (_cliArgs.ShowHelp)
        {
            CliArgs.PrintHelp();
            return Array.Empty<BenchmarkResult>();
        }

        Console.WriteLine($"Timer resolution: {Stopwatch.Frequency:N0} ticks/s "
                          + $"({1_000_000_000.0 / Stopwatch.Frequency:F2} ns per tick)");

        Console.WriteLine();

        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime);
        var allSuites = _assemblies.SelectMany(discoverer.Discover).ToList();

        if (allSuites.Count == 0)
        {
            Console.WriteLine("No benchmark classes found. Decorate methods with [Benchmark].");
            return Array.Empty<BenchmarkResult>();
        }

        var filtered = FilterSuites(allSuites, _cliArgs.Filter, _cliArgs.CategoryFilterInclude, _cliArgs.CategoryFilterExclude, _categoryFilterInclude, _categoryFilterExclude);

        if (_cliArgs.ListOnly)
        {
            foreach (var suite in filtered)
            {
                Console.WriteLine($"── {suite.Type.Name} ──");

                foreach (var b in suite.Benchmarks)
                {
                    var categorySuffix = b.Categories.Count > 0
                        ? $" [{string.Join(", ", b.Categories)}]"
                        : "";

                    Console.WriteLine($"    {b.DisplayName}"
                                      + (b.Attribute.Description is not null ? $" - {b.Attribute.Description}" : "")
                                      + categorySuffix);
                }
            }

            return Array.Empty<BenchmarkResult>();
        }

        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var suiteOptions = _cliArgs.DryRun
            ? _options with { Iterations = 0, WarmupIterations = 0 }
            : MergeCliOptions(_options, _cliArgs);

        var allNames = filtered
            .SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.DisplayName}"))
            .ToList();

        var totalBenchmarks = allNames.Count;

        await _progress.OnSuiteStarting(allNames, totalBenchmarks).ConfigureAwait(false);

        // Under --dry-run, --in-process, or WithIsolation(false), nothing is spawned. A
        // dry run never invokes a body, so isolation would only add process overhead.
        var inProcessGlobal = _cliArgs.InProcess || !_isolationEnabled || _cliArgs.DryRun;

        var runningIndex = 0;

        foreach (var suite in filtered)
        {
            var inProcess = new List<BenchmarkMethodDefinition>();
            var perClass = new List<BenchmarkMethodDefinition>();
            var perBenchmark = new List<BenchmarkMethodDefinition>();

            foreach (var benchmark in suite.Benchmarks)
            {
                switch (ResolveIsolation(benchmark, inProcessGlobal))
                {
                    case IsolationDecision.InProcess:
                        inProcess.Add(benchmark);
                        break;
                    case IsolationDecision.PerBenchmark:
                        perBenchmark.Add(benchmark);
                        break;
                    default:
                        perClass.Add(benchmark);
                        break;
                }
            }

            if (inProcess.Count > 0)
            {
                await RunInProcessSuiteAsync(
                    suite with { Benchmarks = inProcess }, suiteOptions, runningIndex, totalBenchmarks,
                    allResults, rawSamples, cancellationToken).ConfigureAwait(false);

                runningIndex += inProcess.Count;
            }

            if (perClass.Count > 0)
            {
                await RunIsolatedGroupAsync(
                    suite, perClass, runningIndex, totalBenchmarks,
                    allResults, rawSamples, cancellationToken).ConfigureAwait(false);

                runningIndex += perClass.Count;
            }

            foreach (var benchmark in perBenchmark)
            {
                await RunIsolatedGroupAsync(
                    suite, [benchmark], runningIndex, totalBenchmarks,
                    allResults, rawSamples, cancellationToken).ConfigureAwait(false);

                runningIndex++;
            }
        }

        await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

        Significance.ApplyIfEnabled(allResults, rawSamples, suiteOptions);

        if (_cliArgs.ThresholdPct.HasValue
            && ThresholdCheck.HasRegression(allResults, _cliArgs.ThresholdPct.Value) is (true, var regressed))
        {
            Console.Error.WriteLine(
                $"Regression threshold exceeded ({_cliArgs.ThresholdPct.Value}%). "
                + $"Regressed benchmarks: {string.Join(", ", regressed)}");

            Environment.ExitCode = 1;
        }

        if (!string.IsNullOrEmpty(_cliArgs.OutputDir))
            ApplyOutputDirectory(_cliArgs.OutputDir);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(allResults, cancellationToken).ConfigureAwait(false);
        }

        return allResults;
    }

    private async Task RunInProcessSuiteAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        if (suite.Lifetime == InstanceLifetime.PerClass)
            await RunPerClassInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, cancellationToken).ConfigureAwait(false);
        else
            await RunPerMethodInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPerClassInProcessAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceFactory);

        if (created is null)
            return;

        var (instance, instanceTeardown) = created.Value;
        var instanceFromFactory = _instanceFactory is not null;

        try
        {
            var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(suite, instance, suiteOptions);

            if (!setupSuccess)
            {
                allResults.AddRange(setupErrors!);
                return;
            }

            var factory = () => instance;
            var envelopes = suite.Benchmarks
                .Select(b => BenchmarkEnvelope.FromDiscovered(b, suite.Type.Name, factory))
                .ToList();

            var (results, samples) = await SuiteRunner.RunAsync(
                envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                startIndex, totalBenchmarks, _progress, cancellationToken).ConfigureAwait(false);

            allResults.AddRange(results);

            foreach (var kvp in samples)
            {
                rawSamples[kvp.Key] = kvp.Value;
            }
        }
        finally
        {
            await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, PostSuiteCleanup);
        }
    }

    private async Task RunPerMethodInProcessAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        var orderedBenchmarks = OrderBenchmarksForRun(suite.Benchmarks, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed);

        foreach (var benchmark in orderedBenchmarks)
        {
            var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceFactory);

            if (created is null)
            {
                var errored = OutcomeBuilder.Build(
                    new RunOutcome.Errored(new InvalidOperationException("Could not instantiate benchmark class"), "Instance creation failed"),
                    $"{suite.Type.Name}.{benchmark.DisplayName}", benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                    suiteOptions, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                    benchmark.Categories).Result;

                allResults.Add(errored);
                startIndex++;
                continue;
            }

            var (instance, instanceTeardown) = created.Value;
            var instanceFromFactory = _instanceFactory is not null;

            try
            {
                var singleBenchmarkSuite = suite with { Benchmarks = [benchmark] };
                var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(singleBenchmarkSuite, instance, suiteOptions);

                if (!setupSuccess)
                {
                    allResults.AddRange(setupErrors!);
                    startIndex++;
                    continue;
                }

                var factory = () => instance;
                var envelope = BenchmarkEnvelope.FromDiscovered(benchmark, suite.Type.Name, factory);

                var (results, samples) = await SuiteRunner.RunAsync(
                    [envelope], _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                    startIndex, totalBenchmarks, _progress, cancellationToken).ConfigureAwait(false);

                allResults.AddRange(results);

                foreach (var kvp in samples)
                {
                    rawSamples[kvp.Key] = kvp.Value;
                }
            }
            finally
            {
                await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, null);
            }

            startIndex++;
        }
    }

    private static IReadOnlyList<BenchmarkMethodDefinition> OrderBenchmarksForRun(
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        RunOrder order,
        int? seed)
    {
        if (order != RunOrder.Random)
            return benchmarks;

        var shuffled = benchmarks.ToList();
        var rng = new Random(seed ?? Random.Shared.Next());

        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }

    /// <summary>
    ///     Resolves how a single benchmark should run, layering the global in-process
    ///     switch over the isolation intent declared by its attributes.
    /// </summary>
    private static IsolationDecision ResolveIsolation(BenchmarkMethodDefinition benchmark, bool inProcessGlobal)
    {
        if (inProcessGlobal)
            return IsolationDecision.InProcess;

        return benchmark.Isolation switch
        {
            IsolationMode.InProcess => IsolationDecision.InProcess,
            IsolationMode.PerBenchmark => IsolationDecision.PerBenchmark,
            _ => IsolationDecision.PerClass,
        };
    }

    /// <summary>
    ///     Runs a group of benchmarks from one class in a single child process and folds
    ///     the results back into the parent. A per-class group shares one child; a
    ///     per-benchmark benchmark is passed here as a group of one.
    /// </summary>
    private async Task RunIsolatedGroupAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < benchmarks.Count; i++)
        {
            var name = $"{suite.Type.Name}.{benchmarks[i].DisplayName}";
            await _progress.OnBenchmarkStarting(name, startIndex + i + 1, totalBenchmarks).ConfigureAwait(false);
        }

        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Host,
            DeclaringTypeFullName = suite.Type.FullName,
            DisplayPrefix = suite.Type.Name,
            BenchmarkDisplayNames = benchmarks.Select(b => b.DisplayName).ToList(),
            Overrides = MeasurementOverrides.FromCliArgs(_cliArgs),
        };

        var items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

        foreach (var benchmark in benchmarks)
        {
            var name = $"{suite.Type.Name}.{benchmark.DisplayName}";

            BenchmarkResult result;
            double[] raw;

            if (byName.TryGetValue(name, out var item))
            {
                result = item.Result;
                raw = item.RawSamples;
            }
            else
            {
                var message = $"Isolated child did not return a result for '{name}'.";

                result = OutcomeBuilder.Build(
                    new RunOutcome.Errored(new InvalidOperationException(message), message),
                    name, benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                    _options, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                    benchmark.Categories).Result;

                raw = [];
            }

            allResults.Add(result);
            rawSamples[name] = raw;

            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Child-process entry point: run the class the parent requested (one or more of
    ///     its benchmarks) in this fresh CLR and write the serialized samples to the output
    ///     file the parent supplied. No banner, progress, or reporters - the parent owns
    ///     presentation and computes significance over the returned samples.
    /// </summary>
    private async Task<IReadOnlyList<BenchmarkResult>> RunHostChildAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
    {
        var options = request.Overrides.Apply(_options);
        var requested = new HashSet<string>(request.BenchmarkDisplayNames, StringComparer.Ordinal);

        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime);

        foreach (var suite in _assemblies.SelectMany(discoverer.Discover))
        {
            if (suite.Type.FullName != request.DeclaringTypeFullName)
                continue;

            var selected = suite.Benchmarks.Where(b => requested.Contains(b.DisplayName)).ToList();

            if (selected.Count == 0)
                continue;

            if (suite.Lifetime == InstanceLifetime.PerClass)
                return await RunPerClassHostChildAsync(suite, selected, options, cancellationToken)
                    .ConfigureAwait(false);
            else
                return await RunPerMethodHostChildAsync(suite, selected, options, cancellationToken)
                    .ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Isolated class '{request.DeclaringTypeFullName}' was not found.");

        // In the child: a non-zero exit code is what the parent's launcher reads as failure.
        Environment.ExitCode = 1;
        return Array.Empty<BenchmarkResult>();
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunPerClassHostChildAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        CancellationToken cancellationToken)
    {
        var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceFactory);

        if (created is null)
        {
            Environment.ExitCode = 1;
            return Array.Empty<BenchmarkResult>();
        }

        var (instance, instanceTeardown) = created.Value;
        var instanceFromFactory = _instanceFactory is not null;
        List<BenchmarkResult> results;
        var samples = new Dictionary<string, double[]>();

        try
        {
            var selectedSuite = suite with { Benchmarks = selected };
            var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(selectedSuite, instance, options);

            if (!setupSuccess)
                results = setupErrors!.ToList();
            else
            {
                var factory = () => instance;
                var envelopes = selected
                    .Select(b => BenchmarkEnvelope.FromDiscovered(b, suite.Type.Name, factory))
                    .ToList();

                (results, samples) = await SuiteRunner.RunAsync(
                    envelopes, RunOrder.Declaration, null, options,
                    0, selected.Count, NullBenchmarkProgress.Instance, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, PostSuiteCleanup);
        }

        await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(results, samples, cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunPerMethodHostChildAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>();

        foreach (var benchmark in selected)
        {
            var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceFactory);

            if (created is null)
            {
                Environment.ExitCode = 1;
                return Array.Empty<BenchmarkResult>();
            }

            var (instance, instanceTeardown) = created.Value;
            var instanceFromFactory = _instanceFactory is not null;

            try
            {
                var singleBenchmarkSuite = suite with { Benchmarks = [benchmark] };
                var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(singleBenchmarkSuite, instance, options);

                if (!setupSuccess)
                {
                    results.AddRange(setupErrors!);
                    continue;
                }

                var factory = () => instance;
                var envelope = BenchmarkEnvelope.FromDiscovered(benchmark, suite.Type.Name, factory);

                var (batchResults, batchSamples) = await SuiteRunner.RunAsync(
                    [envelope], RunOrder.Declaration, null, options,
                    0, 1, NullBenchmarkProgress.Instance, cancellationToken).ConfigureAwait(false);

                results.AddRange(batchResults);

                foreach (var kvp in batchSamples)
                {
                    samples[kvp.Key] = kvp.Value;
                }
            }
            finally
            {
                await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, null);
            }
        }

        await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(results, samples, cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    private void ApplyOutputDirectory(string outputDir)
    {
        for (var i = 0; i < _reporters.Count; i++)
        {
            if (ReporterRegistry.TryCreate(_reporters[i].Name, outputDir, _detail, out var rebuilt))
                _reporters[i] = rebuilt;
        }
    }

    private static IReadOnlyList<BenchmarkSuiteDefinition> FilterSuites(
        IReadOnlyList<BenchmarkSuiteDefinition> suites,
        string? filter,
        IReadOnlyList<string> cliInclude,
        IReadOnlyList<string> cliExclude,
        IReadOnlyList<string> hostInclude,
        IReadOnlyList<string> hostExclude)
    {
        var hasIncludeFilter = cliInclude.Count > 0 || hostInclude.Count > 0;

        if (filter is null && !hasIncludeFilter && cliExclude.Count == 0 && hostExclude.Count == 0)
            return suites;

        var exclude = UnionCategories(cliExclude, hostExclude);

        var filtered = suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b =>
                    {
                        if (filter is not null && !GlobMatcher.Match(filter, $"{s.Type.Name}.{b.DisplayName}"))
                            return false;

                        return CategoryFilter.Matches(b.Categories, cliInclude, hostInclude, exclude, hasIncludeFilter);
                    })
                    .ToList(),
            })
            .Where(s => s.Benchmarks.Count > 0)
            .ToList();

        return filtered;
    }

    private static void AddCategories(List<string> target, IEnumerable<string> source, string paramName)
    {
        foreach (var category in source)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category names cannot be null, empty, or whitespace.", paramName);

            var normalized = category.Trim();

            if (!target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                target.Add(normalized);
        }
    }

    private static IReadOnlyList<string> UnionCategories(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count == 0)
            return second;

        if (second.Count == 0)
            return first;

        var merged = new List<string>(first);

        foreach (var category in second)
        {
            if (!merged.Contains(category, StringComparer.OrdinalIgnoreCase))
                merged.Add(category);
        }

        return merged;
    }

    private static MeasurementOptions MergeCliOptions(MeasurementOptions options, CliArgs cliArgs)
        => MeasurementOverrides.FromCliArgs(cliArgs).Apply(options);
}
