using System.Diagnostics;
using System.Globalization;
using NBenchmark.Observers;
using NBenchmark.Reporters;

namespace NBenchmark.Engine;

internal sealed record CliArgs
{
    public bool ShowHelp { get; init; }
    public bool ListOnly { get; init; }
    public bool DryRun { get; init; }
    public int? ThresholdPct { get; init; }
    public string? Filter { get; init; }
    public string? OutputDir { get; init; }
    public int? Seed { get; init; }
    public RunOrder? RunOrder { get; init; }
    public int? Iterations { get; init; }
    public int? WarmupIterations { get; init; }
    public double? ConfidenceLevel { get; init; }
    public double? Alpha { get; init; }
    public OutlierMode? OutlierMode { get; init; }
    public TailMetricsBasis? TailMetricsBasis { get; init; }
    public IReadOnlyList<string> ReporterNames { get; init; } = [];
    public IReadOnlyList<string> ObserverNames { get; init; } = [];
    public IReadOnlyList<string> CategoryFilterInclude { get; init; } = [];
    public IReadOnlyList<string> CategoryFilterExclude { get; init; } = [];
    public ReportDetail Detail { get; init; } = ReportDetail.Simple;

    /// <summary>
    ///     When true, every benchmark runs in the host process, overriding Harness mode's
    ///     isolated-by-default execution and any <c>[IsolatedProcess]</c> attributes.
    /// </summary>
    public bool InProcess { get; init; }

    /// <summary>
    ///     When true, any benchmark that was <b>not</b> measured in an isolated worker fails the
    ///     run.
    ///     <para>
    ///         For CI. Without it, a machine missing the worker, or a benchmark whose body captures
    ///         state, silently produces host-process numbers - correctly labelled, but a label in
    ///         scrollback is not a gate. This turns the label into an exit code.
    ///     </para>
    /// </summary>
    public bool StrictIsolation { get; init; }

    /// <summary>
    ///     When true, measures everything a second time in the host process and prints the
    ///     per-benchmark difference.
    ///     <para>
    ///         This exists because the case for isolation is not believable in the abstract. On this
    ///         library's own sample, in-process measurement of the same body reported 7,009 ns and
    ///         320 ns on consecutive attempts - a 21x error with a tight confidence interval on
    ///         each. Reading that in a changelog persuades nobody; seeing it on your own benchmarks
    ///         does.
    ///     </para>
    /// </summary>
    public bool VerifyIsolation { get; init; }

    /// <summary>
    ///     When true, significance is computed across all classes in a single comparison
    ///     table instead of per class. The baseline is chosen from the whole group.
    /// </summary>
    public bool CrossClass { get; init; }

    public MeasurementProfile? Profile { get; init; }

    /// <summary>
    ///     The runtime-startup configuration requested via <c>--runtime-profile</c>. Applied to
    ///     isolated children through their environment block; in-process benchmarks inherit the
    ///     host's configuration and report <c>"host"</c>.
    /// </summary>
    public RuntimeProfile? RuntimeProfile { get; init; }

    public bool? ForceGc { get; init; }

    public bool? NoAllocations { get; init; }

    /// <summary>
    ///     When true, disables the full GC that otherwise runs between benchmarks under both
    ///     profiles (maps to <see cref="MeasurementOptions.ForceGcBetweenBenchmarksOverride" /> =
    ///     <c>false</c>). Use when the inter-benchmark heap carry-over is intended.
    /// </summary>
    public bool NoGcBetweenBenchmarks { get; init; }

    /// <summary>
    ///     The minimum practical effect [0, 1] a change must reach to keep a significant verdict.
    ///     <c>null</c> uses the <see cref="MeasurementOptions" /> default (0.147); <c>0</c> restores
    ///     p-value-only verdicts.
    /// </summary>
    public double? MinPracticalEffect { get; init; }

    /// <summary>
    ///     The minimum relative median shift [0, 1] a change must reach to keep a significant
    ///     verdict, gated alongside <see cref="MinPracticalEffect" />. <c>null</c> uses the
    ///     <see cref="MeasurementOptions" /> default (0.01); <c>0</c> disables the relative-shift
    ///     gate.
    /// </summary>
    public double? MinRelativeShift { get; init; }

    public int? OpsPerSample { get; init; }

    public AutoTunePreset? AutoTunePreset { get; init; }

    public double? CiTarget { get; init; }

    public int? MinSamples { get; init; }

    public int? MaxSamples { get; init; }

    public int? MinWarmup { get; init; }

    public int? MaxWarmup { get; init; }

    public TimeSpan? MaxTuningTime { get; init; }

    public AutoTuneCapBehavior? AutoTuneCapBehavior { get; init; }

    /// <summary>
    ///     The maximum share of <c>--max-tuning-time</c> that ops-per-sample calibration and
    ///     warmup may consume together. <c>null</c> uses the <see cref="AutoTuneOptions" />
    ///     default (0.4). Must be a fraction strictly greater than 0 and at most 1.
    /// </summary>
    public double? WarmupBudgetFraction { get; init; }

    /// <summary>
    ///     The hard ceiling multiplier the measurement phase may reach while chasing
    ///     <c>--min-samples</c> after the wall-clock cap fires. <c>null</c> uses the
    ///     <see cref="AutoTuneOptions" /> default (1.5). Must be at least 1.
    /// </summary>
    public double? CapGraceFactor { get; init; }

    /// <summary>
    ///     The minimum in-body time auto-warmup must run before it may settle. <c>null</c> uses
    ///     the <see cref="AutoTuneOptions" /> default (250 ms). Parsed from a millisecond value.
    /// </summary>
    public TimeSpan? MinWarmupTime { get; init; }

    /// <summary>
    ///     When true, disables the JIT-quiescence warmup gate (maps to
    ///     <see cref="AutoTuneOptions.RequireJitQuiescence" /> = <c>false</c>). The warmup time floor
    ///     still applies.
    /// </summary>
    public bool NoJitQuiescence { get; init; }

