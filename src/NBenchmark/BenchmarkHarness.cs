using System.Diagnostics;
using System.Reflection;
using NBenchmark.Diagnostics;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using NBenchmark.Observers;
using NBenchmark.Reporters;
using NBenchmark.Stats;
using NBenchmark.Workers;

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
    private readonly List<IMeasurementObserver> _observers = [];
    private readonly List<IReporter> _reporters = [];
    private CliArgs _cliArgs = new();
    private bool _crossClass;
    private InstanceLifetime _defaultInstanceLifetime = InstanceLifetime.PerMethod;
    private ReportDetail _detail;
    private Func<Type, InstanceHandle>? _instanceFactory;
    private bool _isolationEnabled = true;
    private bool _launchCountExplicit;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private bool _optionsExplicitlySet;
    private bool _noSamples;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
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

    /// <summary>
    ///     The options an isolated child should be launched under, with CLI overrides merged in.
    ///     Used by the child-request builders for the values the parent must resolve on the child's
    ///     behalf - the wall-clock timeout and the runtime profile, neither of which a child can
    ///     work out for itself (no user arguments are forwarded, and runtime knobs are fixed before
    ///     any managed code runs).
    /// </summary>
    private MeasurementOptions ChildLaunchOptions => MergeCliOptions(EffectiveBaseOptions, _cliArgs);

    public static BenchmarkHarness Create(string[] args)
    {
        var cliArgs = CliArgs.Parse(args);
        var harness = new BenchmarkHarness();
        harness._cliArgs = cliArgs;
        harness._detail = cliArgs.Detail;
        harness._noSamples = cliArgs.NoSamples;

        foreach (var name in cliArgs.ReporterNames)
        {
            if (ReporterRegistry.TryCreate(name, null, cliArgs.Detail, out var reporter))
                harness._reporters.Add(reporter);
        }

        harness.ApplyNoSamples();

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
        ApplyNoSamples();
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
        {
            explicitNames.Add(name);
        }

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
    public BenchmarkHarness WithProcessPriority(ProcessPriorityClass priority)
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
    ///     Suppresses the always-on Debug-build / debugger-attached guidance warning for
    ///     this harness run. Use when measuring Debug behavior is intentional. Also
    ///     propagated to isolated child processes.
    /// </summary>
    public BenchmarkHarness WithSuppressBuildConfigurationWarning(bool suppress = true)
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
    ///     Sets the runtime-startup configuration to measure under - JIT tiering, dynamic PGO,
    ///     ReadyToRun and GC flavour. Defaults to <see cref="RuntimeProfile.SteadyState" />.
    ///     <para>
    ///         This is the setting that requires a child process to exist: the runtime reads these
    ///         knobs once at startup, so they can only be applied to a process being launched.
    ///         Benchmarks that run in the host process report <c>"host"</c> and inherit its
    ///         configuration.
    ///     </para>
    /// </summary>
    public BenchmarkHarness WithRuntimeProfile(RuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _options = _options with { RuntimeProfile = profile };
        _optionsExplicitlySet = true;
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

        return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Measures everything a second time in this process and prints the difference.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The second pass is a full re-run rather than a cheap estimate, because the effect
    ///         being demonstrated <i>is</i> the measurement: an in-process reading that has not gone
    ///         through the real warmup and tuning path is not the number a user would have trusted.
    ///     </para>
    ///     <para>
    ///         Reporters and gates are suppressed for it. It exists to be compared against, not to be
    ///         published, and letting it write files or move an exit code would make a diagnostic
    ///         command change the build's outcome.
    ///     </para>
    /// </remarks>
    private async Task VerifyIsolationAsync(
        IReadOnlyList<BenchmarkResult> isolated,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Re-measuring in this process for comparison...");

        // The same harness re-run with isolation forced off, rather than a second harness built to
        // look like this one. A copy would have to reproduce every builder call the user made -
        // filters, categories, options, instance lifetime - and any field missed would silently
        // compare two different sets of benchmarks while presenting the result as one set measured
        // two ways. That is a worse failure than not offering the command.
        var savedArgs = _cliArgs;
        var savedReporters = _reporters.ToList();
        var savedProgress = _progress;
        var savedProgressExplicit = _progressExplicitlySet;

        // Reporters and gates are suppressed: this pass exists to be compared against, not
        // published. Letting it write files or move the exit code would make a diagnostic command
        // change the build's outcome.
        _cliArgs = _cliArgs with
        {
            InProcess = true,
            VerifyIsolation = false,
            StrictIsolation = false,
            ThresholdPct = null,
            OutputDir = null,
        };

        _reporters.Clear();
        _progress = NullBenchmarkProgress.Instance;
        _progressExplicitlySet = true;

        try
        {
            var inProcess = await RunCoreAsync(cancellationToken).ConfigureAwait(false);

            IsolationAudit.Render(isolated, inProcess, Console.Out);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed comparison pass must not fail the run it was only commenting on.
            Console.Error.WriteLine($"--verify-isolation: the in-process comparison pass failed: {ex.Message}");
        }
        finally
        {
            _cliArgs = savedArgs;
            _reporters.Clear();
            _reporters.AddRange(savedReporters);
            _progress = savedProgress;
            _progressExplicitlySet = savedProgressExplicit;
        }
    }

    private static IDisposable ApplyCliOtelEndpointScope(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return NoopScope.Instance;

        var previousNBenchmarkEndpoint = Environment.GetEnvironmentVariable(MeasurementBudget.OtelEndpointEnvVar);
        var previousOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        Environment.SetEnvironmentVariable(MeasurementBudget.OtelEndpointEnvVar, endpoint);

        if (string.IsNullOrEmpty(previousOtlpEndpoint))
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);

        return new OtlpEndpointScope(previousNBenchmarkEndpoint, previousOtlpEndpoint);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(CancellationToken cancellationToken)
    {
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
        // guidance) for the duration of the run. The scope restores the prior process state on
        // dispose. A worker applies the same settings to itself from the options it was sent; this
        // covers in-process benchmarks and the coordinator's own thread.
        using var _ = EnvironmentControl.Apply(suiteOptions.Environment);

        var allNames = filtered
            .SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.DisplayName}"))
            .ToList();

        var totalBenchmarks = allNames.Count;

        NBenchmarkDiagnostics.OnSuiteStarting(
            _cliArgs.Filter ?? "harness",
            totalBenchmarks,
            _options.Profile.ToString(),
            _cliArgs.Runtimes is { Count: > 0 } runtimes ? string.Join(",", runtimes.Select(r => r.ToTargetFramework())) : null,
            _cliArgs.Seed,
            (_cliArgs.RunOrder ?? _runOrder).ToString());

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

                // Whether a worker can measure this class at all. When it cannot, the benchmarks run
                // in the host process and say so, rather than being quietly measured under whatever
                // configuration the host happened to start with.
                var workerDecision = inProcessGlobal
                    ? new WorkerRunPlan.Decision(WorkerRunPlan.Refusal.RequestedInProcess, null)
                    : WorkerRunPlan.ForDiscoveredClass(
                        suite.Type.Assembly.Location, _instanceFactory is not null, suiteOptions);

                if (workerDecision is { CanIsolate: false, Explanation: { } explanation })
                    EmitIsolationRefusal(suite.Type.Name, explanation);

                var forceInProcess = inProcessGlobal || !workerDecision.CanIsolate;

                foreach (var benchmark in suite.Benchmarks)
                {
                    var decision = ResolveIsolation(benchmark, suite, forceInProcess, _instanceFactory is not null, out var autoUpgraded);

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
                    var inProcessStart = allResults.Count;

                    await RunInProcessSuiteAsync(
                        suite with { Benchmarks = inProcess }, suiteOptions, runningIndex, totalBenchmarks,
                        allResults, rawSamples, observer, cancellationToken).ConfigureAwait(false);

                    // A benchmark that carries [InProcess] chose the host itself, whatever the class
                    // as a whole could or could not do; anything else landed here because of the
                    // class-level refusal, and should say which one.
                    StampIsolationStatus(allResults, inProcessStart, inProcess, workerDecision.Status);

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
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
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
                    string.Empty,
                    MeasurementPhase.SuiteCompleted,
                    PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(allResults);
        }

        // SuiteRunner and the isolated-launch aggregators key raw samples by benchmark name;
        // ApplyPerClassSignificance needs the composite name+runtime key so multi-runtime
        // results don't collide.
        rawSamples = RawSampleKey.ToComposite(allResults, rawSamples);

        ApplyPerClassSignificance(allResults, rawSamples, suiteOptions, _cliArgs.CrossClass || _crossClass);

        if (_cliArgs.ThresholdPct.HasValue
            && ThresholdCheck.HasRegression(allResults, _cliArgs.ThresholdPct.Value) is (true, var regressed))
        {
            Console.Error.WriteLine(
                $"Regression threshold exceeded ({_cliArgs.ThresholdPct.Value}%). "
                + $"Regressed benchmarks: {string.Join(", ", regressed)}");

            Environment.ExitCode = 1;
        }

        await FinalizeRunAsync(allResults, cancellationToken).ConfigureAwait(false);

        // After the reporters, because the comparison is commentary on the table just shown and is
        // meaningless without it.
        if (_cliArgs.VerifyIsolation)
            await VerifyIsolationAsync(allResults, cancellationToken).ConfigureAwait(false);

        return allResults;
    }

    /// <summary>
    ///     The steps every completed harness run ends with: the isolation gate, the output
    ///     directory, the reporters, and the isolation comparison.
    /// </summary>
    /// <remarks>
    ///     Shared because there are two run paths that both finish this way, and they had already
    ///     drifted once - a flag wired into one of them appeared to do nothing at all from the other.
    ///     That is the same defect shape as the composite-key bug that spread across nine call sites
    ///     and silently disagreed, so the tail lives in one place.
    /// </remarks>
    private async Task FinalizeRunAsync(
        IReadOnlyList<BenchmarkResult> allResults,
        CancellationToken cancellationToken)
    {
        // Enforced before reporters run, so the failure is stated up front rather than after several
        // screens of tables the user is being told not to trust.
        if (_cliArgs.StrictIsolation && !IsolationAudit.Enforce(allResults, Console.Error))
            Environment.ExitCode = 1;

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

        // Apply opt-in hardware/OS controls to this process for the duration of the multi-runtime
        // run, mirroring the single-runtime path. Each worker applies the same settings to itself
        // from the options it was sent; this scope covers the coordinator's own aggregation work.
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

                    var (runtimeResults, runtimeSamples) = await RunForRuntimeAsync(
                            build.Moniker, build, filtered, observer, cancellationToken)
                        .ConfigureAwait(false);

                    // Results already carry their own samples: they arrived in the same frame, so
                    // there is no side table to look them up in and no key that can fail to match.
                    allResults.AddRange(runtimeResults);

                    foreach (var (key, samples) in runtimeSamples)
                    {
                        rawSamples[key] = samples;
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
                foreach (var runtimeGroup in allResults.GroupBy(ComparisonGroup.KeyFor))
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

            await FinalizeRunAsync(allResults, cancellationToken).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true.
            observer.OnPhase(new MeasurementPhaseEvent(
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
                Succeeded: true));

            sentinelEmitted = true;

            // After the reporters, because the comparison is a commentary on the table just shown
            // and is meaningless without it.
            if (_cliArgs.VerifyIsolation)
                await VerifyIsolationAsync(allResults, cancellationToken).ConfigureAwait(false);

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
        }
    }

    /// <summary>
    ///     Measures every selected class against one target framework's build, in that build's own
    ///     worker.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The worker is taken from the build's output directory rather than from beside this
    ///         process. A worker is a framework-dependent assembly, so only the net8.0 worker can
    ///         load a net8.0 build - and the build targets already deployed it there, next to the
    ///         code under test. That makes worker selection a lookup rather than a policy.
    ///     </para>
    ///     <para>
    ///         A build whose output has no worker is reported and skipped rather than measured in
    ///         this process. Falling back would measure the <i>coordinator's</i> runtime while
    ///         labelling the row with another framework's moniker, which is a worse answer than no
    ///         answer.
    ///     </para>
    /// </remarks>
    private async Task<(List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples)>
        RunForRuntimeAsync(
            RuntimeMoniker moniker,
            TfmBuild build,
            IReadOnlyList<BenchmarkSuiteDefinition> filteredSuites,
            IMeasurementObserver observer,
            CancellationToken cancellationToken)
    {
        var results = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var tfm = moniker.ToTargetFramework();

        var workerPath = WorkerLocator.ForOutputDirectory(build.OutputDirectory);

        if (workerPath is null)
        {
            Console.Error.WriteLine(
                $"  {tfm}: no measurement worker was found in the build output "
                + $"('{build.OutputDirectory}'), so this runtime was skipped. Measuring it in this "
                + "process would report this runtime's numbers from a different one.");

            return (results, rawSamples);
        }

        var options = ChildLaunchOptions;

        // Said out loud rather than refused. Every other mode declines to isolate a run whose custom
        // strategy a worker cannot rebuild, but this one has no in-process fallback available - the
        // point is to measure another framework's build - so the honest move is to name the downgrade
        // instead of hiding it behind a skipped runtime.
        if (WorkerRunPlan.UnrebuildableStrategy(options) is { } strategyRefusal)
        {
            Console.Error.WriteLine(
                $"  {tfm}: {strategyRefusal} These benchmarks will be scored with the built-in "
                + "strategy instead. Give it a parameterless constructor to carry it across.");
        }

        foreach (var suite in filteredSuites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayNames = suite.Benchmarks.Select(b => b.DisplayName).ToList();

            var request = new RunGroupPayload
            {
                GroupId = $"{tfm}:{suite.Type.Name}",
                Kind = WorkGroupKind.DiscoveredClass,

                // The class under test lives in the build for this framework, not in the assembly
                // this coordinator was loaded from.
                TargetAssemblyPath = build.DllPath!,
                WorkerAssemblyPath = workerPath,
                DeclaringTypeFullName = suite.Type.FullName,
                DisplayPrefix = suite.Type.Name,
                BenchmarkNames = displayNames,

                // One worker per (runtime x class), so this worker measures once - the same invariant
                // every other request path pins, and leaving it unpinned here made this the one place
                // a worker could be asked to spend replicates it has no way to report separately.
                Options = options with { LaunchCount = 1 },
                OutlierDetectorTypeName = WorkerRunPlan.StrategyTypeName(options.OutlierDetector, out _),
                SignificanceTestTypeName = WorkerRunPlan.StrategyTypeName(options.SignificanceTest, out _),
                Order = _cliArgs.RunOrder ?? _runOrder,
                Seed = _cliArgs.Seed,
                DefaultInstanceLifetime = _defaultInstanceLifetime,
                TotalBenchmarks = displayNames.Count,
            };

            var group = await WorkerLauncher.Current.RunGroupAsync(
                    request, _progress, observer, MeasurementBudget.For(options, displayNames.Count),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var fault in group.Faults)
            {
                Console.Error.WriteLine($"  {tfm}: {fault.Message}");
            }

            foreach (var result in group.Results)
            {
                // Stamped with the framework it was measured under, which is what keeps rows from
                // different runtimes out of each other's comparison groups.
                var stamped = result with
                {
                    RuntimeMoniker = tfm,
                    IsolationStatus = IsolationStatus.Isolated,
                    RawSamples = group.RawSamples.GetValueOrDefault(result.Name, []),
                };

                results.Add(stamped);
                rawSamples[RawSampleKey.For(stamped.Name, tfm)] = group.RawSamples.GetValueOrDefault(result.Name, []);

                // The coordinator emits OnResult for every runtime's results so an observer sees one
                // stream across the whole run.
                observer.OnResult(stamped);
            }
        }

        return (results, rawSamples);
    }

    private static void ApplyPerClassSignificance(
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options,
        bool crossClass = false)
    {
        static string RawKey(BenchmarkResult r)
        {
            return RawSampleKey.For(r);
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
                .GroupBy(ri => ComparisonGroup.KeyFor(ri.Result))
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
                    .GroupBy(ri => ComparisonGroup.KeyFor(ri.Result))
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
                         .GroupBy(ri => ComparisonGroup.KeyFor(ri.Result)))
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
                         .GroupBy(ri => ComparisonGroup.KeyFor(ri.Result)))
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
                            startIndex, totalBenchmarks, progress, cancellationToken, null, launchObserver).ConfigureAwait(false);

                        perLaunchResults.AddRange(results);
                        perLaunchSamples.Add(samples);
                    }

                    // Combine reads the representative launch's samples off the pairing, so the
                    // displayed trim marks still index the array they were computed against. The
                    // pooled samples below are what significance reads across every launch.
                    var launches = perLaunchResults
                        .Select((r, i) => new LaunchAggregator.Launch(
                            r, perLaunchSamples[i].GetValueOrDefault(r.Name, [])))
                        .ToList();

                    allResults.Add(LaunchAggregator.Combine(launches));

                    foreach (var kvp in PoolRawSamplesByName(perLaunchSamples))
                    {
                        rawSamples[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    var (results, samples) = await SuiteRunner.RunAsync(
                        [envelope], _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                        startIndex, totalBenchmarks, _progress, cancellationToken, null, observer).ConfigureAwait(false);

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
            // Zipped before filtering, so a launch missing this benchmark drops its samples with it
            // rather than shifting every later launch's samples onto the wrong result.
            var launches = allLaunchResults
                .Select((launch, i) => (Result: launch.FirstOrDefault(r => r.Name == name), Index: i))
                .Where(x => x.Result is not null)
                .Select(x => new LaunchAggregator.Launch(
                    x.Result!, allLaunchSamples[x.Index].GetValueOrDefault(name, [])))
                .ToList();

            if (launches.Count == 0)
                continue;

            aggregated.Add(LaunchAggregator.Combine(launches));

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
        // A parameter sweep keeps its parameter values in declaration order and randomizes only
        // within each of them, because every comparison the table invites is within a parameter
        // value and interleaving the values would make the table unreadable for no statistical gain.
        return benchmarks.Any(b => b.ParameterSet.Count > 0)
            ? RunOrdering.ApplyWithinGroups(benchmarks, order, seed, b => BenchmarkParameter.GetKey(b.ParameterSet))
            : RunOrdering.Apply(benchmarks, order, seed);
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

        var options = ChildLaunchOptions;
        var names = benchmarks.Select(b => b.DisplayName).ToList();
        var timeout = MeasurementBudget.For(options, benchmarks.Count);

        // Each replicate is its own worker, so LaunchCount buys a between-process variance estimate
        // rather than repeated measurements inside one process. That is the reproducibility number a
        // regression gate needs; the within-process interval only describes precision, and the data
        // shows how misleading it is alone - a standard deviation of 0.16 ns on an 11 ns reading
        // while the true run-to-run spread was 3.27x.
        var allLaunchItems = new List<IReadOnlyList<IsolatedResultItem>>(effectiveLaunchCount);

        for (var replicate = 0; replicate < effectiveLaunchCount; replicate++)
        {
            var request = WorkerRunPlan.DiscoveredClassRequest(
                suite.Type,
                names,
                options,
                _defaultInstanceLifetime,
                _cliArgs.RunOrder ?? _runOrder,
                _cliArgs.Seed,
                replicate,
                startIndex,
                totalBenchmarks,
                WorkerRunPlan.StrategyTypeName(options.OutlierDetector, out _),
                WorkerRunPlan.StrategyTypeName(options.SignificanceTest, out _));

            allLaunchItems.Add(
                await RunGroupInWorkerAsync(request, replicate, names, suite, timeout, observer, cancellationToken)
                    .ConfigureAwait(false));
        }

        var items = effectiveLaunchCount > 1
            ? HostAggregateIsolatedLaunches(allLaunchItems, benchmarks, suite)
            : allLaunchItems[0];

        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

        foreach (var benchmark in benchmarks)
        {
            var name = $"{suite.Type.Name}.{benchmark.DisplayName}";

            BenchmarkResult result;
            double[] raw;

            if (byName.TryGetValue(name, out var item))
            {
                // Re-attach display samples the child stripped from its serialized result. For
                // launch-aggregated items, prefer the representative launch samples kept on
                // Result so TrimmedOrdinals stay aligned with the shown distribution.
                result = item.Result with { RawSamples = ResolveResultRawSamples(item) };
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

    /// <summary>
    ///     Records why each in-process result ran in the host, on the result itself.
    ///     <para>
    ///         A benchmark carrying <c>[InProcess]</c> asked for the host and is stamped as such even
    ///         when the class was also refused for another reason - the explicit choice is the true
    ///         explanation, and reporting a refusal the user never hit would send them chasing a
    ///         problem they do not have.
    ///     </para>
    /// </summary>
    private static void StampIsolationStatus(
        List<BenchmarkResult> results,
        int startIndex,
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        IsolationStatus classStatus)
    {
        var explicitlyInProcess = benchmarks
            .Where(b => b.Isolation == IsolationMode.InProcess)
            .Select(b => b.DisplayName)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = startIndex; i < results.Count; i++)
        {
            var result = results[i];

            var status = explicitlyInProcess.Contains(MethodNameOf(result.Name))
                ? IsolationStatus.InProcessRequested
                : classStatus;

            results[i] = result with
            {
                IsolationStatus = status == IsolationStatus.Isolated
                    ? IsolationStatus.InProcessRequested
                    : status,
            };
        }
    }

    /// <summary>Strips the class prefix from a <c>Class.Method</c> result name.</summary>
    private static string MethodNameOf(string resultName)
    {
        var separator = resultName.LastIndexOf('.');

        return separator < 0 ? resultName : resultName[(separator + 1)..];
    }

    /// <summary>
    ///     Reports, once per class, that isolation was declined and why.
    ///     <para>
    ///         This is the visible half of "refuse rather than guess". A silent fallback to
    ///         in-process would be the worst outcome available: on bodies of provably identical cost,
    ///         in-process runs spanned 3.27x and fabricated a 2.80x difference between two of them,
    ///         each reported with a tight confidence interval. The results themselves are also
    ///         stamped <c>host</c>, so the provenance survives even if this message is scrolled past.
    ///     </para>
    /// </summary>
    private void EmitIsolationRefusal(string className, string explanation)
    {
        if (!_isolationRefusalsReported.Add(className))
            return;

        Console.Error.WriteLine(
            $"Isolation: '{className}' is being measured in this process because {explanation}");

        Console.Error.WriteLine(
            "  In-process measurements cannot control JIT tiering, PGO, ReadyToRun or GC flavour, "
            + "because the runtime fixes those at startup. They are stamped 'host' and are never "
            + "compared against isolated results.");
    }

    private readonly HashSet<string> _isolationRefusalsReported = new(StringComparer.Ordinal);

    /// <summary>
    ///     Spawns one worker, measures one replicate of a group in it, and shuts it down.
    ///     <para>
    ///         A worker is single-use. Recycling one across groups is the obvious optimisation and is
    ///         disqualified: the only way to unload a target assembly is a collectible load context,
    ///         and a collectible context reaches static fields through a <c>LoaderAllocator</c>
    ///         indirection that inflates any benchmark touching a static - an overhead the report
    ///         would attribute to the user's code.
    ///     </para>
    ///     <para>
    ///         Progress is replayed into the real progress instance for the first replicate only.
    ///         Later replicates measure the same benchmarks again, so forwarding their lifecycle
    ///         events would make a progress bar appear to run backwards.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<IsolatedResultItem>> RunGroupInWorkerAsync(
        RunGroupPayload request,
        int replicate,
        IReadOnlyList<string> benchmarkNames,
        BenchmarkSuiteDefinition suite,
        TimeSpan timeout,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var isFirstReplicate = replicate == 0;

        var group = await WorkerLauncher.Current.RunGroupAsync(
                request,
                isFirstReplicate ? _progress : NullBenchmarkProgress.Instance,
                isFirstReplicate ? observer : NullMeasurementObserver.Instance,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

        var items = group.Results
            .Select(result => new IsolatedResultItem
            {
                Result = result,
                RawSamples = group.RawSamples.GetValueOrDefault(result.Name, []),
            })
            .ToList();

        // Benchmarks the worker never reported become errored rows naming the reason, so a failure
        // is visible in the table rather than a silently missing line.
        foreach (var errored in WorkerGroupRunner.ToErroredResults(group, benchmarkNames, suite.Type.Name))
        {
            items.Add(new IsolatedResultItem { Result = errored, RawSamples = [] });
        }

        return items;
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
            var perLaunchResults = new List<LaunchAggregator.Launch>();

            foreach (var launchItems in allLaunchItems)
            {
                var match = launchItems.FirstOrDefault(item => item.Result.Name == name);

                if (match is not null)
                    perLaunchResults.Add(new LaunchAggregator.Launch(match.Result, match.RawSamples));
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

            var rawSamples = allLaunchItems
                .SelectMany(launchItems => launchItems.Where(item => item.Result.Name == name))
                .SelectMany(item => item.RawSamples)
                .ToArray();

            // Combine averages the per-launch estimates and takes the reported interval from the
            // spread between the workers, so the number describes what a re-run would produce rather
            // than how precisely the luckiest worker measured it. It keeps the representative launch's
            // own samples on the row so the trim marks still index the array they came from; the
            // pooled array travels alongside for significance.
            //
            // Pooling samples across replicate workers multiplies statistical power without improving
            // reproducibility, so a difference far below the run-to-run noise can still read as
            // overwhelmingly significant. Combine attaches a warning where that is happening.
            aggregated.Add(new IsolatedResultItem
            {
                Result = LaunchAggregator.Combine(perLaunchResults),
                RawSamples = rawSamples,
            });
        }

        return aggregated;
    }

    /// <summary>
    ///     Warns on every result from a class that shares one instance across several benchmark
    ///     methods, because the second method can observe state the first left behind - which breaks
    ///     the independence assumption the significance test rests on.
    /// </summary>
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

        ApplyNoSamples();
    }

    private static IReadOnlyList<double> ResolveResultRawSamples(IsolatedResultItem item)
    {
        return item.Result.RawSamples.Count > 0 ? item.Result.RawSamples : item.RawSamples;
    }

    private void ApplyNoSamples()
    {
        if (!_noSamples)
            return;

        foreach (var reporter in _reporters)
        {
            if (reporter is JsonReporter json)
                json.IncludeSamples = false;
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

    private sealed class OtlpEndpointScope(string? previousNBenchmarkEndpoint, string? previousOtlpEndpoint) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(MeasurementBudget.OtelEndpointEnvVar, previousNBenchmarkEndpoint);
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
}
