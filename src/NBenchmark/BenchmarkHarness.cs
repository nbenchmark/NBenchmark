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
    private readonly List<Assembly> _assemblies = [];
    private readonly List<string> _categoryFilterExclude = [];
    private readonly List<string> _categoryFilterInclude = [];
    private readonly List<IMeasurementObserver> _observers = [];
    private readonly List<IReporter> _reporters = [];
    private CliArgs _cliArgs = new();
    private bool _crossClass;
    private InstanceLifetime _defaultInstanceLifetime = InstanceLifetime.PerMethod;
    private ReportDetail _detail;
    /// <summary>
    ///     Where benchmark instances come from, when they do not simply come from the type's own
    ///     constructor. Carries both the host-side resolver and the recipe a worker can follow, so the
    ///     harness can tell "live code, cannot isolate" from "addressable, can" - a distinction the two
    ///     unrelated fields this replaced could not express.
    /// </summary>
    private InstanceSource? _instanceSource;
    private bool _isolationEnabled = true;

    /// <summary>
    ///     The launch count the caller pinned, or <c>null</c> for "not pinned" - which is what
    ///     <see cref="LaunchCounts.HarnessDefault" /> fills in. A separate field rather than a value on
    ///     <see cref="_options" /> because a launch is a <i>process</i>, spent here; see
    ///     <see cref="LaunchCounts" />.
    /// </summary>
    private int? _launchCount;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private bool _noSamples;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;

    /// <summary>
    ///     The one discovery pass this harness makes, and the configuration it was made under.
    /// </summary>
    /// <remarks>
    ///     Memoised because discovery is not a pure read. It <i>invokes</i> every
    ///     <c>[BenchmarkCases]</c> source it meets, and a default run made two passes - one to read
    ///     <c>[Runtimes]</c> attributes, one to run - with a third for a multi-runtime dispatch. Each
    ///     ran every case source again, with its side effects, and nothing said the three passes had to
    ///     agree: a source yielding different values on a second call produced a run whose case names
    ///     came from one pass and whose runtime decision came from another. Keyed on the default
    ///     lifetime because that is the only input <see cref="BenchmarkDiscoverer" /> reads which this
    ///     type can still change after a run.
    /// </remarks>
    private (InstanceLifetime Lifetime, bool FactoryResolved, IReadOnlyList<BenchmarkSuiteDefinition> Suites)?
        _discovered;

    private BenchmarkHarness()
    {
    }

    internal Action? PostSuiteCleanup { get; set; }

    /// <inheritdoc cref="_discovered" />
    private IReadOnlyList<BenchmarkSuiteDefinition> DiscoverOnce()
    {
        var factoryResolved = _instanceSource is not null;

        if (_discovered is { } cached
            && cached.Lifetime == _defaultInstanceLifetime
            && cached.FactoryResolved == factoryResolved)
        {
            return cached.Suites;
        }

        var discoverer = new BenchmarkDiscoverer(_defaultInstanceLifetime, factoryResolved);
        var suites = _assemblies.SelectMany(discoverer.Discover).ToList();

        _discovered = (_defaultInstanceLifetime, factoryResolved, suites);

        return suites;
    }

    /// <summary>
    ///     How many launches - that is, how many worker processes - each benchmark gets, before any
    ///     per-method <c>[Benchmark(LaunchCount = ...)]</c> override is layered on top.
    ///     <para>
    ///         <c>--launch-count</c> wins over <see cref="WithLaunchCount" />, and with neither set
    ///         harness mode launches <see cref="LaunchCounts.HarnessDefault" /> times so the
    ///         launch-aggregation view surfaces run-to-run variance without opt-in.
    ///         <see cref="WithOptions" /> has no say here, because a launch count is not a
    ///         measurement option - see <see cref="LaunchCounts" />.
    ///     </para>
    ///     <para>
    ///         A dry run takes neither the default nor the flag: it exists to prove the wiring works
    ///         without measuring anything, so repeating it in three processes would be three times the
    ///         startup cost for the same nothing.
    ///     </para>
    /// </summary>
    private int EffectiveLaunchCount
        => _cliArgs.DryRun
            ? _launchCount ?? LaunchCounts.Single
            : _cliArgs.LaunchCount ?? _launchCount ?? LaunchCounts.HarnessDefault;

    /// <summary>
    ///     The options an isolated child should be launched under, with CLI overrides merged in.
    ///     Used by the child-request builders for the values the parent must resolve on the child's
    ///     behalf - the wall-clock timeout and the runtime profile, neither of which a child can
    ///     work out for itself (no user arguments are forwarded, and runtime knobs are fixed before
    ///     any managed code runs).
    /// </summary>
    private MeasurementOptions ChildLaunchOptions => MergeCliOptions(_options, _cliArgs);

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
        return this;
    }

    /// <summary>
    ///     Pins the number of times each benchmark repeats as an independent launch - one worker
    ///     process each, with its own warmup and measurement pass. The per-launch medians are
    ///     aggregated into a launch-level confidence interval that surfaces run-to-run variance from
    ///     process-level effects (ASLR, scheduler placement, tiered JIT).
    ///     <para>
    ///         When unset, harness mode launches <see cref="LaunchCounts.HarnessDefault" /> times so
    ///         the launch-aggregation table is shown without opt-in. Pass
    ///         <see cref="LaunchCounts.Single" /> to restore single-launch behaviour.
    ///     </para>
    /// </summary>
    public BenchmarkHarness WithLaunchCount(int count)
    {
        if (!LaunchCounts.IsValid(count))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                $"LaunchCount must be between {LaunchCounts.Single} and {LaunchCounts.Max}.");
        }

        _launchCount = count;
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
        ArgumentNullException.ThrowIfNull(factory);

        // The user's own delegate is kept as the recipe rather than only the wrapper built around it.
        // A static, non-capturing factory is addressable, so the worker can run it and resolve
        // instances in the process that measures - which is what stops WithInstanceFactory from
        // costing every run its isolation regardless of how the factory was written.
        return WithInstanceSource(new InstanceSource
        {
            Kind = InstanceSourceKind.InstanceFactory,
            Recipe = factory,
            Resolve = type => InstanceHandle.NoTeardown(factory(type)),
        });
    }

    internal BenchmarkHarness WithInstanceFactory(Func<Type, InstanceHandle> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return WithInstanceSource(new InstanceSource
        {
            Kind = InstanceSourceKind.InstanceFactory,
            Resolve = factory,
        });
    }

    /// <summary>
    ///     Records how instances are obtained. Internal so <c>NBenchmark.DependencyInjection</c> can
    ///     declare a scoped source, which core has no way to build without a container dependency.
    /// </summary>
    internal BenchmarkHarness WithInstanceSource(InstanceSource source)
    {
        _instanceSource = source ?? throw new ArgumentNullException(nameof(source));

        return this;
    }

    /// <summary>
    ///     Why the configured instance source cannot be reproduced in a worker, or <c>null</c> when it
    ///     can. A test seam for the DI package, whose own tests have no worker deployed beside them and
    ///     so cannot ask the whole isolation question.
    /// </summary>
    internal string? InstanceSourceRefusalForTesting() => _instanceSource?.Refusal();

    /// <summary>
    ///     Configures the harness to resolve benchmark instances from a provider built by
    ///     <paramref name="factory" />, so DI-backed benchmarks can still be measured in a worker.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is no overload taking a built <see cref="IServiceProvider" />, on purpose. A service
    ///         provider is live code: it holds singletons, open connections and closures that cannot
    ///         cross a process boundary, so passing one would cost the run its isolation before anything
    ///         is measured. The <i>recipe</i> for a provider can cross, though - a static factory that
    ///         registers the services and builds the container is addressable, and the worker runs it to
    ///         get an equivalent container in the process doing the measuring:
    ///     </para>
    ///     <code>
    ///     await BenchmarkHarness.Create(args)
    ///         .AddFromAssembly&lt;MyBenchmarks&gt;()
    ///         .WithServiceProvider(BuildServices)
    ///         .RunAsync();
    ///
    ///     static IServiceProvider BuildServices() =&gt; new ServiceCollection()
    ///         .AddSingleton&lt;IDataStore, InMemoryDataStore&gt;()
    ///         .AddTransient&lt;MyBenchmarks&gt;()
    ///         .BuildServiceProvider();
    ///     </code>
    ///     <para>
    ///         The container the worker builds is a <i>different instance</i> from the one built here, and
    ///         that is the point rather than a caveat: a benchmark measured against a container warmed by
    ///         this process's own startup is measuring that warmth. The factory must capture nothing, for
    ///         the same reason a benchmark body must.
    ///     </para>
    /// </remarks>
    public BenchmarkHarness WithServiceProvider(Func<IServiceProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Deliberately not invoked here. The host-side container is only wanted if this process ends
        // up measuring, and on a fully isolated run - the default, and the whole point of passing a
        // factory - it never does. Building it eagerly opened a database and constructed an EF model
        // in a process with no benchmark in it, which the doc above says is exactly what a factory
        // exists to avoid.
        return WithInstanceSource(new InstanceSource
        {
            Kind = InstanceSourceKind.ServiceProvider,
            Recipe = factory,
            Resolve = ResolverFor(factory),
        });
    }

    /// <summary>
    ///     The instance resolver for a provider built by <paramref name="factory" />.
    /// </summary>
    private static Func<Type, InstanceHandle> ResolverFor(Func<IServiceProvider> factory)
    {
        // One container per harness, built on first use rather than at configuration time. Lazy
        // rather than eager is the whole of W-17; Lazy<T> rather than a null check because a run can
        // resolve instances from several threads and building the container twice would give two
        // sets of singletons.
        var provider = new Lazy<IServiceProvider>(
            () => factory() ?? throw new InvalidOperationException(
                "The service provider factory returned null."));

        return type =>
        {
            var instance = provider.Value.GetService(type)
                           ?? throw new InvalidOperationException(
                               $"No service of type '{type.FullName}' is registered in the service provider.");

            return InstanceHandle.NoTeardown(instance);
        };
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
    ///     Sets the minimum relative median shift (|candidate − baseline| / baseline median) a
    ///     change must reach to keep a Significant verdict, gated alongside the practical-effect
    ///     gate. Pass <c>0</c> to disable the relative-shift gate.
    /// </summary>
    public BenchmarkHarness WithMinimumRelativeShift(double minimumRelativeShift)
    {
        _options = _options with { MinimumRelativeShift = minimumRelativeShift };
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
    ///     Turns evidence-based interference rejection on or off. <b>On by default</b>: every timed
    ///     sample is bracketed with a thread-CPU-clock read, and a sample whose CPU occupancy falls
    ///     materially below this benchmark's own median is rejected before the statistical outlier
    ///     detector ever sees it. Pass <c>false</c> to trim only on the statistical detector, as
    ///     before this feature existed. See <see cref="InterferenceOptions" />.
    /// </summary>
    public BenchmarkHarness WithInterferenceFilter(bool enabled = true)
    {
        _options = _options with { Interference = _options.Interference with { Enabled = enabled } };
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
    ///     Turns the thread-level OS controls on or off. They are <b>on by default</b>: the
    ///     measuring thread takes an affinity matching <see cref="WithHardwareAffinity" />, a
    ///     priority matching <see cref="WithProcessPriority" />, and - on macOS - the
    ///     user-interactive quality of service that keeps it on an Apple Silicon performance
    ///     core. Propagated to every worker, each of which applies it to its own measuring
    ///     thread. Pass <c>false</c> to measure under the host's default thread scheduling.
    /// </summary>
    public BenchmarkHarness WithThreadControl(bool enabled = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { ThreadControl = enabled },
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
    ///     Configures the host drift canary - the deterministic control workload measured at each
    ///     benchmark boundary, which is what lets a run say how much the host's effective speed
    ///     moved while it was running. On by default.
    /// </summary>
    public BenchmarkHarness WithDriftCanary(DriftCanaryOptions driftCanary)
    {
        ArgumentNullException.ThrowIfNull(driftCanary);
        _options = _options with { DriftCanary = driftCanary };
        return this;
    }

    /// <summary>
    ///     Turns the host drift canary on or off. On by default; switching it off takes no control
    ///     readings between benchmarks and silences the host-drift warning.
    /// </summary>
    public BenchmarkHarness WithDriftCanary(bool enabled)
    {
        _options = _options with { DriftCanary = _options.DriftCanary with { Enabled = enabled } };
        return this;
    }

    /// <summary>
    ///     Controls Harness mode's isolated-by-default execution. When enabled (the default),
    ///     each discovered class runs in its own clean-room child process unless a benchmark
    ///     or its class opts out with <c>[InProcess]</c>. When disabled, every benchmark
    ///     runs in the host process - equivalent to passing <c>--in-process</c> on the CLI.
    /// </summary>
    /// <remarks>
    ///     Not quite the same method as <see cref="BenchmarkSuite.WithIsolation" />, despite the shared
    ///     name and signature. This one is a <i>global switch</i> that stays settable in both directions,
    ///     so a later <c>WithIsolation(true)</c> re-enables what an earlier <c>false</c> turned off.
    ///     Suite mode has one suite and therefore nothing to switch: there, <c>false</c> records an
    ///     explicit request for the host process and <c>true</c> is a no-op asking for the default.
    /// </remarks>
    public BenchmarkHarness WithIsolation(bool enabled = true)
    {
        _isolationEnabled = enabled;
        return this;
    }

    /// <summary>
    ///     Whether an isolation <b>refusal</b> fails the run instead of falling back to the host
    ///     process. On by default; <c>--strict-isolation</c> turns it on regardless.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The opposite of <see cref="WithIsolation" />, which says whether to <i>try</i>. This says
    ///         what happens when trying does not work. Turning isolation off is therefore not affected by
    ///         this at all - <c>WithIsolation(false)</c> asks for the host process and gets it, which is
    ///         not a refusal.
    ///     </para>
    ///     <para>
    ///         Exists because <c>MeasurementOptions.RequireIsolation</c> was unreachable from here:
    ///         <c>WithOptions(new MeasurementOptions { RequireIsolation = true })</c> set a field the
    ///         harness never read, so the one mode with a fully-fledged isolation pipeline was the one
    ///         mode that could not ask for it.
    ///     </para>
    /// </remarks>
    public BenchmarkHarness WithRequireIsolation(bool required = true)
    {
        _options = _options with { RequireIsolation = required };
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

        return await RunCoreAsync(RunPass.Primary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Which of the two things a run can be: the measured run the user asked for, or the in-process
    ///     comparison <c>--verify-isolation</c> measures to compare it against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A parameter rather than a set of fields the comparison pass overwrites and restores. The
    ///         previous shape saved four fields and leaked through every publish decision it had not
    ///         thought of: the user's observer received a second full suite stream for a pass whose
    ///         results are never published <i>and</i> was disposed twice, a second suite activity opened
    ///         under the same name, the refusal-dedup set was never cleared, and
    ///         <see cref="CliArgs.Runtimes" /> stayed populated - so on a cross-runtime run the
    ///         "in-process comparison" re-entered the multi-runtime orchestrator and was not in-process
    ///         at all.
    ///     </para>
    ///     <para>
    ///         Making it an argument turns "every publish decision must remember to check the flag" into
    ///         "the comparison pass cannot reach a publisher". There is nothing to restore because
    ///         nothing was changed.
    ///     </para>
    /// </remarks>
    private sealed record RunPass
    {
        public static readonly RunPass Primary = new();

        public static readonly RunPass InProcessComparison = new()
        {
            Publishes = false,
            ForceInProcess = true,
        };

        /// <summary>
        ///     Whether this pass owns the run's outputs: reporters, the regression and isolation gates,
        ///     the output directory, the exit code, the user's observer and progress, and the suite
        ///     activity's identity.
        /// </summary>
        public bool Publishes { get; init; } = true;

        /// <summary>
        ///     Measures in this process whatever the CLI, attributes or builder asked for.
        /// </summary>
        public bool ForceInProcess { get; init; }
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
    ///         It is the same harness re-run rather than a second harness built to look like this one. A
    ///         copy would have to reproduce every builder call the user made - filters, categories,
    ///         options, instance lifetime - and any field missed would silently compare two different
    ///         sets of benchmarks while presenting the result as one set measured two ways.
    ///     </para>
    ///     <para>
    ///         Nothing is saved or restored around it: <see cref="RunPass.InProcessComparison" /> says
    ///         what this pass is, and <see cref="RunCoreAsync" /> declines to publish on that basis. See
    ///         <see cref="RunPass" /> for what the save-and-restore version leaked.
    ///     </para>
    /// </remarks>
    private async Task VerifyIsolationAsync(
        IReadOnlyList<BenchmarkResult> isolated,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Re-measuring in this process for comparison...");

        try
        {
            var inProcess = await RunCoreAsync(RunPass.InProcessComparison, cancellationToken)
                .ConfigureAwait(false);

            IsolationAudit.Render(isolated, inProcess, Console.Out);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed comparison pass must not fail the run it was only commenting on.
            Console.Error.WriteLine($"--verify-isolation: the in-process comparison pass failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     The progress instance for this pass: whatever the caller attached, a console bar when nothing
    ///     was attached, or nothing at all for the comparison pass.
    /// </summary>
    /// <remarks>
    ///     Returned rather than assigned to <c>_progress</c>. The field used to be overwritten here when
    ///     no progress had been attached, which is why the comparison pass had to save and restore both
    ///     it and <c>_progressExplicitlySet</c> - and why forcing the flag true was load-bearing for
    ///     reasons no reader could see. <c>_progressExplicitlySet</c> is now write-once, set only by
    ///     <see cref="WithProgress" />.
    /// </remarks>
    private IBenchmarkProgress ResolveProgress(RunPass pass)
    {
        if (!pass.Publishes)
            return NullBenchmarkProgress.Instance;

        return _progressExplicitlySet ? _progress : new DefaultConsoleProgress();
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

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(
        RunPass pass,
        CancellationToken cancellationToken)
    {
        if (_cliArgs.ShowHelp)
        {
            CliArgs.PrintHelp();
            return Array.Empty<BenchmarkResult>();
        }

        // Skipped entirely for the comparison pass, which is defined as measuring *this* process. A
        // cross-runtime run reached here with Runtimes still populated and went straight back into the
        // multi-runtime orchestrator, so the "in-process comparison" spawned workers per target
        // framework and compared the run against itself. Not reachable now, rather than guarded there.
        if (!pass.ForceInProcess)
        {
            // When runtimes are specified (CLI or attribute), delegate to the multi-runtime
            // orchestrator. --help and --list-only are handled before this so they never trigger
            // multi-runtime builds.
            var effectiveRuntimes = _cliArgs.Runtimes;

            if (effectiveRuntimes.Count == 0 && !_cliArgs.ListOnly)
                effectiveRuntimes = DiscoverAttributeRuntimes();

            if (effectiveRuntimes.Count > 0)
            {
                if (_cliArgs.InProcess || !_isolationEnabled)
                    Console.WriteLine("Warning: cross-runtime execution always uses child processes.");

                return await RunMultiRuntimeAsync(effectiveRuntimes, cancellationToken).ConfigureAwait(false);
            }
        }

        if (pass.Publishes)
        {
            Console.WriteLine($"Timer resolution: {Stopwatch.Frequency:N0} ticks/s "
                              + $"({1_000_000_000.0 / Stopwatch.Frequency:F2} ns per tick)");

            Console.WriteLine();
        }

        var allSuites = DiscoverOnce();

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

        // A local, threaded down through the run rather than assigned to the field. The field was
        // mutated here and had to be saved and restored around the comparison pass; a parameter cannot
        // be left behind.
        var progress = ResolveProgress(pass);

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var suiteOptions = _cliArgs.DryRun
            ? _options with { Iterations = 0, WarmupIterations = 0 }
            : MergeCliOptions(_options, _cliArgs);

        // Under --dry-run, --in-process, or WithIsolation(false), nothing is spawned. A dry run never
        // invokes a body, so isolation would only add process overhead.
        var inProcessGlobal = pass.ForceInProcess
                              || _cliArgs.InProcess
                              || !_isolationEnabled
                              || _cliArgs.DryRun;

        // Every class's isolatability, answered here rather than one class at a time inside the run
        // loop. See ResolveIsolationPlan: this is what makes a refusal a fact about the run instead of
        // something classes 1..N-1 have already been measured past.
        var isolationPlan = ResolveIsolationPlan(filtered, inProcessGlobal, suiteOptions);

        // Apply opt-in hardware/OS controls (CPU affinity, process priority, dedicated-host
        // guidance) for the duration of the run. The scope restores the prior process state on
        // dispose. A worker applies the same settings to itself from the options it was sent; this
        // covers in-process benchmarks and the coordinator's own thread.
        using var _ = EnvironmentControl.Apply(suiteOptions.Environment);

        // Thread-scoped sibling, covering the same in-process rows. A worker opens its own on the
        // thread its measurement loop runs on, since a thread scope cannot cross a process.
        using var _thread = ThreadEnvironmentControl.Apply(suiteOptions.Environment);

        var allNames = filtered
            .SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.DisplayName}"))
            .ToList();

        var totalBenchmarks = allNames.Count;

        // Resolve the observer once for the whole run so auto-attached observers (e.g. a
        // live-streaming observer) see one stream per RunAsync, not one per per-class group.
        // The using disposes the observer (and its composite children) on both the success
        // and exception paths; the composite's Dispose fans out with try/catch isolation.
        //
        // The comparison pass gets the null observer, not the user's. It used to resolve the same
        // instance again from inside the primary pass's `using`, so a streaming observer received a
        // second SuiteStarting..SuiteCompleted stream for results that are never published and was then
        // disposed twice - a double-finalise for any observer whose Dispose closes out a session.
        //
        // Strictly before OnSuiteStarting below. An observer may be the thing that attaches a
        // listener to the ActivitySource - that is exactly what the OTLP exporter is - and
        // StartActivity returns null when nothing is listening yet. Resolving second cost the run
        // its root span, and with it the parent that every benchmark, phase and worker span in the
        // trace hangs from: the export still happened, and still looked like a set of unrelated
        // fragments.
        using var observer = pass.Publishes ? ResolveObserver() : NullMeasurementObserver.Instance;

        // Labelled rather than suppressed for the comparison pass. Suppressing only the suite span would
        // leave the per-benchmark spans - raised from deep inside the engine, where the pass is not
        // visible - as parentless roots, which is worse than a labelled parent. A diagnostic pass must
        // not impersonate the run, but it may describe itself.
        NBenchmarkDiagnostics.OnSuiteStarting(
            pass.Publishes
                ? _cliArgs.Filter ?? "harness"
                : $"{_cliArgs.Filter ?? "harness"} [in-process comparison]",
            totalBenchmarks,
            _options.Profile.ToString(),
            _cliArgs.Runtimes is { Count: > 0 } runtimes ? string.Join(",", runtimes.Select(r => r.ToTargetFramework())) : null,
            _cliArgs.Seed,
            (_cliArgs.RunOrder ?? _runOrder).ToString());

        var sentinelEmitted = false;

        try
        {
            await progress.OnSuiteStarting(allNames, totalBenchmarks).ConfigureAwait(false);

            var runningIndex = 0;

            foreach (var declared in filtered)
            {
                var suiteResultStart = allResults.Count;
                var inProcess = new List<BenchmarkMethodDefinition>();
                var perClass = new List<BenchmarkMethodDefinition>();
                var perBenchmark = new List<BenchmarkMethodDefinition>();

                // How long an instance lives, decided before and independently of where it is
                // measured. The two used to be one function, and this half was unreachable whenever
                // the other half said "in-process" - see ResolveGranularity.
                var lifetime = InstanceIndependence.ResolveLifetime(
                    declared.Type, declared.Lifetime, _instanceSource is not null, out var lifetimeDowngrade);

                var suite = declared with { Lifetime = lifetime };

                // Decided and reported before this loop started - see ResolveIsolationPlan. Looked up
                // rather than recomputed, so the answer the user was given up front is the answer the
                // run acts on.
                var workerDecision = isolationPlan[suite.Type];

                var forceInProcess = inProcessGlobal || !workerDecision.CanIsolate;

                foreach (var benchmark in suite.Benchmarks)
                {
                    var decision = ResolveGranularity(benchmark, forceInProcess);

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
                        allResults, rawSamples, progress, observer, cancellationToken).ConfigureAwait(false);

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
                        allResults, rawSamples, progress, observer, cancellationToken).ConfigureAwait(false);

                    runningIndex += perClass.Count;
                }

                foreach (var benchmark in perBenchmark)
                {
                    await RunIsolatedGroupAsync(
                        suite, [benchmark], runningIndex, totalBenchmarks,
                        allResults, rawSamples, progress, observer, cancellationToken).ConfigureAwait(false);

                    runningIndex++;
                }

                // Attached to the whole class's rows, because the lifetime is a property of the class
                // rather than of one method - unlike the granularity decision, which is per method.
                if (lifetimeDowngrade is not null)
                {
                    ApplyClassWarning(
                        allResults,
                        suiteResultStart,
                        BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type),
                        lifetimeDowngrade);
                }
            }

            await progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

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

        // One gate around everything that leaves this process: the regression and isolation checks, the
        // exit code, the output directory, the reporters, BenchmarkTable's cross-class static, and the
        // comparison pass itself. The comparison pass used to be kept out of all of these by editing the
        // CliArgs it read - five separate nullings, each of which had to be remembered. Now it cannot
        // reach them, so a sixth output added below is covered without anyone thinking about it.
        if (pass.Publishes)
        {
            if (_cliArgs.ThresholdPct.HasValue
                && ThresholdCheck.HasRegressionAcrossGroups(allResults, _cliArgs.ThresholdPct.Value) is (true, var regressed))
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
        }

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
        // Refused before anything is built, because this path has no in-process fallback to refuse
        // into: every group is measured in a worker by definition, and a worker handed no instance
        // source falls back to constructing the type. For a DI-only class that is a clean
        // instantiation failure, but a class that happens to have a parameterless constructor is
        // measured with every dependency unwired and reported under its own name - a silent
        // substitution, which is the one outcome the design refuses everywhere else. The
        // single-runtime path answers this through WorkerRunPlan.ForDiscoveredClass; this one never
        // consulted it.
        if (_instanceSource?.Refusal() is { } sourceRefusal)
        {
            Console.Error.WriteLine(
                $"A multi-runtime run cannot be measured because {sourceRefusal} Every runtime is "
                + "measured in a worker, so there is no in-process fallback to decline into here.");

            Environment.ExitCode = 1;

            return [];
        }

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

        var allSuites = DiscoverOnce();

        var filtered = FilterSuites(allSuites, _cliArgs.Filter, _cliArgs.CategoryFilterInclude,
            _cliArgs.CategoryFilterExclude, _categoryFilterInclude, _categoryFilterExclude);

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var suiteOptions = _cliArgs.DryRun
            ? _options with { Iterations = 0, WarmupIterations = 0 }
            : MergeCliOptions(_options, _cliArgs);

        // Apply opt-in hardware/OS controls to this process for the duration of the multi-runtime
        // run, mirroring the single-runtime path. Each worker applies the same settings to itself
        // from the options it was sent; this scope covers the coordinator's own aggregation work.
        using var _ = EnvironmentControl.Apply(suiteOptions.Environment);

        // Resolve the observer once for the whole multi-runtime run so auto-attached
        // observers see one stream per RunAsync, mirroring the single-runtime path. The
        // using disposes the observer on both the success and exception paths.
        //
        // Always the publishing pass: the comparison pass is defined as measuring this process and
        // returns before the multi-runtime branch is reached, so there is no non-publishing entry here.
        using var observer = ResolveObserver();
        var progress = ResolveProgress(RunPass.Primary);
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
                            build.Moniker, build, filtered, progress, observer, cancellationToken)
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

                // Threshold comparisons only make sense within the same runtime and the same
                // class - net8 will always look "slower" than net10, which would false-positive
                // every net8 row, and an unrelated benchmark in another class is not this
                // class's baseline. HasRegressionAcrossGroups partitions on both.
                if (ThresholdCheck.HasRegressionAcrossGroups(allResults, threshold) is (true, var regressedNames))
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

            // Refused rather than attempted. Forcing this pass in-process would compare every runtime's
            // rows against one host row - IsolationAudit.Render keys the host side by name, and a
            // moniker is the only thing that distinguishes those rows - printing a table that looks
            // like a finding and is not one. "In-process" has no defined meaning for a net8.0 build
            // measured from a net10.0 coordinator either. Previously this re-entered the multi-runtime
            // orchestrator and spawned workers, so the "in-process comparison" was neither.
            if (_cliArgs.VerifyIsolation)
            {
                IsolationAudit.RefuseCrossRuntimeComparison(
                    runtimes.Select(r => r.ToTargetFramework()), Console.Out);
            }

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
            IBenchmarkProgress progress,
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
        //
        // Carried onto the results as well as printed. WorkerRunPlan.Decision.Status exists precisely
        // so "the reason travels with the numbers rather than living only in a console message that
        // scrolls by", and this was the one downgrade that had no such carrier: the rows are stamped
        // Isolated below - correctly, they did run in a worker - so nothing on them said they had been
        // scored with a different statistical method than the one configured.
        var strategyDowngrade = WorkerRunPlan.UnrebuildableStrategy(options);

        if (strategyDowngrade is not null)
        {
            Console.Error.WriteLine(
                $"  {tfm}: {strategyDowngrade} These benchmarks will be scored with the built-in "
                + "strategy instead. Give it a parameterless constructor to carry it across.");
        }

        foreach (var suite in filteredSuites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayNames = suite.Benchmarks.Select(b => b.DisplayName).ToList();

            // Resolved here as well, and sent, so a container-resolved PerClass class is measured
            // with the same instance lifetime under every runtime. This path does not call
            // ResolveGranularity at all - there is only one granularity available to it - but the
            // lifetime question is independent of that and has the same answer.
            var lifetime = InstanceIndependence.ResolveLifetime(
                suite.Type, suite.Lifetime, _instanceSource is not null, out _);
            var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

            // One table for the group, shared by the instance source below and by whatever the
            // strategy factories close over.
            var receivers = new ReceiverTable(options.MaxTransferredStateBytes);

            var request = WorkerRunPlan.WithStrategies(new RunGroupPayload
            {
                GroupId = $"{tfm}:{qualifiedClassName}",
                Kind = WorkGroupKind.DiscoveredClass,

                // The class under test lives in the build for this framework, not in the assembly
                // this coordinator was loaded from.
                TargetAssemblyPath = build.DllPath!,
                WorkerAssemblyPath = workerPath,
                DeclaringTypeFullName = suite.Type.FullName,
                DisplayPrefix = qualifiedClassName,
                BenchmarkNames = displayNames,

                // One worker per (runtime x class), so this worker measures once. A multi-runtime run
                // spends no replicates: the comparison it exists to make is between frameworks, and a
                // launch count multiplied by the runtime count would be a different, longer run than
                // the one asked for.
                Options = options,
                InstanceSource = _instanceSource?.ToPayload(receivers),
                Order = _cliArgs.RunOrder ?? _runOrder,
                Seed = _cliArgs.Seed,
                DefaultInstanceLifetime = _defaultInstanceLifetime,
                InstanceLifetimeOverride = lifetime,
                TotalBenchmarks = displayNames.Count,
            }, options, receivers);

            var group = await WorkerLauncher.Current.RunGroupAsync(
                    request, progress, observer, MeasurementBudget.For(options, displayNames.Count),
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

                    // Not folded into IsolationStatus: the row *was* isolated, and claiming otherwise
                    // would misreport where it ran to fix what it was scored with. Those are separate
                    // facts, so the downgrade rides as a warning instead.
                    Warnings = strategyDowngrade is null
                        ? result.Warnings
                        : [.. result.Warnings, $"Scored with the built-in strategy rather than the one "
                                               + $"configured: {strategyDowngrade}"],
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
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        if (suite.Lifetime == InstanceLifetime.PerClass)
        {
            await RunPerClassInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, progress, observer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunPerMethodInProcessAsync(suite, suiteOptions, startIndex, totalBenchmarks,
                allResults, rawSamples, progress, observer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Measures a whole class in this process, one instance shared by its methods.
    /// </summary>
    /// <remarks>
    ///     The instance is built <i>inside</i> the launch loop. It used to be built once and reused
    ///     by every launch, which quietly emptied the number the launch count exists to produce:
    ///     <see cref="LaunchAggregator" /> derives the reported standard error and margin of error
    ///     from the spread <i>between</i> launches, and three launches sharing one instance, one DI
    ///     scope and one <c>[BenchmarkSetup]</c> are not three independent measurements of anything.
    ///     Rebuilding also settles the reset question at the launch boundary by construction: there
    ///     is no state to carry across, so there is nothing for <see cref="IStateReset" /> to be
    ///     asked to do there.
    /// </remarks>
    private async Task RunPerClassInProcessAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var effectiveClassLaunchCount = EffectiveLaunchCount;
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

        // Clamped, because an attribute argument is a compile-time constant nothing else validates -
        // the fluent builders and the CLI parser reject an out-of-range count, and this is the one
        // path where a typo could ask for ten thousand launches.
        foreach (var b in suite.Benchmarks)
        {
            if (b.Attribute.HasLaunchCountOverride)
                effectiveClassLaunchCount = Math.Max(effectiveClassLaunchCount, LaunchCounts.Clamp(b.Attribute.LaunchCount));
        }

        var allLaunchResults = new List<IReadOnlyList<BenchmarkResult>>(effectiveClassLaunchCount);
        var allLaunchSamples = new List<Dictionary<string, double[]>>(effectiveClassLaunchCount);
        var dependenceWarning = InstanceIndependence.DependenceWarning(
            suite.Type, suite.Lifetime, suite.Benchmarks.Count, suiteOptions);

        for (var launchIdx = 0; launchIdx < effectiveClassLaunchCount; launchIdx++)
        {
            // Later launches measure the same benchmarks again, so their lifecycle events are
            // dropped - forwarding them would make a progress bar run backwards.
            var launchProgress = launchIdx == 0 ? progress : NullBenchmarkProgress.Instance;
            var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;
            var lastLaunch = launchIdx == effectiveClassLaunchCount - 1;

            var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceSource?.Resolve, out var failure);

            if (created is null)
            {
                // Errored rows rather than a silent return. Dropping them shrank the table instead of
                // reporting a failure, so a class that could not be constructed simply went missing from
                // every reporter - while the isolated path synthesises rows for exactly this case
                // (WorkerGroupRunner.ToErroredResults). A shorter table is the one failure shape a reader
                // has no way to notice.
                allResults.AddRange(InstantiationFailures(suite, suiteOptions, failure));

                return;
            }

            var (instance, instanceTeardown) = created.Value;
            var instanceFromFactory = _instanceSource is not null;

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
                    .Select(b => BenchmarkEnvelope.FromDiscovered(b, qualifiedClassName, factory))
                    .ToList();

                // When the class implements IStateReset, fire ResetAsync between benchmark methods
                // to keep the shared instance's state clean across PerClass execution. Null otherwise.
                Func<Task>? betweenBenchmarksReset = InstanceIndependence.ResetsItself(suite.Type)
                    ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                    : null;

                var (results, samples) = await SuiteRunner.RunAsync(
                    envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                    startIndex, totalBenchmarks, launchProgress, cancellationToken,
                    betweenBenchmarksReset, launchObserver).ConfigureAwait(false);

                allLaunchResults.Add(results);
                allLaunchSamples.Add(samples);
            }
            finally
            {
                // Once for the class, not once per launch: the callback is named for the suite and
                // the launches are repetitions of it.
                await BenchmarkLifecycle.RunTeardown(
                    suite, instance, instanceFromFactory, instanceTeardown,
                    lastLaunch ? PostSuiteCleanup : null);
            }
        }

        if (allLaunchResults.Count == 0)
            return;

        if (allLaunchResults.Count > 1)
        {
            var aggregated = AggregateInProcessLaunches(allLaunchResults, allLaunchSamples);
            InstanceIndependence.Attach(aggregated.Results, dependenceWarning);
            allResults.AddRange(aggregated.Results);

            foreach (var kvp in aggregated.Samples)
            {
                rawSamples[kvp.Key] = kvp.Value;
            }

            return;
        }

        var single = allLaunchResults[0].ToList();
        InstanceIndependence.Attach(single, dependenceWarning);
        allResults.AddRange(single);

        foreach (var kvp in allLaunchSamples[0])
        {
            rawSamples[kvp.Key] = kvp.Value;
        }
    }

    private async Task RunPerMethodInProcessAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);
        var orderedBenchmarks = OrderBenchmarksForRun(suite.Benchmarks, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed);

        foreach (var benchmark in orderedBenchmarks)
        {
            var perMethodLaunchCount = benchmark.Attribute.HasLaunchCountOverride
                ? LaunchCounts.Clamp(benchmark.Attribute.LaunchCount)
                : EffectiveLaunchCount;

            var perLaunchResults = new List<BenchmarkResult>();
            var perLaunchSamples = new List<Dictionary<string, double[]>>();
            var faulted = false;

            // Inside the launch loop, for the reason given on RunPerClassInProcessAsync: a launch
            // count buys a between-launch spread, and launches sharing one instance and one
            // [BenchmarkSetup] do not produce one.
            for (var launchIdx = 0; launchIdx < perMethodLaunchCount && !faulted; launchIdx++)
            {
                // Suppressed for every launch after the first, so a progress bar does not run
                // backwards over benchmarks it has already reported.
                var launchProgress = launchIdx == 0 ? progress : NullBenchmarkProgress.Instance;
                var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;

                var created = BenchmarkLifecycle.CreateInstance(suite.Type, _instanceSource?.Resolve, out var failure);

                if (created is null)
                {
                    allResults.Add(InstantiationFailure(suite, benchmark, suiteOptions, failure));
                    faulted = true;

                    break;
                }

                var (instance, instanceTeardown) = created.Value;
                var instanceFromFactory = _instanceSource is not null;

                try
                {
                    var singleBenchmarkSuite = suite with { Benchmarks = [benchmark] };
                    var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(singleBenchmarkSuite, instance, suiteOptions);

                    if (!setupSuccess)
                    {
                        allResults.AddRange(setupErrors!);
                        faulted = true;

                        continue;
                    }

                    var factory = () => instance;
                    var envelope = BenchmarkEnvelope.FromDiscovered(benchmark, qualifiedClassName, factory);

                    var (results, samples) = await SuiteRunner.RunAsync(
                        [envelope], _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                        startIndex, totalBenchmarks, launchProgress, cancellationToken, null, launchObserver).ConfigureAwait(false);

                    perLaunchResults.AddRange(results);
                    perLaunchSamples.Add(samples);
                }
                finally
                {
                    await BenchmarkLifecycle.RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, null);
                }
            }

            if (faulted || perLaunchResults.Count == 0)
            {
                startIndex++;

                continue;
            }

            if (perLaunchResults.Count > 1)
            {
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
                allResults.AddRange(perLaunchResults);

                foreach (var kvp in perLaunchSamples[0])
                {
                    rawSamples[kvp.Key] = kvp.Value;
                }
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
    ///     Which process measures a single benchmark, layering the global in-process switch over the
    ///     isolation intent its attributes declare.
    /// </summary>
    /// <remarks>
    ///     Granularity only. How long the instance lives is <see cref="InstanceIndependence" />'s
    ///     question and is answered separately, because deciding both here meant the first answer
    ///     swallowed the second: <c>--in-process</c> returned before the lifetime rule ran, so the
    ///     rule was unreachable for every run that could not isolate - which is precisely the run
    ///     where a shared instance is measured in a dirty host and nothing at all is said about it.
    /// </remarks>
    private static IsolationDecision ResolveGranularity(
        BenchmarkMethodDefinition benchmark,
        bool inProcessGlobal)
    {
        if (inProcessGlobal)
            return IsolationDecision.InProcess;

        if (benchmark.Isolation == IsolationMode.InProcess)
            return IsolationDecision.InProcess;

        return benchmark.Isolation == IsolationMode.PerBenchmark
            ? IsolationDecision.PerBenchmark
            : IsolationDecision.PerClass;
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
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var effectiveLaunchCount = EffectiveLaunchCount;

        // Check per-benchmark attribute overrides; take the maximum so all
        // benchmarks in the group get enough launches for their overrides.
        foreach (var b in benchmarks)
        {
            if (b.Attribute.HasLaunchCountOverride)
                effectiveLaunchCount = Math.Max(effectiveLaunchCount, LaunchCounts.Clamp(b.Attribute.LaunchCount));
        }

        var options = ChildLaunchOptions;
        var names = benchmarks.Select(b => b.DisplayName).ToList();
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);
        var timeout = MeasurementBudget.For(options, benchmarks.Count);

        // Each replicate is its own worker, so LaunchCount buys a between-process variance estimate
        // rather than repeated measurements inside one process. That is the reproducibility number a
        // regression gate needs; the within-process interval only describes precision, and the data
        // shows how misleading it is alone - a standard deviation of 0.16 ns on an 11 ns reading
        // while the true run-to-run spread was 3.27x.
        var allLaunchItems = new List<IReadOnlyList<IsolatedResultItem>>(effectiveLaunchCount);

        for (var replicate = 0; replicate < effectiveLaunchCount; replicate++)
        {
            // Per replicate, because each is its own worker and its own request - and the table is
            // what that request carries.
            var receivers = new ReceiverTable(options.MaxTransferredStateBytes);

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
                _instanceSource?.ToPayload(receivers),

                // The lifetime this class resolved to on the coordinator, sent so the worker measures
                // the same object graph the in-process path would have. Without it a class carrying
                // [InstanceLifetime(PerClass)] keeps its attribute in the worker and the resolution
                // applies to exactly the half of the run that does not need it.
                suite.Lifetime,
                receivers);

            allLaunchItems.Add(
                await RunGroupInWorkerAsync(request, replicate, names, suite, timeout, progress, observer, cancellationToken)
                    .ConfigureAwait(false));
        }

        var items = effectiveLaunchCount > 1
            ? HostAggregateIsolatedLaunches(allLaunchItems, benchmarks, suite)
            : allLaunchItems[0];

        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

        foreach (var benchmark in benchmarks)
        {
            var name = BenchmarkEnvelope.QualifiedDiscoveredBenchmarkName(suite.Type, benchmark.DisplayName);

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
                    name, qualifiedClassName, benchmark.Attribute.Description, benchmark.IsBaseline,
                    _options, TimeSpan.Zero, TimeSpan.Zero, 0, null,
                    benchmark.Categories).Result;

                raw = [];
            }

            allResults.Add(result);
            rawSamples[name] = raw;

            await progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
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
    ///     <para>
    ///         The mirror case is stamped too. A benchmark carrying <c>[IsolatedProcess]</c> that ends up
    ///         here asked for a worker and was denied one, which the status alone cannot say - it reads
    ///         identically to a benchmark that never asked - so the row carries a warning naming the
    ///         denied request.
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

        var explicitlyIsolated = benchmarks
            .Where(b => b.Isolation == IsolationMode.PerBenchmark)
            .Select(b => b.DisplayName)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = startIndex; i < results.Count; i++)
        {
            var result = results[i];
            var methodName = MethodNameOf(result.Name);

            var status = explicitlyInProcess.Contains(methodName)
                ? IsolationStatus.InProcessRequested
                : classStatus;

            if (status == IsolationStatus.Isolated)
                status = IsolationStatus.InProcessRequested;

            var warnings = status.IsRefusal() && explicitlyIsolated.Contains(methodName)
                ? (IReadOnlyList<string>)
                [
                    .. result.Warnings,
                    $"[IsolatedProcess] was requested for '{methodName}' and refused: "
                    + $"{status.ToLabel()}. The measurement ran in this process anyway.",
                ]
                : result.Warnings;

            results[i] = result with { IsolationStatus = status, Warnings = warnings };
        }
    }

    /// <summary>
    ///     One errored row per benchmark in a class that could not be constructed, so the failure
    ///     appears in the table rather than shortening it.
    /// </summary>
    private static IEnumerable<BenchmarkResult> InstantiationFailures(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        string? failure)
        => suite.Benchmarks.Select(b => InstantiationFailure(suite, b, suiteOptions, failure));

    /// <inheritdoc cref="InstantiationFailures" />
    private static BenchmarkResult InstantiationFailure(
        BenchmarkSuiteDefinition suite,
        BenchmarkMethodDefinition benchmark,
        MeasurementOptions suiteOptions,
        string? failure)
    {
        var message = failure ?? $"Could not instantiate {suite.Type.Name}.";
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

        return OutcomeBuilder.Build(
            new RunOutcome.Errored(new InvalidOperationException(message), message),
            BenchmarkEnvelope.QualifiedDiscoveredBenchmarkName(suite.Type, benchmark.DisplayName),
            qualifiedClassName,
            benchmark.Attribute.Description,
            benchmark.IsBaseline,
            suiteOptions,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            null,
            benchmark.Categories).Result;
    }

    /// <summary>Strips the class prefix from a <c>Class.Method</c> result name.</summary>
    private static string MethodNameOf(string resultName)
    {
        var separator = resultName.LastIndexOf('.');

        return separator < 0 ? resultName : resultName[(separator + 1)..];
    }

    /// <summary>
    ///     Answers "can a worker measure this?" for every discovered class at once, before the first
    ///     benchmark runs - reporting every refusal in one message, and failing the run here when
    ///     isolation is required.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hoisted out of the run loop, where the same call was made per class immediately before
    ///         that class launched. Every input except the assembly path is run-global - worker
    ///         availability, the instance source, the resolved options - so a per-class question was
    ///         being asked N times to get at most one distinct answer per assembly, and asking it late
    ///         meant class N's refusal was discovered after classes 1..N-1 had already been measured.
    ///         Under a hard error that is the difference between failing in a second and failing after
    ///         the run.
    ///     </para>
    ///     <para>
    ///         The refusal is reported per class rather than per assembly even though the decision is
    ///         per assembly: the reader is looking for the name they wrote, and "your assembly cannot be
    ///         isolated" is not something they can act on.
    ///     </para>
    /// </remarks>
    private Dictionary<Type, WorkerRunPlan.Decision> ResolveIsolationPlan(
        IReadOnlyList<BenchmarkSuiteDefinition> filtered,
        bool inProcessGlobal,
        MeasurementOptions suiteOptions)
    {
        var plan = new Dictionary<Type, WorkerRunPlan.Decision>();

        if (inProcessGlobal)
        {
            var requested = new WorkerRunPlan.Decision(WorkerRunPlan.Refusal.RequestedInProcess, null);

            foreach (var suite in filtered)
            {
                plan[suite.Type] = requested;
            }

            return plan;
        }

        // One answer per assembly, reused by every class declared in it. The path is the only per-class
        // input the decision has, and in the overwhelmingly common single-assembly run that makes this
        // a single evaluation for the whole set.
        var byAssembly = new Dictionary<string, WorkerRunPlan.Decision>(StringComparer.Ordinal);
        var refusals = new List<IsolationRefusal>();

        foreach (var suite in filtered)
        {
            var location = suite.Type.Assembly.Location;

            if (!byAssembly.TryGetValue(location, out var decision))
            {
                decision = WorkerRunPlan.ForDiscoveredClass(location, _instanceSource, suiteOptions);
                byAssembly[location] = decision;
            }

            plan[suite.Type] = decision;

            if (decision.CanIsolate)
                continue;

            EmitIsolationRefusal(suite, decision.Explanation);
            refusals.Add(new IsolationRefusal(suite.Type.Name, decision.Status, decision.Explanation));
        }

        // Every refusal in the run, in one exception, before anything has been measured.
        IsolationAudit.ThrowIfRequired(suiteOptions, refusals);

        return plan;
    }

    /// <summary>
    ///     Reports that isolation was declined for one class, and why.
    ///     <para>
    ///         This is the visible half of "refuse rather than guess". A silent fallback to
    ///         in-process would be the worst outcome available: on bodies of provably identical cost,
    ///         in-process runs spanned 3.27x and fabricated a 2.80x difference between two of them,
    ///         each reported with a tight confidence interval. The results themselves are also
    ///         stamped <c>host</c>, so the provenance survives even if this message is scrolled past.
    ///     </para>
    /// </summary>
    private static void EmitIsolationRefusal(BenchmarkSuiteDefinition suite, string? explanation)
    {
        Console.Error.WriteLine(
            $"Isolation: '{suite.Type.Name}' is being measured in this process because "
            + (explanation ?? "it could not be addressed across a process boundary."));

        // An explicit [IsolatedProcess] being denied is strictly more interesting than a default being
        // denied - the user said what they wanted and is not getting it - and used to be indis-
        // tinguishable from it in both the message and the row label.
        var explicitlyRequested = ExplicitIsolationRequests(suite.Benchmarks);

        if (explicitlyRequested.Count > 0)
        {
            Console.Error.WriteLine(
                $"  {string.Join(", ", explicitlyRequested)} asked for this explicitly with "
                + "[IsolatedProcess], so the request is being denied rather than defaulted away.");
        }

        Console.Error.WriteLine(
            "  In-process measurements cannot control JIT tiering, PGO, ReadyToRun or GC flavour, "
            + "because the runtime fixes those at startup. They are stamped 'host' and are never "
            + "compared against isolated results.");
    }

    /// <summary>
    ///     The benchmarks that carry <c>[IsolatedProcess]</c>, i.e. asked for a worker by name rather
    ///     than getting one by default.
    /// </summary>
    private static IReadOnlyList<string> ExplicitIsolationRequests(
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks)
        => benchmarks
            .Where(b => b.Isolation == IsolationMode.PerBenchmark)
            .Select(b => $"'{b.DisplayName}'")
            .ToList();

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
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var isFirstReplicate = replicate == 0;

        var group = await WorkerLauncher.Current.RunGroupAsync(
                request,
                isFirstReplicate ? progress : NullBenchmarkProgress.Instance,
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
        foreach (var errored in WorkerGroupRunner.ToErroredResults(
                     group,
                     benchmarkNames,
                     BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type)))
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
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

        foreach (var benchmark in benchmarks)
        {
            var name = BenchmarkEnvelope.QualifiedDiscoveredBenchmarkName(suite.Type, benchmark.DisplayName);
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
                        name, qualifiedClassName, benchmark.Attribute.Description, benchmark.IsBaseline,
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
    ///     Attaches a class-level warning to every row this class produced in the current run,
    ///     whichever path measured it.
    /// </summary>
    /// <remarks>
    ///     Keyed by class name over the run's own slice rather than by benchmark name, because the
    ///     fact being reported is about the class - and a per-name set could not describe a class
    ///     whose rows were split across the in-process, per-class and per-benchmark paths without
    ///     enumerating them three times.
    /// </remarks>
    private static void ApplyClassWarning(
        List<BenchmarkResult> results,
        int startIndex,
        string className,
        string warning)
    {
        for (var i = startIndex; i < results.Count; i++)
        {
            if (!string.Equals(results[i].ClassName, className, StringComparison.Ordinal))
                continue;

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
        var allSuites = DiscoverOnce();

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