    /// <summary>
    ///     How long the JIT compiled-method count must stay unchanged before the quiescence gate lets
    ///     warmup settle. <c>null</c> uses the <see cref="AutoTuneOptions" /> default (50 ms). Parsed
    ///     from a millisecond value; <c>0</c> disables the gate.
    /// </summary>
    public TimeSpan? JitQuietPeriod { get; init; }

    /// <summary>
    ///     The minimum in-body time the measurement phase must span before it may stop on the CI
    ///     target. <c>null</c> uses the <see cref="AutoTuneOptions" /> default (100 ms). Parsed from a
    ///     millisecond value; <c>0</c> disables the floor.
    /// </summary>
    public TimeSpan? MinMeasurementTime { get; init; }

    /// <summary>
    ///     How far the first and second halves of the measured samples may disagree before the loop
    ///     refuses to stop on the CI target. <c>null</c> uses the <see cref="AutoTuneOptions" /> default
    ///     (0.1); <c>0</c> disables the gate.
    /// </summary>
    public double? DriftTolerance { get; init; }

    /// <summary>
    ///     How many times the drift gate may discard the collected samples and restart measurement.
    ///     <c>null</c> uses the <see cref="AutoTuneOptions" /> default (2); <c>0</c> reports
    ///     <see cref="SampleStopReason.DriftUnresolved" /> on the first detected drift instead.
    /// </summary>
    public int? MaxDriftRestarts { get; init; }

    /// <summary>Number of separate launches for each benchmark (1 = default, single-run behavior).</summary>
    public int? LaunchCount { get; init; }

    /// <summary>
    ///     Comma-separated list of percentile values to report (e.g. "0.50,0.95,0.99,0.999,1.0").
    ///     <c>null</c> uses the MeasurementOptions default.
    /// </summary>
    public IReadOnlyList<double>? ReportedPercentiles { get; init; }

    /// <summary>
    ///     When true, disables the latency histogram.
    /// </summary>
    public bool NoHistogram { get; init; }

    /// <summary>
    ///     When true, disables the host drift canary - no control readings are taken between
    ///     benchmarks, and no host-drift warning can fire.
    /// </summary>
    public bool NoDriftCanary { get; init; }

    /// <summary>
    ///     When true, disables the thread-level OS controls - the measuring thread keeps the
    ///     host's default affinity, priority and (on macOS) quality-of-service class.
    /// </summary>
    public bool NoThreadControl { get; init; }

    /// <summary>
    ///     When true, omits raw per-sample arrays from reporter output (JSON).
    ///     Samples are still collected for significance and the Console histogram;
    ///     this only controls whether they are serialized to file.
    /// </summary>
    public bool NoSamples { get; init; }

    /// <summary>
    ///     When true, an isolated worker sends back every raw sample it measured instead of a bounded
    ///     representative subset.
    /// </summary>
    /// <remarks>
    ///     Off by default because a worker can measure up to
    ///     <see cref="MeasurementOptions.MaxIterations" /> samples, and the coordinator only uses what
    ///     crosses for significance testing and the Console sparkline - both distribution properties,
    ///     which a few thousand samples describe as well as a hundred thousand. Turn it on to export
    ///     the full series for external analysis; it does not change any statistic NBenchmark itself
    ///     reports, because the worker computes those over the whole array either way.
    /// </remarks>
    public bool EmitRaw { get; init; }

    /// <summary>
    ///     When true, an isolated worker forwards its live per-sample observer stream back to the
    ///     coordinator, set by <c>--stream-samples</c>.
    /// </summary>
    /// <remarks>
    ///     Off by default because the volume scales with how fast the benchmarked code is - see
    ///     <see cref="MeasurementOptions.StreamSamples" />. Has no effect without an attached
    ///     observer, and none at all in-process, where the observer is called directly.
    /// </remarks>
    public bool StreamSamples { get; init; }

    /// <summary>
    ///     Diagnostics mode controlling which runtime counters are collected.
    ///     <c>null</c> uses the MeasurementOptions default (GC counts on).
    /// </summary>
    public DiagnosticsMode? Diagnostics { get; init; }

    /// <summary>
    ///     Comma-separated list of logical CPU cores to pin the benchmark process to
    ///     (e.g. "0" or "2,3"). <c>null</c> leaves affinity untouched. Parsed into
    ///     <see cref="EnvironmentOptions.CpuAffinity" /> by the harness.
    /// </summary>
    public IReadOnlyList<int>? CpuAffinity { get; init; }

    /// <summary>
    ///     The process priority to request for the benchmark run. <c>null</c> leaves
    ///     priority untouched. Mapped into <see cref="EnvironmentOptions.ProcessPriority" />
    ///     by the harness.
    /// </summary>
    public ProcessPriorityClass? ProcessPriority { get; init; }

    /// <summary>
    ///     When true, emits a non-fatal pre-run warning when the host looks like a shared
    ///     or noisy benchmark environment (low core count, unraisable priority, or on
    ///     macOS unobservable frequency scaling/thermal throttling). Mapped into
    ///     <see cref="EnvironmentOptions.DedicatedHostGuidance" />.
    /// </summary>
    public bool DedicatedHostGuidance { get; init; }

    /// <summary>
    ///     Target framework monikers to benchmark under. When non-empty, the harness builds
    ///     and runs the benchmark project under each specified runtime and aggregates
    ///     results for cross-runtime comparison.
    /// </summary>
    public IReadOnlyList<RuntimeMoniker> Runtimes { get; init; } = [];

