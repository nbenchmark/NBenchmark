using System.Diagnostics;
using System.Reflection;
using NBenchmark.Diagnostics;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using NBenchmark.Observers;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed class BenchmarkHarness
{
    /// <summary>
    ///     The default number of launches each benchmark runs when the user has not pinned
    ///     <see cref="MeasurementOptions.LaunchCount" /> via <see cref="WithLaunchCount" />,
    ///     <see cref="WithOptions" />, the <c>--launch-count</c> CLI flag, or a
    ///     <c>[Benchmark(LaunchCount = ...)]</c> attribute. Harness mode defaults to multiple
    ///     launches so the launch-aggregation table - the honest view of run-to-run variance
    ///     from process-level effects (ASLR, scheduler placement, tiered JIT) - is surfaced
    ///     without users having to opt in. Single mode (<see cref="Benchmark.Run" />) and
    ///     <see cref="BenchmarkSuite" /> are unaffected: they keep <c>LaunchCount = 1</c>
    ///     unless the caller raises it.
    /// </summary>
    internal const int DefaultHarnessLaunchCount = 3;

    private readonly List<Assembly> _assemblies = [];
    private readonly List<string> _categoryFilterExclude = [];
    private readonly List<string> _categoryFilterInclude = [];
    private readonly List<IReporter> _reporters = [];
    private CliArgs _cliArgs = new();
    private bool _crossClass;
    private InstanceLifetime _defaultInstanceLifetime = InstanceLifetime.PerMethod;
    private ReportDetail _detail;
    private Func<Type, InstanceHandle>? _instanceFactory;
    private bool _isolationEnabled = true;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private bool _launchCountExplicit;
    private bool _optionsExplicitlySet;
    private readonly List<IMeasurementObserver> _observers = [];
    private RunOrder _runOrder = RunOrder.Random;

    private BenchmarkHarness()
    {
    }

    internal Action? PostSuiteCleanup { get; set; }

    /// <summary>
    ///     The effective base options with the harness-mode launch-count default applied.
    ///     When the user has not pinned <see cref="MeasurementOptions.LaunchCount" /> via
    ///     <see cref="WithLaunchCount" />, <see cref="WithOptions" />, or
    ///     <c>--launch-count</c>, harness mode defaults to
    ///     <see cref="DefaultHarnessLaunchCount" /> so the launch-aggregation table surfaces
    ///     run-to-run variance without opt-in. Calling <see cref="WithOptions" /> with any
    ///     options object (even one where <c>LaunchCount</c> happens to be 1) is treated as
    ///     an explicit choice and suppresses the default. The CLI flag is layered on top by
    ///     <see cref="MergeCliOptions" /> at the call sites, so a CLI override still wins.
    /// </summary>
    private MeasurementOptions EffectiveBaseOptions
        => _launchCountExplicit || _optionsExplicitlySet || _cliArgs.LaunchCount.HasValue
            ? _options
            : _options with { LaunchCount = DefaultHarnessLaunchCount };

    public static BenchmarkHarness Create(string[] args)
    {
        var cliArgs = CliArgs.Parse(args);
        var harness = new BenchmarkHarness();
        harness._cliArgs = cliArgs;
        harness._detail = cliArgs.Detail;

        foreach (var name in cliArgs.ReporterNames)
        {
            if (ReporterRegistry.TryCreate(name, null, cliArgs.Detail, out var reporter))
                harness._reporters.Add(reporter);
        }

        foreach (var name in cliArgs.ObserverNames)
        {
            if (ObserverRegistry.TryCreate(name, out var observer))
                harness._observers.Add(observer);
        }

        return harness;
    }

    public BenchmarkHarness AddFromAssembly<T>()
    {
        _assemblies.Add(typeof(T).Assembly);
        return this;
    }

    public BenchmarkHarness AddFromAssembly(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    public BenchmarkHarness WithReporter(IReporter reporter)
    {
        reporter.Detail = _detail;
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkHarness WithOptions(MeasurementOptions options)
    {
        _options = options;
        _optionsExplicitlySet = true;
        return this;
    }

    /// <summary>
    ///     Pins the number of times each benchmark repeats as an independent launch. Each
    ///     launch gets its own warmup and measurement pass; the per-launch medians are
    ///     aggregated into a launch-level confidence interval that surfaces run-to-run
    ///     variance from process-level effects (ASLR, scheduler placement, tiered JIT).
    ///     <para>
    ///         When unset, harness mode defaults to <see cref="DefaultHarnessLaunchCount" />
    ///         so the launch-aggregation table is shown without opt-in. Set to 1 to restore
    ///         single-launch behaviour.
    ///     </para>
    /// </summary>
    public BenchmarkHarness WithLaunchCount(int count)
    {
        if (count is < 1 or > MeasurementOptions.MaxLaunchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                $"LaunchCount must be between 1 and {MeasurementOptions.MaxLaunchCount}.");
        }

        _options = _options with { LaunchCount = count };
        _launchCountExplicit = true;
        return this;
    }

    public BenchmarkHarness WithRunOrder(RunOrder order)
    {
        _runOrder = order;
        return this;
    }

    public BenchmarkHarness WithDetail(ReportDetail detail)
    {
        _detail = detail;

        foreach (var reporter in _reporters)
        {
            reporter.Detail = detail;
        }

        return this;
    }

    public BenchmarkHarness WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    public BenchmarkHarness WithObserver(IMeasurementObserver observer)
    {
        if (observer is not null && observer != NullMeasurementObserver.Instance)
            _observers.Add(observer);

        return this;
    }

    /// <summary>
    ///     Resolves the attached observer list to the single <see cref="IMeasurementObserver" />
    ///     the engine should see. Composes three sources: programmatic instances added via
    ///     <see cref="WithObserver(IMeasurementObserver)" />, CLI-supplied names
    ///     (<c>--observer &lt;name&gt;</c>) resolved through <see cref="ObserverRegistry" />,
    ///     and auto-attached observers registered via
    ///     <see cref="ObserverRegistry.RegisterAutoAttach" /> (e.g. a live-streaming observer
    ///     that fires on every <c>RunAsync</c> without explicit opt-in). Dedup is by name across
    ///     all three sources so <c>.WithObserver(new StudioLiveObserver())</c> and
    ///     <c>--observer studio</c> and the auto-attached <c>studio</c> registration produce one
    ///     <c>studio</c> stream, not three.
    /// </summary>
    /// <remarks>
    ///     An empty result collapses to <see cref="NullMeasurementObserver.Instance" /> so the
    ///     hot-path guard (<c>observer != NullMeasurementObserver.Instance</c>) stays false and
    ///     the loop pays no dispatch cost. Two or more observers are wrapped in a
    ///     <see cref="CompositeMeasurementObserver" /> that fans out with per-dispatch try/catch
    ///     isolation. The resolved observer is disposed by the caller's <c>using</c> at the end
    ///     of <c>RunAsync</c>; the composite's <c>Dispose</c> fans out to each child.
    /// </remarks>
    private IMeasurementObserver ResolveObserver()
    {
        // Programmatic instances, named or anonymous. A programmatic StudioLiveObserver
        // has Name = "studio"; a programmatic ChannelMeasurementObserver has Name = null.
        // The list may contain duplicates when the user passes --observer <name> AND
        // .WithObserver(new ...()) for the same observer: BenchmarkHarness.Create(args)
        // resolves --observer <name> via TryCreate and adds the result to _observers, then
        // .WithObserver(...) adds the programmatic instance. Dedup by Name (last wins) so
        // the user's programmatic .WithObserver(...) call takes precedence over the CLI
        // flag - the programmatic call is the more deliberate attachment. Anonymous
        // observers (Name = null) are always kept.
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
                    continue; // earlier occurrence of the same name - the later (programmatic) wins
            }

            programmatic.Add(observer);
        }

        programmatic.Reverse(); // restore declaration order for composite dispatch

        // Build the dedup set from BOTH programmatic-attach names and CLI names so a
        // programmatic .WithObserver(new ...()) suppresses the auto-attached entry of the
        // same name (mirroring IReporter.Name dedup in ReporterRegistry.InvokeReportersAsync).
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _cliArgs.ObserverNames)
            explicitNames.Add(name);
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

        // Wrap programmatic + auto-attached in a composite. The composite's per-dispatch
        // try/catch isolates a throwing auto-attached observer.
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
    ///     The observer names forwarded to isolated children so they can activate the same
    ///     registry-resolved observers as the parent. Only CLI-supplied names
    ///     (<c>--observer &lt;name&gt;</c>) cross the process boundary: programmatic observer
    ///     instances added via <see cref="WithObserver(IMeasurementObserver)" /> are live
    ///     objects and cannot be serialized. The child resolves each name through
    ///     <see cref="ObserverRegistry" />, which is populated identically by
    ///     <c>[ModuleInitializer]</c> self-registration in the child's fresh process.
    /// </summary>
    private IReadOnlyList<string> ResolveObserverNames()
        => _cliArgs.ObserverNames;

    public BenchmarkHarness WithInstanceFactory(Func<Type, object> factory)
    {
        _instanceFactory = type => InstanceHandle.NoTeardown(factory(type));
        return this;
    }

    internal BenchmarkHarness WithInstanceFactory(Func<Type, InstanceHandle> factory)
    {
        _instanceFactory = factory;
        return this;
    }

    /// <summary>
    ///     Configures the harness to resolve benchmark instances from the specified
    ///     <see cref="IServiceProvider" />. Each benchmark method gets a fresh instance
    ///     resolved from the root provider. Throws if a benchmark type is not registered.
    ///     For scoped lifetime (e.g. EF Core's DbContext), install the
    ///     <c>NBenchmark.DependencyInjection</c> package and use
    ///     <c>WithScopedServiceProvider</c> instead.
    /// </summary>
    public BenchmarkHarness WithServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return WithInstanceFactory(type =>
        {
            var instance = serviceProvider.GetService(type)
                ?? throw new InvalidOperationException(
                    $"No service of type '{type.FullName}' is registered in the service provider.");
            return InstanceHandle.NoTeardown(instance);
        });
    }

    /// <summary>
    ///     Requires a minimum strategy-defined practical effect in [0, 1] for a candidate
    ///     to be considered practically significant. Values below the threshold are reported
    ///     as NotSignificant with a <c>neg</c> magnitude label.
    /// </summary>
    public BenchmarkHarness WithMinimumPracticalEffect(double minimumDelta)
    {
        _options = _options with { MinimumPracticalEffect = minimumDelta };
        return this;
    }

    /// <summary>
    ///     Pins the benchmark process to the specified logical CPU cores for the duration
    ///     of the run, removing inter-core migration noise. Cores are zero-based and
    ///     logical (as reported by the OS). The prior affinity is restored when the run
    ///     completes. Also propagated to isolated child processes. Call
    ///     <see cref="WithDedicatedHostGuidance" /> alongside this to surface a warning
    ///     when the host looks unsuitable.
    /// </summary>
    public BenchmarkHarness WithHardwareAffinity(params int[] cores)
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
    ///     restored when the run completes. Also propagated to isolated child processes.
    ///     A refused elevation (common on locked-down CI runners) is surfaced as a
    ///     warning, not an error.
    /// </summary>
    public BenchmarkHarness WithProcessPriority(System.Diagnostics.ProcessPriorityClass priority)
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
    public BenchmarkHarness WithDedicatedHostGuidance(bool enabled = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { DedicatedHostGuidance = enabled },
        };
        return this;
    }

    /// <summary>
    ///     Sets the measurement profile, which bundles per-iteration GC, between-benchmark GC, and
    ///     allocation tracking. <see cref="MeasurementProfile.Realistic" /> (the default) keeps natural
    ///     GC pressure in the timing; <see cref="MeasurementProfile.Independent" /> isolates iterations
    ///     for pure-CPU measurement.
    /// </summary>
    public BenchmarkHarness WithMeasurementProfile(MeasurementProfile profile)
    {
        _options = _options with { Profile = profile };
        return this;
    }

    /// <summary>
    ///     Tunes the adaptive measurement loop (warmup plateau, CI-width sample count, and
    ///     ops-per-sample calibration). Use <see cref="AutoTuneOptions.Quick" /> for fast feedback
    ///     or <see cref="AutoTuneOptions.Thorough" /> for tighter intervals.
    /// </summary>
    public BenchmarkHarness WithAutoTune(AutoTuneOptions autoTune)
    {
        ArgumentNullException.ThrowIfNull(autoTune);
        _options = _options with { AutoTune = autoTune };
        return this;
    }

    /// <summary>Selects an adaptive-tuning preset (Default, Quick, or Thorough).</summary>
    public BenchmarkHarness WithAutoTune(AutoTunePreset preset)
    {
        _options = _options with { AutoTune = AutoTuneOptions.FromPreset(preset) };
        return this;
    }

    /// <summary>
    ///     Pins the number of back-to-back body invocations timed as one sample (<c>K</c>),
    ///     overriding auto-calibration. Honoured even with per-iteration setup/teardown.
    /// </summary>
    public BenchmarkHarness WithOpsPerSample(int opsPerSample)
    {
        _options = _options with { OpsPerSample = opsPerSample };
        return this;
    }

    /// <summary>Configures runtime diagnostics (GC counts, heap info, exceptions, CPU time).</summary>
    public BenchmarkHarness WithDiagnostics(DiagnosticsOptions diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _options = _options with { Diagnostics = diagnostics };
        return this;
    }

    /// <summary>Selects a diagnostics mode (None, Gc, GcAndCpu, All).</summary>
    public BenchmarkHarness WithDiagnostics(DiagnosticsMode mode)
    {
        _options = _options with { Diagnostics = DiagnosticsOptions.FromMode(mode) };
        return this;
    }

    /// <summary>
    ///     Controls Harness mode's isolated-by-default execution. When enabled (the default),
    ///     each discovered class runs in its own clean-room child process unless a benchmark
    ///     or its class opts out with <c>[InProcess]</c>. When disabled, every benchmark
    ///     runs in the host process - equivalent to passing <c>--in-process</c> on the CLI.
    /// </summary>
    public BenchmarkHarness WithIsolation(bool enabled = true)
    {
        _isolationEnabled = enabled;
        return this;
    }

    /// <summary>
    ///     When enabled, significance is computed across all classes in a single comparison
    ///     table instead of per class. The baseline is chosen from the whole group. Use this
    ///     when comparing implementations that live in separate classes (e.g. a legacy version
    ///     and a refactored version). Disabled by default.
    /// </summary>
    public BenchmarkHarness WithCrossClassSignificance(bool enabled = true)
    {
        _crossClass = enabled;
        return this;
    }

    public BenchmarkHarness WithInstanceLifetime(InstanceLifetime lifetime)
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
    public BenchmarkHarness WithCategoryFilter(IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
    {
        if (include is not null)
            AddCategories(_categoryFilterInclude, include, nameof(include));

        if (exclude is not null)
            AddCategories(_categoryFilterExclude, exclude, nameof(exclude));

        return this;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        // Mirror --otlp-endpoint for the duration of this run so isolated children inherit the
        // same exporter endpoint without leaking env-var mutations to subsequent runs in the same
        // process. Keep OTEL_EXPORTER_OTLP_ENDPOINT authoritative when the user already set it.
        using var _ = ApplyCliOtelEndpointScope(_cliArgs.OtlpEndpoint);

        return await IsolatedRunContext
            .WithCurrentRequestAsync(() => RunCoreAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    private static IDisposable ApplyCliOtelEndpointScope(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return NoopScope.Instance;

        var previousNBenchmarkEndpoint = Environment.GetEnvironmentVariable(ChildProcessLauncher.OtelEndpointEnvVar);
        var previousOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        Environment.SetEnvironmentVariable(ChildProcessLauncher.OtelEndpointEnvVar, endpoint);

        if (string.IsNullOrEmpty(previousOtlpEndpoint))
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);

        return new OtlpEndpointScope(previousNBenchmarkEndpoint, previousOtlpEndpoint);
    }

    private sealed class OtlpEndpointScope(string? previousNBenchmarkEndpoint, string? previousOtlpEndpoint) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ChildProcessLauncher.OtelEndpointEnvVar, previousNBenchmarkEndpoint);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", previousOtlpEndpoint);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        private NoopScope()
        {
        }

        public void Dispose()
        {
        }
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(CancellationToken cancellationToken)
    {
        // Isolated child entry: serve only the class the parent requested, write its
        // samples back, and return. A child belonging to a sibling suite (not this harness)
        // does nothing here, so it neither recurses nor duplicates output.
        if (IsolatedRunContext.TryGetActiveRequest(out var activeRequest))
        {
            var results = activeRequest.Kind == IsolatedRunKind.Host
                ? await RunHostChildAsync(activeRequest, cancellationToken).ConfigureAwait(false)
                : Array.Empty<BenchmarkResult>();

            // Stamp RuntimeMoniker on child results.
            if (activeRequest.RuntimeMoniker is { } runtimeMoniker)
            {
                var tfm = runtimeMoniker.ToTargetFramework();
                results = results.Select(r => r with { RuntimeMoniker = tfm }).ToList();
            }

            return results;
        }

        if (_cliArgs.ShowHelp)
        {
            CliArgs.PrintHelp();
            return Array.Empty<BenchmarkResult>();
        }

        // When runtimes are specified (CLI or attribute), delegate to the multi-runtime orchestrator.
        // --help and --list-only are handled before this so they never trigger multi-runtime builds.
        var effectiveRuntimes = _cliArgs.Runtimes;

        if (effectiveRuntimes.Count == 0 && !_cliArgs.ListOnly)
            effectiveRuntimes = DiscoverAttributeRuntimes();

        if (effectiveRuntimes.Count > 0)
        {
            if (_cliArgs.InProcess || !_isolationEnabled)
                Console.WriteLine("Warning: cross-runtime execution always uses child processes.");

            return await RunMultiRuntimeAsync(effectiveRuntimes, cancellationToken).ConfigureAwait(false);
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

        var filtered = FilterSuites(allSuites, _cliArgs.Filter, _cliArgs.CategoryFilterInclude, _cliArgs.CategoryFilterExclude, _categoryFilterInclude,
            _categoryFilterExclude);

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
            : MergeCliOptions(EffectiveBaseOptions, _cliArgs);

        // Apply opt-in hardware/OS controls (CPU affinity, process priority, dedicated-host
        // guidance) for the duration of the run. The scope restores the prior process state
        // on dispose. Isolated children receive the same settings via MeasurementOverrides
        // and apply them themselves; the parent applies its own so in-process benchmarks and
        // the parent-side measurement loop run pinned/elevated too.
        using var _ = EnvironmentControl.Apply(suiteOptions.Environment);

        var allNames = filtered
            .SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.DisplayName}"))
            .ToList();

        var totalBenchmarks = allNames.Count;

        NBenchmarkDiagnostics.OnSuiteStarting(
            _cliArgs.Filter ?? "harness",
            totalBenchmarks,
            profile: _options.Profile.ToString(),
            runtime: _cliArgs.Runtimes is { Count: > 0 } runtimes ? string.Join(",", runtimes.Select(r => r.ToTargetFramework())) : null,
            seed: _cliArgs.Seed,
            runOrder: (_cliArgs.RunOrder ?? _runOrder).ToString());

        // Resolve the observer once for the whole run so auto-attached observers (e.g. a
        // live-streaming observer) see one stream per RunAsync, not one per per-class group.
        // The using disposes the observer (and its composite children) on both the success
        // and exception paths; the composite's Dispose fans out with try/catch isolation.
        using var observer = ResolveObserver();
        var sentinelEmitted = false;

        try
        {
            await _progress.OnSuiteStarting(allNames, totalBenchmarks).ConfigureAwait(false);

            // Under --dry-run, --in-process, or WithIsolation(false), nothing is spawned. A
            // dry run never invokes a body, so isolation would only add process overhead.
            var inProcessGlobal = _cliArgs.InProcess || !_isolationEnabled || _cliArgs.DryRun;

            var runningIndex = 0;

            foreach (var suite in filtered)
            {
                var suiteResultStart = allResults.Count;
                var inProcess = new List<BenchmarkMethodDefinition>();
                var perClass = new List<BenchmarkMethodDefinition>();
                var perBenchmark = new List<BenchmarkMethodDefinition>();
                var autoUpgradedResultNames = new HashSet<string>();

                foreach (var benchmark in suite.Benchmarks)
                {
                    var decision = ResolveIsolation(benchmark, suite, inProcessGlobal, _instanceFactory is not null, out var autoUpgraded);

                    if (autoUpgraded)
                        autoUpgradedResultNames.Add($"{suite.Type.Name}.{benchmark.DisplayName}");

                    switch (decision)
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
                        allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);

                    runningIndex += inProcess.Count;
                }

                if (perClass.Count > 0)
                {
                    await RunIsolatedGroupAsync(
                        suite, perClass, runningIndex, totalBenchmarks,
                        allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);

                    runningIndex += perClass.Count;
                }

                foreach (var benchmark in perBenchmark)
                {
                    await RunIsolatedGroupAsync(
                        suite, [benchmark], runningIndex, totalBenchmarks,
                        allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);

                    runningIndex++;
                }

                // Attach the auto-isolation upgrade warning to every result that was
                // auto-upgraded from PerClass to PerBenchmark by the b-factory rule.
                if (autoUpgradedResultNames.Count > 0)
                    ApplyAutoIsolationUpgradeWarning(allResults, autoUpgradedResultNames, suiteResultStart);
            }

            await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true. A
            // live-streaming observer treats this as the authoritative run-end signal.
            observer.OnPhase(new MeasurementPhaseEvent(
                BenchmarkName: string.Empty,
                Phase: MeasurementPhase.SuiteCompleted,
                Transition: PhaseTransition.Completed,
                Succeeded: true));
            sentinelEmitted = true;
        }
        finally
        {
            // If the try block did not reach its success-path emit (a harness-level
            // exception prevented it), emit the sentinel here with Succeeded = false so a
            // live-streaming observer can finalise the run as Failed rather than leaving it
            // stuck as Running until the idle timeout fires.
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    BenchmarkName: string.Empty,
                    Phase: MeasurementPhase.SuiteCompleted,
                    Transition: PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(allResults);
        }

        // SuiteRunner and the isolated-launch aggregators key raw samples by benchmark name;
        // ApplyPerClassSignificance needs the composite name+runtime key so multi-runtime
        // results don't collide.
        rawSamples = ToCompositeKeys(allResults, rawSamples);

        ApplyPerClassSignificance(allResults, rawSamples, suiteOptions, _cliArgs.CrossClass || _crossClass);

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

        BenchmarkTable.CrossClassMode = _cliArgs.CrossClass || _crossClass;
        try
        {
            await InvokeReportersAsync(allResults, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BenchmarkTable.CrossClassMode = false;
        }

        return allResults;
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunMultiRuntimeAsync(
        IReadOnlyList<RuntimeMoniker> runtimes,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Building for runtimes: {string.Join(", ", runtimes.Select(r => r.ToTargetFramework()))}");

        var builds = await MultiRuntimeOrchestrator
            .BuildForRuntimesAsync(runtimes, cancellationToken).ConfigureAwait(false);

        var failedBuilds = builds.Where(b => b.Error is not null).ToList();

        foreach (var failed in failedBuilds)
        {
            Console.Error.WriteLine($"  {failed.Moniker.ToTargetFramework()}: {failed.Error}");
        }

        var successfulBuilds = builds.Where(b => b.DllPath is not null).ToList();

        if (successfulBuilds.Count == 0)
        {
            Console.Error.WriteLine("All runtime builds failed.");
            Environment.ExitCode = 1;
            return [];
        }

        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime);
        var allSuites = _assemblies.SelectMany(discoverer.Discover).ToList();

        var filtered = FilterSuites(allSuites, _cliArgs.Filter, _cliArgs.CategoryFilterInclude,
            _cliArgs.CategoryFilterExclude, _categoryFilterInclude, _categoryFilterExclude);

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var suiteOptions = _cliArgs.DryRun
            ? _options with { Iterations = 0, WarmupIterations = 0 }
            : MergeCliOptions(EffectiveBaseOptions, _cliArgs);

        // Apply opt-in hardware/OS controls to the parent process for the duration of the
        // multi-runtime run, mirroring the single-runtime path. Each spawned child also
        // applies its own settings via MeasurementOverrides; this scope covers the parent's
        // own measurement/aggregation work.
        using var _ = EnvironmentControl.Apply(suiteOptions.Environment);

        // Resolve the observer once for the whole multi-runtime run so auto-attached
        // observers see one stream per RunAsync, mirroring the single-runtime path. The
        // using disposes the observer on both the success and exception paths.
        using var observer = ResolveObserver();
        var sentinelEmitted = false;
        try
        {
            foreach (var build in successfulBuilds)
            {
                var tfm = build.Moniker.ToTargetFramework();

                try
                {
                    Console.WriteLine($"Running benchmarks under {tfm}...");

                    var runtimeResults = await RunForRuntimeAsync(
                            build.Moniker, build.DllPath!, filtered, observer, cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var item in runtimeResults)
                    {
                        allResults.Add(item.Result);
                        rawSamples[$"{item.Result.Name}\0{tfm}"] = item.RawSamples;
                    }
                }
                finally
                {
                    MultiRuntimeOrchestrator.TryDeleteBuildOutput(build.OutputDirectory);
                }
            }

            ApplyPerClassSignificance(allResults, rawSamples, suiteOptions, _cliArgs.CrossClass || _crossClass);

            if (_cliArgs.ThresholdPct.HasValue)
            {
                var threshold = _cliArgs.ThresholdPct.Value;
                var regressedNames = new List<string>();

                // Threshold comparisons only make sense within the same runtime - net8 will
                // always look "slower" than net10, which would false-positive every net8 row.
                foreach (var runtimeGroup in allResults.GroupBy(r => r.RuntimeMoniker))
                {
                    if (ThresholdCheck.HasRegression(runtimeGroup.ToList(), threshold) is (true, var names))
                        regressedNames.AddRange(names);
                }

                if (regressedNames.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"Regression threshold exceeded ({threshold}%). "
                        + $"Regressed benchmarks: {string.Join(", ", regressedNames)}");

                    Environment.ExitCode = 1;
                }
            }

            if (!string.IsNullOrEmpty(_cliArgs.OutputDir))
                ApplyOutputDirectory(_cliArgs.OutputDir);

            BenchmarkTable.CrossClassMode = _cliArgs.CrossClass || _crossClass;
            try
            {
                await InvokeReportersAsync(allResults, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                BenchmarkTable.CrossClassMode = false;
            }

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true.
            observer.OnPhase(new MeasurementPhaseEvent(
                BenchmarkName: string.Empty,
                Phase: MeasurementPhase.SuiteCompleted,
                Transition: PhaseTransition.Completed,
                Succeeded: true));
            sentinelEmitted = true;

            return allResults;
        }
        finally
        {
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    BenchmarkName: string.Empty,
                    Phase: MeasurementPhase.SuiteCompleted,
                    Transition: PhaseTransition.Completed,
                    Succeeded: false));
            }
        }
    }

    private async Task<IReadOnlyList<IsolatedResultItem>> RunForRuntimeAsync(
        RuntimeMoniker moniker,
        string entryAssemblyPath,
        IReadOnlyList<BenchmarkSuiteDefinition> filteredSuites,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var allItems = new List<IsolatedResultItem>();
        var tfm = moniker.ToTargetFramework();

        foreach (var suite in filteredSuites)
        {
            var request = new IsolatedRunRequest
            {
                Kind = IsolatedRunKind.Host,
                DeclaringTypeFullName = suite.Type.FullName,
                DisplayPrefix = suite.Type.Name,
                BenchmarkDisplayNames = suite.Benchmarks.Select(b => b.DisplayName).ToList(),
                Overrides = MeasurementOverrides.FromCliArgs(_cliArgs),
                RuntimeMoniker = moniker,
                EntryAssemblyPath = entryAssemblyPath,
                ObserverNames = ResolveObserverNames(),
            };

            var items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in items)
            {
                var stamped = item with { Result = item.Result with { RuntimeMoniker = tfm } };
                allItems.Add(stamped);
                // The parent emits OnResult for each child result so the observer sees every
                // benchmark across all runtimes in one stream. The child has its own observer
                // (resolved from the forwarded names); this is the parent-side aggregation.
                observer.OnResult(stamped.Result);
            }
        }

        return allItems;
    }

    private static Dictionary<string, double[]> ToCompositeKeys(
        IReadOnlyList<BenchmarkResult> results,
        Dictionary<string, double[]> nameKeyedSamples)
    {
        var composite = new Dictionary<string, double[]>(nameKeyedSamples.Count);

        foreach (var r in results)
        {
            if (nameKeyedSamples.TryGetValue(r.Name, out var samples))
                composite[$"{r.Name}\0{r.RuntimeMoniker}"] = samples;
        }

        return composite;
    }

    private static void ApplyPerClassSignificance(
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options,
        bool crossClass = false)
    {
        static string RawKey(BenchmarkResult r)
        {
            return $"{r.Name}\0{r.RuntimeMoniker}";
        }

        if (crossClass)
        {
            ApplyCrossClassSignificance(allResults, rawSamples, options, RawKey);
            return;
        }

        var groups = allResults
            .Select((result, index) => (result, index))
            .GroupBy(x => x.result.ClassName)
            .ToList();

        foreach (var classGroup in groups)
        {
            var classIndices = classGroup.Select(x => x.index).ToList();
            var classResults = classIndices.Select(i => allResults[i]).ToList();

            // Within a class, significance and threshold comparisons only make sense within
            // the same runtime. Group by RuntimeMoniker so net8 results are compared against
            // the net8 baseline, not the net10 one.
            var runtimeGroups = classResults
                .Select((r, idx) => (Result: r, Index: idx))
                .GroupBy(ri => ri.Result.RuntimeMoniker)
                .ToList();

            if (!classResults.Any(r => r.ParameterSet.Count > 0))
            {
                foreach (var runtimeGroup in runtimeGroups)
                {
                    var runtimeList = runtimeGroup.ToList();
                    var runtimeResults = runtimeList.Select(ri => ri.Result).ToList();
                    var runtimeRaw = new Dictionary<string, double[]>();

                    foreach (var ri in runtimeList)
                    {
                        if (rawSamples.TryGetValue(RawKey(ri.Result), out var samples))
                            runtimeRaw[ri.Result.Name] = samples;
                    }

                    Significance.ApplyIfEnabled(runtimeResults, runtimeRaw, options);

                    for (var j = 0; j < runtimeList.Count; j++)
                    {
                        allResults[classIndices[runtimeList[j].Index]] = runtimeResults[j];
                    }
                }

                continue;
            }

            var indexedResults = classResults
                .Select((r, idx) => (Result: r, Index: idx))
                .ToList();

            var paramGroups = indexedResults
                .GroupBy(ri => BenchmarkParameter.GetKey(ri.Result.ParameterSet))
                .ToList();

            foreach (var paramGroup in paramGroups)
            {
                var paramRuntimeGroups = paramGroup
                    .GroupBy(ri => ri.Result.RuntimeMoniker)
                    .ToList();

                foreach (var runtimeGroup in paramRuntimeGroups)
                {
                    var paramList = runtimeGroup.ToList();
                    var paramResults = paramList.Select(ri => ri.Result).ToList();
                    var paramRaw = new Dictionary<string, double[]>();

                    foreach (var ri in paramList)
                    {
                        if (rawSamples.TryGetValue(RawKey(ri.Result), out var samples))
                            paramRaw[ri.Result.Name] = samples;
                    }

                    Significance.ApplyIfEnabled(paramResults, paramRaw, options);

                    for (var j = 0; j < paramList.Count; j++)
                    {
                        allResults[classIndices[paramList[j].Index]] = paramResults[j];
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Cross-class significance: group all results by RuntimeMoniker (and parameter set
    ///     when present) instead of by ClassName, then run significance once per group.
    /// </summary>
    private static void ApplyCrossClassSignificance(
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options,
        Func<BenchmarkResult, string> rawKey)
    {
        var anyParameterised = allResults.Any(r => r.ParameterSet.Count > 0);

        if (!anyParameterised)
        {
            foreach (var runtimeGroup in allResults
                         .Select((r, idx) => (Result: r, Index: idx))
                         .GroupBy(ri => ri.Result.RuntimeMoniker))
            {
                var runtimeList = runtimeGroup.ToList();
                var runtimeResults = runtimeList.Select(ri => ri.Result).ToList();
                var runtimeRaw = new Dictionary<string, double[]>();

                foreach (var ri in runtimeList)
                {
                    if (rawSamples.TryGetValue(rawKey(ri.Result), out var samples))
                        runtimeRaw[ri.Result.Name] = samples;
                }

                Significance.ApplyIfEnabled(runtimeResults, runtimeRaw, options);

                for (var j = 0; j < runtimeList.Count; j++)
                {
                    allResults[runtimeList[j].Index] = runtimeResults[j];
                }
            }

            return;
        }

        // Parameterised: group by parameter set, then by runtime within each set.
        var paramGroups = allResults
            .Select((r, idx) => (Result: r, Index: idx))
            .GroupBy(ri => BenchmarkParameter.GetKey(ri.Result.ParameterSet))
            .ToList();

        foreach (var paramGroup in paramGroups)
        {
            foreach (var runtimeGroup in paramGroup
                         .GroupBy(ri => ri.Result.RuntimeMoniker))
            {
                var runtimeList = runtimeGroup.ToList();
                var runtimeResults = runtimeList.Select(ri => ri.Result).ToList();
                var runtimeRaw = new Dictionary<string, double[]>();

                foreach (var ri in runtimeList)
                {
                    if (rawSamples.TryGetValue(rawKey(ri.Result), out var samples))
                        runtimeRaw[ri.Result.Name] = samples;
                }

                Significance.ApplyIfEnabled(runtimeResults, runtimeRaw, options);

                for (var j = 0; j < runtimeList.Count; j++)
                {
                    allResults[runtimeList[j].Index] = runtimeResults[j];
                }
            }
        }
    }

    private async Task RunInProcessSuiteAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        if (suite.Lifetime == InstanceLifetime.PerClass)
        {
            await RunPerClassInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunPerMethodInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunPerClassInProcessAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        IMeasurementObserver observer,
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

            // When the class implements IStateReset, fire ResetAsync between benchmark methods
            // to keep the shared instance's state clean across PerClass execution. Null otherwise.
            Func<Task>? betweenBenchmarksReset = typeof(IStateReset).IsAssignableFrom(suite.Type)
                ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                : null;

            var effectiveClassLaunchCount = suiteOptions.LaunchCount;

            foreach (var b in suite.Benchmarks)
            {
                if (b.Attribute.HasLaunchCountOverride && b.Attribute.LaunchCount > effectiveClassLaunchCount)
                    effectiveClassLaunchCount = b.Attribute.LaunchCount;
            }

            if (effectiveClassLaunchCount > 1)
            {
                var allLaunchResults = new List<IReadOnlyList<BenchmarkResult>>();
                var allLaunchSamples = new List<Dictionary<string, double[]>>();

                for (var launchIdx = 0; launchIdx < effectiveClassLaunchCount; launchIdx++)
                {
                    var progress = launchIdx == 0 ? _progress : NullBenchmarkProgress.Instance;
                    var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;

                    var (results, samples) = await SuiteRunner.RunAsync(
                        envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                        startIndex, totalBenchmarks, progress, cancellationToken, betweenBenchmarksReset, launchObserver).ConfigureAwait(false);

                    allLaunchResults.Add(results);
                    allLaunchSamples.Add(samples);
                }

                var aggregated = AggregateInProcessLaunches(allLaunchResults, allLaunchSamples);
                ApplyPerClassIndependenceWarning(aggregated.Results, suite, suiteOptions);
                allResults.AddRange(aggregated.Results);

                foreach (var kvp in aggregated.Samples)
                {
                    rawSamples[kvp.Key] = kvp.Value;
                }
            }
            else
            {
                var (results, samples) = await SuiteRunner.RunAsync(
                    envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                    startIndex, totalBenchmarks, _progress, cancellationToken, betweenBenchmarksReset, observer).ConfigureAwait(false);

                ApplyPerClassIndependenceWarning(results, suite, suiteOptions);
                allResults.AddRange(results);

                foreach (var kvp in samples)
                {
                    rawSamples[kvp.Key] = kvp.Value;
                }
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
        IMeasurementObserver observer,
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
                    $"{suite.Type.Name}.{benchmark.DisplayName}", suite.Type.Name, benchmark.Attribute.Description, benchmark.IsBaseline,
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

                var perMethodLaunchCount = benchmark.Attribute.HasLaunchCountOverride
                    ? benchmark.Attribute.LaunchCount
                    : suiteOptions.LaunchCount;

                if (perMethodLaunchCount > 1)
                {
                    var perLaunchResults = new List<BenchmarkResult>();
                    var perLaunchSamples = new List<Dictionary<string, double[]>>();

                    for (var launchIdx = 0; launchIdx < perMethodLaunchCount; launchIdx++)
                    {
                        var progress = launchIdx == 0 ? _progress : NullBenchmarkProgress.Instance;
                        var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;

                        var (results, samples) = await SuiteRunner.RunAsync(
                            [envelope], _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                            startIndex, totalBenchmarks, progress, cancellationToken, onBetweenBenchmarksAsync: null, launchObserver).ConfigureAwait(false);

                        perLaunchResults.AddRange(results);
                        perLaunchSamples.Add(samples);
                    }

                    var stats = LaunchAggregator.Aggregate(perLaunchResults);
                    var best = LaunchAggregator.BestLaunch(perLaunchResults);
                    allResults.Add(best with { LaunchStatistics = stats });

                    foreach (var kvp in PoolRawSamplesByName(perLaunchSamples))
                    {
                        rawSamples[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    var (results, samples) = await SuiteRunner.RunAsync(
                        [envelope], _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                        startIndex, totalBenchmarks, _progress, cancellationToken, onBetweenBenchmarksAsync: null, observer).ConfigureAwait(false);

                    allResults.AddRange(results);

                    foreach (var kvp in samples)
                    {
                        rawSamples[kvp.Key] = kvp.Value;
                    }
                }
            }
            finally
            {
                await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, null);
            }

            startIndex++;
        }
    }

    private static (List<BenchmarkResult> Results, Dictionary<string, double[]> Samples) AggregateInProcessLaunches(
        IReadOnlyList<IReadOnlyList<BenchmarkResult>> allLaunchResults,
        IReadOnlyList<Dictionary<string, double[]>> allLaunchSamples)
    {
        if (allLaunchResults.Count == 0)
            return ([], []);

        var names = allLaunchResults[0].Select(r => r.Name).ToList();
        var aggregated = new List<BenchmarkResult>(names.Count);
        var samples = new Dictionary<string, double[]>(names.Count);
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

            if (pooledSamples.TryGetValue(name, out var launchSamples))
                samples[name] = launchSamples;
        }

        return (aggregated, samples);
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

    private static IReadOnlyList<BenchmarkMethodDefinition> OrderBenchmarksForRun(
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        RunOrder order,
        int? seed)
    {
        if (order != RunOrder.Random)
            return benchmarks;

        var hasParameters = benchmarks.Any(b => b.ParameterSet.Count > 0);

        if (!hasParameters)
        {
            var shuffled = benchmarks.ToList();
            var rng = new Random(seed ?? Random.Shared.Next());
            ShuffleInPlace(shuffled, rng);
            return shuffled;
        }

        var parameterGroups = benchmarks
            .GroupBy(b => BenchmarkParameter.GetKey(b.ParameterSet))
            .ToList();

        var ordered = new List<BenchmarkMethodDefinition>(benchmarks.Count);
        var groupSeedRng = new Random(seed ?? Random.Shared.Next());

        foreach (var group in parameterGroups)
        {
            var groupList = group.ToList();
            var groupRng = new Random(groupSeedRng.Next());
            ShuffleInPlace(groupList, groupRng);
            ordered.AddRange(groupList);
        }

        return ordered;
    }

    private static void ShuffleInPlace<T>(List<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    ///     Resolves how a single benchmark should run, layering the global in-process
    ///     switch over the isolation intent declared by its attributes. When the declaring
    ///     class uses <see cref="InstanceLifetime.PerClass" /> with a factory-resolved
    ///     instance and does not implement <see cref="IStateReset" />, the decision is
    ///     auto-upgraded to <see cref="IsolationDecision.PerBenchmark" /> to preserve the
    ///     statistical-independence assumption of the significance engine. Explicit
    ///     <c>[InProcess]</c> on the method or the <c>--in-process</c> global flag wins
    ///     over the upgrade.
    /// </summary>
    /// <param name="autoUpgraded">
    ///     Set to <c>true</c> when the PerClass default was upgraded to PerBenchmark
    ///     because the class uses a factory and does not implement <see cref="IStateReset" />;
    ///     <c>false</c> for every other decision (explicit <c>[IsolatedProcess]</c>,
    ///     in-process, or genuine PerClass).
    /// </param>
    private static IsolationDecision ResolveIsolation(
        BenchmarkMethodDefinition benchmark,
        BenchmarkSuiteDefinition suite,
        bool inProcessGlobal,
        bool usesFactory,
        out bool autoUpgraded)
    {
        autoUpgraded = false;

        if (inProcessGlobal)
            return IsolationDecision.InProcess;

        if (benchmark.Isolation == IsolationMode.InProcess)
            return IsolationDecision.InProcess;

        if (benchmark.Isolation == IsolationMode.PerBenchmark)
            return IsolationDecision.PerBenchmark;

        // PerClass default. Auto-upgrade to PerBenchmark when the instance is
        // factory-resolved and the class does not implement IStateReset, to
        // preserve the statistical-independence assumption of the significance
        // engine. Explicit [InProcess] on the method already returned above.
        if (suite.Lifetime == InstanceLifetime.PerClass
            && usesFactory
            && !typeof(IStateReset).IsAssignableFrom(suite.Type))
        {
            autoUpgraded = true;
            return IsolationDecision.PerBenchmark;
        }

        return IsolationDecision.PerClass;
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
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var effectiveLaunchCount = _cliArgs.LaunchCount ?? EffectiveBaseOptions.LaunchCount;

        // Check per-benchmark attribute overrides; take the maximum so all
        // benchmarks in the group get enough launches for their overrides.
        foreach (var b in benchmarks)
        {
            if (b.Attribute.HasLaunchCountOverride && b.Attribute.LaunchCount > effectiveLaunchCount)
                effectiveLaunchCount = b.Attribute.LaunchCount;
        }

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
            // Forward the resolved observer names so isolated children activate the same
            // observers (e.g. the dashboard, an OTLP exporter) as the parent. The child resolves
            // them through ObserverRegistry, which is populated identically by [ModuleInitializer]
            // self-registration in the child's fresh process.
            ObserverNames = ResolveObserverNames(),
        };

        IReadOnlyList<IsolatedResultItem> items;

        if (effectiveLaunchCount > 1)
        {
            var allLaunchItems = new List<IReadOnlyList<IsolatedResultItem>>();

            for (var launchIdx = 0; launchIdx < effectiveLaunchCount; launchIdx++)
            {
                var launchItems = await ChildProcessLauncher.LaunchAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                allLaunchItems.Add(launchItems);
            }

            items = HostAggregateIsolatedLaunches(allLaunchItems, benchmarks, suite);
        }
        else
            items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);

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
                    name, suite.Type.Name, benchmark.Attribute.Description, benchmark.IsBaseline,
                    _options, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                    benchmark.Categories).Result;

                raw = [];
            }

            allResults.Add(result);
            rawSamples[name] = raw;

            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
            observer.OnResult(result);
        }
    }

    private static IReadOnlyList<IsolatedResultItem> HostAggregateIsolatedLaunches(
        IReadOnlyList<IReadOnlyList<IsolatedResultItem>> allLaunchItems,
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        BenchmarkSuiteDefinition suite)
    {
        if (allLaunchItems.Count == 0)
            return [];

        var aggregated = new List<IsolatedResultItem>();

        foreach (var benchmark in benchmarks)
        {
            var name = $"{suite.Type.Name}.{benchmark.DisplayName}";
            var perLaunchResults = new List<BenchmarkResult>();

            foreach (var launchItems in allLaunchItems)
            {
                var match = launchItems.FirstOrDefault(item => item.Result.Name == name);

                if (match is not null)
                    perLaunchResults.Add(match.Result);
            }

            if (perLaunchResults.Count == 0)
            {
                var message = $"Isolated child did not return a result for '{name}' in any launch.";

                aggregated.Add(new IsolatedResultItem
                {
                    Result = OutcomeBuilder.Build(
                        new RunOutcome.Errored(new InvalidOperationException(message), message),
                        name, suite.Type.Name, benchmark.Attribute.Description, benchmark.IsBaseline,
                        new MeasurementOptions(), TimeSpan.Zero, TimeSpan.Zero, 0, null,
                        benchmark.Categories).Result,
                    RawSamples = [],
                });

                continue;
            }

            var stats = LaunchAggregator.Aggregate(perLaunchResults);
            var best = LaunchAggregator.BestLaunch(perLaunchResults);
            var aggregatedResult = best with { LaunchStatistics = stats };

            var rawSamples = allLaunchItems
                .SelectMany(launchItems => launchItems.Where(item => item.Result.Name == name))
                .SelectMany(item => item.RawSamples)
                .ToArray();

            aggregated.Add(new IsolatedResultItem
            {
                Result = aggregatedResult,
                RawSamples = rawSamples,
            });
        }

        return aggregated;
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

        // A child re-runs in a fresh CLR; apply the propagated environment controls (CPU
        // affinity, priority, guidance) so the child's measurements run under the same
        // hardware constraints as the parent. The scope restores on dispose.
        using var _ = EnvironmentControl.Apply(options.Environment);

        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime);

        foreach (var suite in _assemblies.SelectMany(discoverer.Discover))
        {
            if (suite.Type.FullName != request.DeclaringTypeFullName)
                continue;

            var selected = suite.Benchmarks.Where(b => requested.Contains(b.DisplayName)).ToList();

            if (selected.Count == 0)
                continue;

            if (suite.Lifetime == InstanceLifetime.PerClass)
            {
                return await RunPerClassHostChildAsync(suite, selected, options, ResolveChildObserver(request), cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunPerMethodHostChildAsync(suite, selected, options, ResolveChildObserver(request), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Isolated class '{request.DeclaringTypeFullName}' was not found.");

        // In the child: a non-zero exit code is what the parent's launcher reads as failure.
        Environment.ExitCode = 1;
        return Array.Empty<BenchmarkResult>();
    }

    /// <summary>
    ///     Resolves the observer names forwarded by the parent into a single
    /// <see cref="IMeasurementObserver" /> the child's measurement loop should see. The
    /// child re-runs the entry assembly, so <c>[ModuleInitializer]</c> self-registration
    /// populates <see cref="ObserverRegistry" /> identically and the names resolve to the
    /// same factories. An empty list collapses to <see cref="NullMeasurementObserver.Instance" />
    /// so the hot-path guard stays false and the child pays no dispatch cost.
    /// </summary>
    private static IMeasurementObserver ResolveChildObserver(IsolatedRunRequest request)
    {
        var names = request.ObserverNames;
        var resolved = new List<IMeasurementObserver>(names.Count);

        foreach (var name in names)
        {
            if (ObserverRegistry.TryCreate(name, out var observer)
                && observer != NullMeasurementObserver.Instance)
            {
                resolved.Add(observer);
            }
        }

        // Auto-attached observers also fire in children. EnsureExtensionsLoaded (called by
        // CreateAutoAttachedObservers) has loaded NBenchmark.* assemblies (including
        // NBenchmark.Studio, if referenced) and their [ModuleInitializer]s have registered
        // auto-attached observers. Dedup against the request's explicit observer names so
        // --observer studio does not double-attach. Mirrors the parent-side ResolveObserver.
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

    private async Task<IReadOnlyList<BenchmarkResult>> RunPerClassHostChildAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        IMeasurementObserver observer,
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

                // When the class implements IStateReset, fire ResetAsync between benchmark methods
                // to keep the shared instance's state clean across PerClass execution in the child.
                Func<Task>? betweenBenchmarksReset = typeof(IStateReset).IsAssignableFrom(suite.Type)
                    ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                    : null;

                (results, samples) = await SuiteRunner.RunAsync(
                    envelopes, RunOrder.Declaration, null, options,
                    0, selected.Count, NullBenchmarkProgress.Instance, cancellationToken, betweenBenchmarksReset,
                    observer).ConfigureAwait(false);

                ApplyPerClassIndependenceWarning(results, suite, options);
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
        IMeasurementObserver observer,
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
                    0, 1, NullBenchmarkProgress.Instance, cancellationToken,
                    onBetweenBenchmarksAsync: null, observer).ConfigureAwait(false);

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

    private static void ApplyPerClassIndependenceWarning(
        List<BenchmarkResult> results,
        BenchmarkSuiteDefinition suite,
        MeasurementOptions options)
    {
        if (options.SuppressPerClassIndependenceWarning)
            return;

        if (suite.Lifetime != InstanceLifetime.PerClass)
            return;

        if (suite.Benchmarks.Count <= 1)
            return;

        var warning = $"Class '{suite.Type.Name}' uses InstanceLifetime.PerClass with "
                      + $"{suite.Benchmarks.Count} [Benchmark] methods. Sharing a single instance "
                      + "across methods can cause the second method to observe cached state from "
                      + "the first, violating the statistical-independence assumption of the "
                      + "significance test. To preserve independence: implement IStateReset on the "
                      + "class (the engine will call it between methods), or add [IsolatedProcess] "
                      + "to run each method in a clean process. Set SuppressPerClassIndependenceWarning "
                      + "to true on MeasurementOptions only if sharing is intentional.";

        for (var i = 0; i < results.Count; i++)
        {
            results[i] = results[i] with
            {
                Warnings = results[i].Warnings.Count > 0
                    ? [.. results[i].Warnings, warning]
                    : [warning],
            };
        }
    }

    /// <summary>
    ///     Attaches the auto-isolation upgrade warning to every result whose benchmark
    ///     was auto-upgraded from PerClass to PerBenchmark by the b-factory rule in
    ///     <see cref="ResolveIsolation" />. The warning is attached in the parent process
    ///     after the isolated child results are folded back in, so it appears on the
    ///     results the user actually sees. Only the specific benchmarks that were
    ///     upgraded carry the warning; benchmarks from the same class that kept their
    ///     explicit isolation decision (e.g. <c>[InProcess]</c>) are left untouched.
    /// </summary>
    private static void ApplyAutoIsolationUpgradeWarning(
        List<BenchmarkResult> results,
        HashSet<string> autoUpgradedResultNames,
        int startIndex)
    {
        if (autoUpgradedResultNames.Count == 0)
            return;

        for (var i = startIndex; i < results.Count; i++)
        {
            if (!autoUpgradedResultNames.Contains(results[i].Name))
                continue;

            var warning = $"Class '{results[i].ClassName}' uses InstanceLifetime.PerClass with a "
                          + "factory-resolved instance and does not implement IStateReset; upgrading to "
                          + "per-benchmark isolated process to preserve statistical independence. Implement "
                          + "IStateReset on the class to allow in-process PerClass execution.";

            results[i] = results[i] with
            {
                Warnings = results[i].Warnings.Count > 0
                    ? [.. results[i].Warnings, warning]
                    : [warning],
            };
        }
    }

    private void ApplyOutputDirectory(string outputDir)
    {
        for (var i = 0; i < _reporters.Count; i++)
        {
            if (ReporterRegistry.TryCreate(_reporters[i].Name, outputDir, _detail, out var rebuilt))
                _reporters[i] = rebuilt;
        }
    }

    private async Task InvokeReportersAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
    {
        await ReporterRegistry.InvokeReportersAsync(_reporters, _detail, results, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<BenchmarkSuiteDefinition> FilterSuites(
        IReadOnlyList<BenchmarkSuiteDefinition> suites,
        string? filter,
        IReadOnlyList<string> cliInclude,
        IReadOnlyList<string> cliExclude,
        IReadOnlyList<string> harnessInclude,
        IReadOnlyList<string> harnessExclude)
    {
        var hasIncludeFilter = cliInclude.Count > 0 || harnessInclude.Count > 0;

        if (filter is null && !hasIncludeFilter && cliExclude.Count == 0 && harnessExclude.Count == 0)
            return suites;

        var exclude = UnionCategories(cliExclude, harnessExclude);

        var filtered = suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b =>
                    {
                        if (filter is not null && !GlobMatcher.Match(filter, $"{s.Type.Name}.{b.DisplayName}"))
                            return false;

                        return CategoryFilter.Matches(b.Categories, cliInclude, harnessInclude, exclude, hasIncludeFilter);
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

    private IReadOnlyList<RuntimeMoniker> DiscoverAttributeRuntimes()
    {
        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime);
        var allSuites = _assemblies.SelectMany(discoverer.Discover).ToList();

        var filtered = FilterSuites(allSuites, _cliArgs.Filter, _cliArgs.CategoryFilterInclude,
            _cliArgs.CategoryFilterExclude, _categoryFilterInclude, _categoryFilterExclude);

        return AggregateRuntimes(filtered);
    }

    internal static IReadOnlyList<RuntimeMoniker> AggregateRuntimes(
        IReadOnlyList<BenchmarkSuiteDefinition> suites)
    {
        var union = new List<RuntimeMoniker>();
        var seen = new HashSet<RuntimeMoniker>();

        foreach (var suite in suites)
        {
            foreach (var moniker in suite.Runtimes)
            {
                if (seen.Add(moniker))
                    union.Add(moniker);
            }
        }

        return union;
    }

    private static MeasurementOptions MergeCliOptions(MeasurementOptions options, CliArgs cliArgs)
        => MeasurementOverrides.FromCliArgs(cliArgs).Apply(options);
}