    /// <summary>
    ///     The OTLP endpoint an OpenTelemetry SDK in the entry assembly should export to. When
    ///     set, the harness mirrors this into the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment
    ///     variable before spawning isolated children, so children stream to the same collector
    ///     as the parent. <c>null</c> leaves the env var untouched (the SDK uses its own default
    ///     or whatever the user already configured).
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    ///     Pure parse: tokenises <paramref name="args" />, validates ranges, and returns
    ///     both the structured result and any error messages. No console I/O, no
    ///     <c>Environment.ExitCode</c> mutation.
    /// </summary>
    public static (CliArgs Args, IReadOnlyList<string> Errors) ParseCore(string[] args)
    {
        var showHelp = false;
        var listOnly = false;
        var dryRun = false;
        int? thresholdPct = null;
        string? filter = null;
        string? outputDir = null;
        int? seed = null;
        RunOrder? runOrder = null;
        int? iterations = null;
        int? warmupIterations = null;
        double? confidenceLevel = null;
        var reporterNames = new List<string>();
        var observerNames = new List<string>();
        var categoryInclude = new List<string>();
        var categoryExclude = new List<string>();
        var detail = ReportDetail.Simple;

        double? alpha = null;
        var inProcess = false;
        var strictIsolation = false;
        var verifyIsolation = false;
        OutlierMode? outlierMode = null;
        TailMetricsBasis? tailMetricsBasis = null;
        MeasurementProfile? profile = null;
        RuntimeProfile? runtimeProfile = null;
        bool? forceGc = null;
        bool? noAllocations = null;
        var noGcBetweenBenchmarks = false;
        double? minPracticalEffect = null;
        double? minRelativeShift = null;
        DiagnosticsMode? diagnostics = null;
        int? opsPerSample = null;
        AutoTunePreset? autoTunePreset = null;
        double? ciTarget = null;
        int? minSamples = null;
        int? maxSamples = null;
        int? minWarmup = null;
        int? maxWarmup = null;
        TimeSpan? maxTuningTime = null;
        AutoTuneCapBehavior? autoTuneCapBehavior = null;
        double? warmupBudgetFraction = null;
        double? capGraceFactor = null;
        TimeSpan? minWarmupTime = null;
        var noJitQuiescence = false;
        TimeSpan? jitQuietPeriod = null;
        TimeSpan? minMeasurementTime = null;
        double? driftTolerance = null;
        int? maxDriftRestarts = null;
        int? launchCount = null;
        IReadOnlyList<double>? reportedPercentiles = null;
        var noHistogram = false;
        var noDriftCanary = false;
        var noThreadControl = false;
        var noSamples = false;
        var emitRaw = false;
        var streamSamples = false;
        var runtimes = new List<RuntimeMoniker>();
        IReadOnlyList<int>? cpuAffinity = null;
        ProcessPriorityClass? processPriority = null;
        var crossClass = false;
        var dedicatedHostGuidance = false;
        string? otlpEndpoint = null;

        var errors = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var iters)
                        && iters >= MeasurementOptions.MinIterations
                        && iters <= MeasurementOptions.MaxIterations)
                        iterations = iters;
                    else
                        errors.Add($"Invalid --iterations value '{args[i]}'. Must be {MeasurementOptions.MinIterations}–{MeasurementOptions.MaxIterations}.");

                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var warmup)
                        && warmup >= 0
                        && warmup <= MeasurementOptions.MaxWarmupIterations)
                        warmupIterations = warmup;
                    else
                        errors.Add($"Invalid --warmup value '{args[i]}'. Must be 0–{MeasurementOptions.MaxWarmupIterations}.");

                    break;
                case "--output" when i + 1 < args.Length:
                    outputDir = PathValidation.ValidateOutputPath(args[++i]);
                    break;
                case "--category" when i + 1 < args.Length:
                    AddCategory(args[++i], "--category", categoryInclude, errors);
                    break;
                case "--exclude-category" when i + 1 < args.Length:
                    AddCategory(args[++i], "--exclude-category", categoryExclude, errors);
                    break;
                case "--reporter" when i + 1 < args.Length:
                    reporterNames.Add(args[++i]);
                    break;
                case "--observer" when i + 1 < args.Length:
                    observerNames.Add(args[++i]);
                    break;
                case "--confidence" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var conf)
                        && conf is > 0 and < 1)
                        confidenceLevel = conf;
                    else
                        errors.Add($"Invalid --confidence value '{args[i]}'. Must be a fraction strictly between 0 and 1 (e.g. 0.95).");

                    break;
                case "--alpha" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var a)
                        && a is > 0 and < 1)
                        alpha = a;
                    else
                        errors.Add($"Invalid --alpha value '{args[i]}'. Must be a fraction strictly between 0 and 1 (e.g. 0.05).");

                    break;
                case "--outlier" when i + 1 < args.Length:
                    var outlierStr = args[++i];

                    if (TryParseOutlierMode(outlierStr, out var parsedOutlier))
                        outlierMode = parsedOutlier;
                    else
                        errors.Add($"Invalid --outlier value '{outlierStr}'. Must be one of: none, top5, both5, iqr, mad.");

                    break;
                case "--tail-basis" when i + 1 < args.Length:
                    var tailStr = args[++i];

                    if (string.Equals(tailStr, "raw", StringComparison.OrdinalIgnoreCase))
                        tailMetricsBasis = NBenchmark.TailMetricsBasis.Raw;
                    else if (string.Equals(tailStr, "trimmed", StringComparison.OrdinalIgnoreCase))
                        tailMetricsBasis = NBenchmark.TailMetricsBasis.Trimmed;
                    else
                        errors.Add($"Invalid --tail-basis value '{tailStr}'. Must be one of: raw, trimmed.");

                    break;
                case "--order" when i + 1 < args.Length:
                    var order = args[++i];

                    if (string.Equals(order, "declaration", StringComparison.OrdinalIgnoreCase))
                        runOrder = NBenchmark.RunOrder.Declaration;
                    else if (string.Equals(order, "random", StringComparison.OrdinalIgnoreCase))
                        runOrder = NBenchmark.RunOrder.Random;
                    else
                        errors.Add($"Invalid --order value '{order}'. Must be 'random' or 'declaration'.");

                    break;
                case "--threshold-pct" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var tPct)
                        && tPct > 0)
                        thresholdPct = tPct;
                    else
                        errors.Add($"Invalid --threshold-pct value '{args[i]}'. Must be a positive integer (1 or greater).");

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var seedVal))
                        seed = seedVal;
                    else
                        errors.Add($"Invalid --seed value '{args[i]}'. Must be an integer.");

                    break;
                case "--detail" when i + 1 < args.Length:
                    var detailStr = args[++i];

                    if (string.Equals(detailStr, "simple", StringComparison.OrdinalIgnoreCase))
                        detail = ReportDetail.Simple;
                    else if (string.Equals(detailStr, "standard", StringComparison.OrdinalIgnoreCase))
                        detail = ReportDetail.Standard;
                    else if (string.Equals(detailStr, "advanced", StringComparison.OrdinalIgnoreCase))
                        detail = ReportDetail.Advanced;
                    else
                        errors.Add($"Invalid --detail value '{detailStr}'. Must be 'simple', 'standard', or 'advanced'.");

                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--in-process":
                    inProcess = true;
                    break;
                case "--strict-isolation":
                    strictIsolation = true;
                    break;
                case "--verify-isolation":
                    verifyIsolation = true;
                    break;
                case "--cross-class":
                    crossClass = true;
                    break;
                case "--profile" when i + 1 < args.Length:
                    var profileStr = args[++i];

                    if (string.Equals(profileStr, "realistic", StringComparison.OrdinalIgnoreCase))
                        profile = MeasurementProfile.Realistic;
                    else if (string.Equals(profileStr, "independent", StringComparison.OrdinalIgnoreCase))
                        profile = MeasurementProfile.Independent;
                    else
                        errors.Add($"Invalid --profile value '{profileStr}'. Must be 'realistic' or 'independent'.");

                    break;
                case "--runtime-profile" when i + 1 < args.Length:
                    var runtimeProfileStr = args[++i];

                    if (RuntimeProfile.TryParse(runtimeProfileStr, out var parsedRuntimeProfile))
                        runtimeProfile = parsedRuntimeProfile;
                    else
                    {
                        errors.Add(
                            $"Invalid --runtime-profile value '{runtimeProfileStr}'. Must be one of: "
                            + $"{string.Join(", ", RuntimeProfile.KnownNames)}.");
                    }

                    break;
                case "--force-gc":
                    forceGc = true;
                    break;
                case "--no-allocations":
                    noAllocations = true;
                    break;
                case "--no-gc-between-benchmarks":
                    noGcBetweenBenchmarks = true;
                    break;
                case "--min-practical-effect" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var mpe) && mpe is >= 0 and <= 1)
                        minPracticalEffect = mpe;
                    else
                        errors.Add($"Invalid --min-practical-effect value '{args[i]}'. Must be a fraction in [0, 1] (0 restores p-value-only verdicts).");

                    break;
                case "--min-relative-shift" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var mrs) && mrs is >= 0 and <= 1)
                        minRelativeShift = mrs;
                    else
                        errors.Add($"Invalid --min-relative-shift value '{args[i]}'. Must be a fraction in [0, 1] (0 disables the relative-shift gate).");

                    break;
                case "--diagnostics" when i + 1 < args.Length:
                    var diagStr = args[++i];

                    if (string.Equals(diagStr, "none", StringComparison.OrdinalIgnoreCase))
                        diagnostics = DiagnosticsMode.None;
                    else if (string.Equals(diagStr, "gc", StringComparison.OrdinalIgnoreCase))
                        diagnostics = DiagnosticsMode.Gc;
                    else if (string.Equals(diagStr, "gcandcpu", StringComparison.OrdinalIgnoreCase))
                        diagnostics = DiagnosticsMode.GcAndCpu;
                    else if (string.Equals(diagStr, "all", StringComparison.OrdinalIgnoreCase))
                        diagnostics = DiagnosticsMode.All;
                    else
                        errors.Add($"Invalid --diagnostics value '{diagStr}'. Must be 'none', 'gc', 'gcandcpu', or 'all'.");

                    break;
                case "--auto-tune" when i + 1 < args.Length:
                    var presetStr = args[++i];

                    if (string.Equals(presetStr, "default", StringComparison.OrdinalIgnoreCase))
                        autoTunePreset = NBenchmark.AutoTunePreset.Default;
                    else if (string.Equals(presetStr, "quick", StringComparison.OrdinalIgnoreCase))
                        autoTunePreset = NBenchmark.AutoTunePreset.Quick;
                    else if (string.Equals(presetStr, "thorough", StringComparison.OrdinalIgnoreCase))
                        autoTunePreset = NBenchmark.AutoTunePreset.Thorough;
                    else
                        errors.Add($"Invalid --auto-tune value '{presetStr}'. Must be 'default', 'quick', or 'thorough'.");

                    break;
                case "--ops-per-sample" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ops)
                        && ops >= 1
                        && ops <= MeasurementOptions.MaxOpsPerSampleLimit)
                        opsPerSample = ops;
                    else
                        errors.Add($"Invalid --ops-per-sample value '{args[i]}'. Must be 1–{MeasurementOptions.MaxOpsPerSampleLimit}.");

                    break;
                case "--ci-target" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var cit) && cit is > 0 and < 1)
                        ciTarget = cit;
                    else
                        errors.Add($"Invalid --ci-target value '{args[i]}'. Must be a fraction strictly between 0 and 1 (e.g. 0.025).");

                    break;
                case "--min-samples" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var mins) && mins >= 1 && mins <= MeasurementOptions.MaxIterations)
                        minSamples = mins;
                    else
                        errors.Add($"Invalid --min-samples value '{args[i]}'. Must be 1–{MeasurementOptions.MaxIterations}.");

                    break;
                case "--max-samples" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var maxs) && maxs >= 1 && maxs <= MeasurementOptions.MaxIterations)
                        maxSamples = maxs;
                    else
                        errors.Add($"Invalid --max-samples value '{args[i]}'. Must be 1–{MeasurementOptions.MaxIterations}.");

                    break;
                // The auto-warmup bounds use MaxAutoWarmupIterations, not the tighter pinned-warmup
                // limit: a fast body needs tens of thousands of samples to reach MinWarmupTime.
                case "--min-warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var minw) && minw >= 0 && minw <= MeasurementOptions.MaxAutoWarmupIterations)
                        minWarmup = minw;
                    else
                        errors.Add($"Invalid --min-warmup value '{args[i]}'. Must be 0–{MeasurementOptions.MaxAutoWarmupIterations}.");

                    break;
                case "--max-warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var maxw) && maxw >= 1 && maxw <= MeasurementOptions.MaxAutoWarmupIterations)
                        maxWarmup = maxw;
                    else
                        errors.Add($"Invalid --max-warmup value '{args[i]}'. Must be 1–{MeasurementOptions.MaxAutoWarmupIterations}.");

                    break;
                case "--max-tuning-time" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var secs) && secs > 0)
                        maxTuningTime = TimeSpan.FromSeconds(secs);
                    else
                        errors.Add($"Invalid --max-tuning-time value '{args[i]}'. Must be a positive number of seconds.");

                    break;
                case "--autotune-cap-behavior" when i + 1 < args.Length:
                    var capBehaviorStr = args[++i];

                    if (string.Equals(capBehaviorStr, "warn", StringComparison.OrdinalIgnoreCase))
                        autoTuneCapBehavior = NBenchmark.AutoTuneCapBehavior.Warn;
                    else if (string.Equals(capBehaviorStr, "error", StringComparison.OrdinalIgnoreCase))
                        autoTuneCapBehavior = NBenchmark.AutoTuneCapBehavior.Error;
                    else
                        errors.Add($"Invalid --autotune-cap-behavior value '{capBehaviorStr}'. Must be 'warn' or 'error'.");

                    break;
                case "--warmup-budget-fraction" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var wbf) && wbf is > 0 and <= 1)
                        warmupBudgetFraction = wbf;
                    else
                        errors.Add($"Invalid --warmup-budget-fraction value '{args[i]}'. Must be a fraction in (0, 1] (e.g. 0.4).");

                    break;
                case "--cap-grace-factor" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var cgf) && cgf >= 1)
                        capGraceFactor = cgf;
                    else
                        errors.Add($"Invalid --cap-grace-factor value '{args[i]}'. Must be at least 1 (e.g. 1.5).");

                    break;
                case "--min-warmup-time" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var mwt) && mwt >= 0)
                        minWarmupTime = TimeSpan.FromMilliseconds(mwt);
                    else
                        errors.Add($"Invalid --min-warmup-time value '{args[i]}'. Must be a non-negative number of milliseconds (0 disables).");

                    break;
                case "--no-jit-quiescence":
                    noJitQuiescence = true;
                    break;
                case "--jit-quiet-period" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var jqp) && jqp >= 0)
                        jitQuietPeriod = TimeSpan.FromMilliseconds(jqp);
                    else
                        errors.Add($"Invalid --jit-quiet-period value '{args[i]}'. Must be a non-negative number of milliseconds (0 disables the gate).");

                    break;
                case "--min-measurement-time" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var mmt) && mmt >= 0)
                        minMeasurementTime = TimeSpan.FromMilliseconds(mmt);
                    else
                        errors.Add($"Invalid --min-measurement-time value '{args[i]}'. Must be a non-negative number of milliseconds (0 disables the floor).");

                    break;
                case "--drift-tolerance" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var dt) && dt is >= 0 and <= 1)
                        driftTolerance = dt;
                    else
                        errors.Add($"Invalid --drift-tolerance value '{args[i]}'. Must be a fraction in [0, 1] (e.g. 0.1; 0 disables the gate).");

                    break;
                case "--max-drift-restarts" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var mdr) && mdr >= 0)
                        maxDriftRestarts = mdr;
                    else
                        errors.Add($"Invalid --max-drift-restarts value '{args[i]}'. Must be zero or a positive integer.");

                    break;
                case "--launch-count" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var lc) && LaunchCounts.IsValid(lc))
                        launchCount = lc;
                    else
                    {
                        errors.Add($"Invalid --launch-count value '{args[i]}'. "
                                   + $"Must be {LaunchCounts.Single}-{LaunchCounts.Max}.");
                    }

                    break;
                case "--percentiles" when i + 1 < args.Length:
                    var raw = args[++i];
                    var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var parsed = new List<double>(parts.Length);
                    var valid = true;

                    foreach (var part in parts)
                    {
                        if (double.TryParse(part, CultureInfo.InvariantCulture, out var val) && val is >= 0 and <= 1)
                            parsed.Add(val);
                        else
                        {
                            valid = false;
                            errors.Add($"Invalid percentile value '{part}' in --percentiles. Each value must be a fraction between 0 and 1 (e.g. 0.95).");
                        }
                    }

                    if (valid && parsed.Count > 0)
                        reportedPercentiles = parsed;
                    else if (valid)
                        errors.Add("--percentiles must specify at least one value (e.g. 0.50,0.95,0.99).");

                    break;
                case "--no-histogram":
                    noHistogram = true;
                    break;
                case "--no-drift-canary":
                    noDriftCanary = true;
                    break;
                case "--no-thread-control":
                    noThreadControl = true;
                    break;
                case "--no-samples":
                    noSamples = true;
                    break;
                case "--emit-raw":
                    emitRaw = true;
                    break;
                case "--stream-samples":
                    streamSamples = true;
                    break;
                case "--cpu-affinity" when i + 1 < args.Length:
                    var affinityRaw = args[++i];

                    try
                    {
                        cpuAffinity = EnvironmentOptions.ParseCpuAffinity(affinityRaw);
                    }
                    catch (FormatException ex)
                    {
                        errors.Add($"Invalid --cpu-affinity value '{affinityRaw}': {ex.Message}");
                    }

                    break;
                case "--priority" when i + 1 < args.Length:
                    var priorityStr = args[++i];

                    if (TryParseProcessPriority(priorityStr, out var parsedPriority))
                        processPriority = parsedPriority;
                    else
                    {
                        errors.Add(
                            $"Invalid --priority value '{priorityStr}'. Must be one of: "
                            + "normal, idle, belownormal, abovenormal, high, realtime.");
                    }

                    break;
                case "--dedicated-host-guidance":
                    dedicatedHostGuidance = true;
                    break;
                case "--otlp-endpoint" when i + 1 < args.Length:
                    var endpointStr = args[++i];

                    if (Uri.TryCreate(endpointStr, UriKind.Absolute, out var endpointUri)
                        && (endpointUri.Scheme == Uri.UriSchemeHttp || endpointUri.Scheme == Uri.UriSchemeHttps))
                        otlpEndpoint = endpointStr;
                    else
                        errors.Add($"Invalid --otlp-endpoint value '{endpointStr}'. Must be an absolute http:// or https:// URL.");

                    break;
                case "--runtimes" when i + 1 < args.Length:
                    var runtimesRaw = args[++i];
                    var runtimeParts = runtimesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    foreach (var part in runtimeParts)
                    {
                        if (TryParseRuntimeMoniker(part, out var parsedMoniker))
                        {
                            if (!runtimes.Contains(parsedMoniker))
                                runtimes.Add(parsedMoniker);
                        }
                        else
                            errors.Add($"Unknown runtime '{part}' in --runtimes. Valid values: net8, net9, net10 (or net8.0, net9.0, net10.0).");
                    }

                    break;
                case "--filter" or "--iterations" or "--warmup" or "--output"
                    or "--reporter" or "--observer" or "--category" or "--exclude-category" or "--confidence" or "--order"
                    or "--threshold-pct" or "--seed" or "--alpha" or "--outlier" or "--tail-basis" or "--detail" or "--profile"
                    or "--runtime-profile"
                    or "--auto-tune" or "--ops-per-sample" or "--ci-target" or "--min-samples" or "--max-samples"
                    or "--min-warmup" or "--max-warmup" or "--max-tuning-time" or "--autotune-cap-behavior"
                    or "--warmup-budget-fraction" or "--cap-grace-factor" or "--min-warmup-time"
                    or "--jit-quiet-period" or "--min-measurement-time" or "--drift-tolerance"
                    or "--max-drift-restarts"
                    or "--launch-count" or "--percentiles" or "--runtimes" or "--min-practical-effect"
                    or "--min-relative-shift"
                    or "--cpu-affinity" or "--priority" or "--otlp-endpoint" or "--diagnostics":
                    // Every flag whose case above is guarded by `when i + 1 < args.Length` belongs
                    // here, or it falls through to `default` and a user who simply forgot the value is
                    // told the flag does not exist. --diagnostics was missing for exactly that reason;
                    // CliArgsTests.Parse_RecognisesEveryKnownFlag now pins the whole set.
                    errors.Add($"Missing value for '{args[i]}'.");
                    break;
                default:
                    errors.Add($"Unknown flag: '{args[i]}'. Use --help to see available options.");
                    break;
            }
        }

        return (new CliArgs
        {
            ShowHelp = showHelp,
            ListOnly = listOnly,
            DryRun = dryRun,
            ThresholdPct = thresholdPct,
            Filter = filter,
            OutputDir = outputDir,
            Seed = seed,
            RunOrder = runOrder,
            Iterations = iterations,
            WarmupIterations = warmupIterations,
            ConfidenceLevel = confidenceLevel,
            Alpha = alpha,
            OutlierMode = outlierMode,
            TailMetricsBasis = tailMetricsBasis,
            ReporterNames = reporterNames,
            ObserverNames = observerNames,
            CategoryFilterInclude = categoryInclude,
            CategoryFilterExclude = categoryExclude,
            Detail = detail,
            InProcess = inProcess,
            StrictIsolation = strictIsolation,
            VerifyIsolation = verifyIsolation,
            CrossClass = crossClass,
            Profile = profile,
            RuntimeProfile = runtimeProfile,
            ForceGc = forceGc,
            NoAllocations = noAllocations,
            NoGcBetweenBenchmarks = noGcBetweenBenchmarks,
            MinPracticalEffect = minPracticalEffect,
            MinRelativeShift = minRelativeShift,
            Diagnostics = diagnostics,
            OpsPerSample = opsPerSample,
            AutoTunePreset = autoTunePreset,
            CiTarget = ciTarget,
            MinSamples = minSamples,
            MaxSamples = maxSamples,
            MinWarmup = minWarmup,
            MaxWarmup = maxWarmup,
            MaxTuningTime = maxTuningTime,
            AutoTuneCapBehavior = autoTuneCapBehavior,
            WarmupBudgetFraction = warmupBudgetFraction,
            CapGraceFactor = capGraceFactor,
            MinWarmupTime = minWarmupTime,
            NoJitQuiescence = noJitQuiescence,
            JitQuietPeriod = jitQuietPeriod,
            MinMeasurementTime = minMeasurementTime,
            DriftTolerance = driftTolerance,
            MaxDriftRestarts = maxDriftRestarts,
            LaunchCount = launchCount,
            ReportedPercentiles = reportedPercentiles,
            NoHistogram = noHistogram,
            NoDriftCanary = noDriftCanary,
            NoThreadControl = noThreadControl,
            NoSamples = noSamples,
            EmitRaw = emitRaw,
            StreamSamples = streamSamples,
            Runtimes = runtimes,
            CpuAffinity = cpuAffinity,
            ProcessPriority = processPriority,
            DedicatedHostGuidance = dedicatedHostGuidance,
            OtlpEndpoint = otlpEndpoint,
        }, errors);
    }

    /// <summary>
    ///     Parses <paramref name="args" /> and emits validation errors to stderr.
    ///     Sets <c>Environment.ExitCode = 1</c> when any errors are found.
    /// </summary>
    public static CliArgs Parse(string[] args)
    {
        var (cliArgs, errors) = ParseCore(args);
        var allErrors = new List<string>(errors);

        foreach (var name in cliArgs.ReporterNames)
        {
            if (!ReporterRegistry.TryCreate(name, null, cliArgs.Detail, out _))
            {
                allErrors.Add(
                    $"Unknown reporter: '{name}'. Valid: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}. (NBenchmark.Reporters.Console package provides 'console'.)");
            }
        }

        foreach (var name in cliArgs.ObserverNames)
        {
            if (!ObserverRegistry.IsRegistered(name))
            {
                allErrors.Add(
                    $"Unknown observer: '{name}'. Valid: {string.Join(", ", ObserverRegistry.Available.Select(r => r.Name))}.");
            }
        }

        foreach (var error in allErrors)
        {
            Console.Error.WriteLine(error);
        }

        if (allErrors.Count > 0)
            Environment.ExitCode = 1;

        return cliArgs;
    }

    private static bool TryParseOutlierMode(string value, out OutlierMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "none":
                mode = NBenchmark.OutlierMode.None;
                return true;
            case "top5":
                mode = NBenchmark.OutlierMode.RemoveTop5Percent;
                return true;
            case "both5":
                mode = NBenchmark.OutlierMode.RemoveTopAndBottom5Percent;
                return true;
            case "iqr":
                mode = NBenchmark.OutlierMode.IqrFence;
                return true;
            case "mad":
                mode = NBenchmark.OutlierMode.MedianAbsoluteDeviation;
                return true;
            default:
                mode = NBenchmark.OutlierMode.IqrFence;
                return false;
        }
    }

    private static bool TryParseRuntimeMoniker(string value, out RuntimeMoniker moniker)
    {
        switch (value.ToLowerInvariant())
        {
            case "net8" or "net8.0":
                moniker = RuntimeMoniker.Net8;
                return true;
            case "net9" or "net9.0":
                moniker = RuntimeMoniker.Net9;
                return true;
            case "net10" or "net10.0":
                moniker = RuntimeMoniker.Net10;
                return true;
            default:
                moniker = default;
                return false;
        }
    }

    private static bool TryParseProcessPriority(string value, out ProcessPriorityClass priority)
    {
        switch (value.ToLowerInvariant())
        {
            case "normal":
                priority = ProcessPriorityClass.Normal;
                return true;
            case "idle":
                priority = ProcessPriorityClass.Idle;
                return true;
            case "belownormal":
                priority = ProcessPriorityClass.BelowNormal;
                return true;
            case "abovenormal":
                priority = ProcessPriorityClass.AboveNormal;
                return true;
            case "high":
                priority = ProcessPriorityClass.High;
                return true;
            case "realtime":
                priority = ProcessPriorityClass.RealTime;
                return true;
            default:
                priority = default;
                return false;
        }
    }

    private static void AddCategory(string rawValue, string flagName, List<string> target, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            errors.Add($"Invalid {flagName} value. Category names cannot be blank.");
            return;
        }

        var normalized = rawValue.Trim();

        if (!target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            target.Add(normalized);
    }

    /// <summary>
    ///     Every flag <see cref="Parse" /> accepts, and therefore every flag
    ///     <see cref="PrintHelp" /> must document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This exists because <c>--strict-isolation</c> and <c>--verify-isolation</c> shipped
    ///         parsed but undocumented. They are the two flags a CI pipeline most needs - the whole
    ///         point of the first is that an advisory warning nobody reads is indistinguishable from no
    ///         warning - and <c>--help</c> was the one place a user would have looked.
    ///     </para>
    ///     <para>
    ///         <c>CliArgsTests</c> holds this to an exact set equality against the flags named in
    ///         <see cref="PrintHelp" />'s output, and separately requires <see cref="Parse" /> to
    ///         recognise each entry, so the list cannot drift from either side. It does not detect a
    ///         flag added to the parse switch and to neither this list nor the help text - a
    ///         <c>switch</c>'s labels are not enumerable at runtime - so adding a case here is part of
    ///         adding a case there.
    ///     </para>
    /// </remarks>
    internal static readonly string[] KnownFlags =
    [
        "--alpha", "--auto-tune", "--autotune-cap-behavior", "--cap-grace-factor", "--category",
        "--ci-target", "--confidence", "--cpu-affinity", "--cross-class",
        "--dedicated-host-guidance", "--detail", "--diagnostics", "--drift-tolerance", "--dry-run",
        "--emit-raw", "--exclude-category", "--filter", "--force-gc", "--help", "--in-process",
        "--iterations", "--jit-quiet-period", "--launch-count", "--list", "--max-drift-restarts",
        "--max-samples", "--max-tuning-time", "--max-warmup", "--min-measurement-time",
        "--min-practical-effect", "--min-relative-shift", "--min-samples", "--min-warmup", "--min-warmup-time",
        "--no-allocations", "--no-drift-canary", "--no-gc-between-benchmarks", "--no-histogram",
        "--no-jit-quiescence",
        "--no-samples", "--no-thread-control", "--observer", "--ops-per-sample", "--order", "--otlp-endpoint", "--outlier",
        "--output", "--percentiles", "--priority", "--profile", "--reporter", "--runtime-profile",
        "--runtimes", "--seed", "--stream-samples", "--strict-isolation", "--tail-basis",
        "--threshold-pct", "--verify-isolation", "--warmup", "--warmup-budget-fraction",
    ];

    internal static void PrintHelp()
    {
        Console.WriteLine("Usage: myapp.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --filter <pattern>     Run suites/methods matching glob (e.g., String*, *.Contains*)");
        Console.WriteLine("  --category <name>      Include benchmarks tagged with this category (repeatable, OR)");
        Console.WriteLine("  --exclude-category <name> Exclude benchmarks tagged with this category (repeatable, OR)");
        Console.WriteLine("  --iterations <n>       Pin measured sample count (default: auto, CI-driven)");
        Console.WriteLine("  --warmup <n>           Pin warmup sample count (default: auto, plateau-driven)");
        Console.WriteLine($"  --reporter <type>      Set reporter: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}{FormatAutoAttached()}");

        Console.WriteLine(
            $"  --observer <type>      Attach measurement observer: {string.Join(", ", ObserverRegistry.Available.Select(r => r.Name))}{FormatAutoAttachedObservers()}");

        Console.WriteLine("                         (repeatable; multiple observers are composed into a fan-out)");
        Console.WriteLine("  --output <dir>         Set output directory for file-based reporters");
        Console.WriteLine("  --confidence <0-1>     Confidence level for the interval on the mean (default: 0.95)");
        Console.WriteLine("  --alpha <0-1>          Significance level for the significance test (default: 0.05)");
        Console.WriteLine("  --outlier <mode>       Outlier trimming: none, top5, both5, iqr (default), mad");
        Console.WriteLine("  --tail-basis <basis>   Percentile/Min/Max/histogram source: raw (default), trimmed");
        Console.WriteLine("  --auto-tune <preset>   Adaptive tuning preset: default, quick, or thorough");
        Console.WriteLine("  --ops-per-sample <n>   Pin ops-per-sample K (default: auto-calibrated)");
        Console.WriteLine("  --ci-target <0-1>      Target relative CI half-width for auto sampling (default: 0.025)");
        Console.WriteLine("  --min-samples <n>      Minimum measured samples in auto mode (default: 30)");
        Console.WriteLine("  --max-samples <n>      Maximum measured samples in auto mode (default: 5000)");
        Console.WriteLine("  --min-warmup <n>       Minimum warmup samples in auto mode (default: 8)");
        Console.WriteLine("  --max-warmup <n>       Maximum warmup samples in auto mode (default: 100000)");
        Console.WriteLine("  --max-tuning-time <s>  Wall-clock cap per benchmark, in seconds (default: 20)");
        Console.WriteLine("  --autotune-cap-behavior <mode>  Cap handling: warn (default) or error");
        Console.WriteLine("  --warmup-budget-fraction <0-1>  Max share of --max-tuning-time for calibration + warmup (default: 0.4)");
        Console.WriteLine("  --cap-grace-factor <n>  Multiplier on --max-tuning-time the measurement phase may reach while chasing --min-samples (default: 1.5)");
        Console.WriteLine("  --min-warmup-time <ms>  Minimum warmup time before auto-warmup may settle, in ms (default: 500; 0 disables)");
        Console.WriteLine("  --no-jit-quiescence     Disable the JIT-quiescence warmup gate (keep only the time floor)");
        Console.WriteLine("  --jit-quiet-period <ms>  How long the JIT must stay quiet before auto-warmup may settle, in ms (default: 50; 0 disables the gate)");
        Console.WriteLine("  --min-measurement-time <ms>  Minimum measurement time before the CI target may stop sampling, in ms (default: 100; 0 disables)");
        Console.WriteLine("  --drift-tolerance <0-1>  Max first-half/second-half disagreement before the CI stop is refused (default: 0.1; 0 disables)");
        Console.WriteLine("  --max-drift-restarts <n>  How many times drift may discard samples and restart measurement (default: 2)");
        Console.WriteLine("  --launch-count <n>      Repeat each benchmark N times as separate launches (harness default: 5)");
        Console.WriteLine("  --percentiles <list>    Custom percentile values (comma-separated, e.g. 0.50,0.95,0.99,0.999)");
        Console.WriteLine("  --no-histogram          Disable latency histogram computation");
        Console.WriteLine("  --no-drift-canary       Disable the host drift canary (the control workload measured between benchmarks)");
        Console.WriteLine("  --no-thread-control     Disable thread-level affinity, priority and (on macOS) performance-core placement");
        Console.WriteLine("  --no-samples            Omit raw per-sample arrays from JSON output (samples still feed significance and Console histogram)");
        Console.WriteLine($"  --emit-raw              Return every raw sample from an isolated worker instead of a {MeasurementOptions.DefaultMaxRawSamples}-sample representative subset");
        Console.WriteLine("  --stream-samples        Forward the live per-sample observer stream out of an isolated worker (needs --observer; costs fidelity)");
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Run with 0 iterations; no measurement, no body invocation");
        Console.WriteLine("  --in-process           Run every benchmark in the host process (disables isolation)");
        Console.WriteLine("  --strict-isolation     Fail with exit code 1 if any benchmark could not be isolated");
        Console.WriteLine("  --verify-isolation     Re-measure in this process and print how much isolation changed");
        Console.WriteLine("  --cross-class          Compute significance across all classes instead of per class");
        Console.WriteLine("  --runtimes <list>      Runtimes to compare (comma-separated, e.g. net8,net9,net10)");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --seed <n>             Seed for deterministic random ordering");
        Console.WriteLine("  --detail <level>       Report detail: simple, standard, or advanced (default: simple)");
        Console.WriteLine("  --threshold-pct <n>    Fail with exit code 1 if any benchmark regresses");
        Console.WriteLine("                        >N% vs baseline (median-based comparison; n >= 1).");
        Console.WriteLine("  --profile <mode>       Measurement profile: realistic (default) or independent");
        Console.WriteLine("  --runtime-profile <p>   Runtime config for isolated children: steady-state");
        Console.WriteLine("                          (default), production, server-gc, or host");
        Console.WriteLine("  --force-gc             Force Gen0 GC before every iteration (overrides profile)");
        Console.WriteLine("  --no-allocations       Disable allocation tracking (overrides profile)");
        Console.WriteLine("  --no-gc-between-benchmarks  Disable the full GC between benchmarks (on by default for both profiles)");
        Console.WriteLine("  --min-practical-effect <0-1>  Min practical effect for a significant verdict (default: 0.147; 0 = p-value only)");
        Console.WriteLine("  --min-relative-shift <0-1>   Min relative median shift for a significant verdict (default: 0.01; 0 = off)");
        Console.WriteLine("  --diagnostics <mode>   Runtime diagnostics: none, gc, gcandcpu, all (default: gc)");
        Console.WriteLine("  --cpu-affinity <list>  Pin benchmark process to logical CPU cores (e.g. 0 or 2,3)");
        Console.WriteLine("  --priority <level>     Process priority: normal, idle, belownormal, abovenormal, high, realtime");
        Console.WriteLine("  --dedicated-host-guidance  Warn when the host looks noisy (low core count, unraisable priority, macOS throttling)");
        Console.WriteLine("  --otlp-endpoint <url>  OTLP endpoint for the OpenTelemetry SDK (http:// or https://); forwarded to isolated children");
        Console.WriteLine("  --help, -h             Show this help text");
    }

    private static string FormatAutoAttached()
    {
        var names = ReporterRegistry.AutoAttached;

        if (names.Count == 0)
            return string.Empty;

        return $" (auto-attached: {string.Join(", ", names.Select(r => r.Name))})";
    }

    private static string FormatAutoAttachedObservers()
    {
        var names = ObserverRegistry.AutoAttached;

        if (names.Count == 0)
            return string.Empty;

        return $" (auto-attached: {string.Join(", ", names.Select(r => r.Name))})";
    }
}
