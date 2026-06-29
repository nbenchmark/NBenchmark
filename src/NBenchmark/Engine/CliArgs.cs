using System.Diagnostics;
using System.Globalization;
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
    ///     When true, significance is computed across all classes in a single comparison
    ///     table instead of per class. The baseline is chosen from the whole group.
    /// </summary>
    public bool CrossClass { get; init; }

    public MeasurementProfile? Profile { get; init; }

    public bool? ForceGc { get; init; }

    public bool? NoAllocations { get; init; }

    public int? OpsPerSample { get; init; }

    public AutoTunePreset? AutoTunePreset { get; init; }

    public double? CiTarget { get; init; }

    public int? MinSamples { get; init; }

    public int? MaxSamples { get; init; }

    public int? MinWarmup { get; init; }

    public int? MaxWarmup { get; init; }

    public TimeSpan? MaxTuningTime { get; init; }

    public AutoTuneCapBehavior? AutoTuneCapBehavior { get; init; }

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
        OutlierMode? outlierMode = null;
        MeasurementProfile? profile = null;
        bool? forceGc = null;
        bool? noAllocations = null;
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
        int? launchCount = null;
        IReadOnlyList<double>? reportedPercentiles = null;
        var noHistogram = false;
        var runtimes = new List<RuntimeMoniker>();
        IReadOnlyList<int>? cpuAffinity = null;
        ProcessPriorityClass? processPriority = null;
        var crossClass = false;
        var dedicatedHostGuidance = false;

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
                case "--force-gc":
                    forceGc = true;
                    break;
                case "--no-allocations":
                    noAllocations = true;
                    break;
                case "--diagnostics" when i + 1 < args.Length:
                    var diagStr = args[++i];

                    if (string.Equals(diagStr, "none", StringComparison.OrdinalIgnoreCase))
                        diagnostics = NBenchmark.DiagnosticsMode.None;
                    else if (string.Equals(diagStr, "gc", StringComparison.OrdinalIgnoreCase))
                        diagnostics = NBenchmark.DiagnosticsMode.Gc;
                    else if (string.Equals(diagStr, "gcandcpu", StringComparison.OrdinalIgnoreCase))
                        diagnostics = NBenchmark.DiagnosticsMode.GcAndCpu;
                    else if (string.Equals(diagStr, "all", StringComparison.OrdinalIgnoreCase))
                        diagnostics = NBenchmark.DiagnosticsMode.All;
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
                case "--min-warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var minw) && minw >= 0 && minw <= MeasurementOptions.MaxWarmupIterations)
                        minWarmup = minw;
                    else
                        errors.Add($"Invalid --min-warmup value '{args[i]}'. Must be 0–{MeasurementOptions.MaxWarmupIterations}.");

                    break;
                case "--max-warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var maxw) && maxw >= 1 && maxw <= MeasurementOptions.MaxWarmupIterations)
                        maxWarmup = maxw;
                    else
                        errors.Add($"Invalid --max-warmup value '{args[i]}'. Must be 1–{MeasurementOptions.MaxWarmupIterations}.");

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
                case "--launch-count" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var lc) && lc >= 1 && lc <= MeasurementOptions.MaxLaunchCount)
                        launchCount = lc;
                    else
                        errors.Add($"Invalid --launch-count value '{args[i]}'. Must be 1-{MeasurementOptions.MaxLaunchCount}.");

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
                        errors.Add(
                            $"Invalid --priority value '{priorityStr}'. Must be one of: "
                            + "normal, idle, belownormal, abovenormal, high, realtime.");

                    break;
                case "--dedicated-host-guidance":
                    dedicatedHostGuidance = true;
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
                    or "--threshold-pct" or "--seed" or "--alpha" or "--outlier" or "--detail" or "--profile"
                    or "--auto-tune" or "--ops-per-sample" or "--ci-target" or "--min-samples" or "--max-samples"
                    or "--min-warmup" or "--max-warmup" or "--max-tuning-time" or "--autotune-cap-behavior"
                    or "--launch-count" or "--percentiles" or "--runtimes"
                    or "--cpu-affinity" or "--priority":
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
            ReporterNames = reporterNames,
            ObserverNames = observerNames,
            CategoryFilterInclude = categoryInclude,
            CategoryFilterExclude = categoryExclude,
            Detail = detail,
            InProcess = inProcess,
            CrossClass = crossClass,
            Profile = profile,
            ForceGc = forceGc,
            NoAllocations = noAllocations,
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
            LaunchCount = launchCount,
            ReportedPercentiles = reportedPercentiles,
            NoHistogram = noHistogram,
            Runtimes = runtimes,
            CpuAffinity = cpuAffinity,
            ProcessPriority = processPriority,
            DedicatedHostGuidance = dedicatedHostGuidance,
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
            if (!ObserverRegistry.TryCreate(name, out _))
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
        Console.WriteLine($"  --reporter <type>      Set reporter: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}");
        Console.WriteLine($"  --observer <type>      Attach measurement observer: {string.Join(", ", ObserverRegistry.Available.Select(r => r.Name))}");
        Console.WriteLine("                         (repeatable; multiple observers are composed into a fan-out)");
        Console.WriteLine("  --output <dir>         Set output directory for file-based reporters");
        Console.WriteLine("  --confidence <0-1>     Confidence level for the interval on the mean (default: 0.95)");
        Console.WriteLine("  --alpha <0-1>          Significance level for the significance test (default: 0.05)");
        Console.WriteLine("  --outlier <mode>       Outlier trimming: none, top5, both5, iqr (default), mad");
        Console.WriteLine("  --auto-tune <preset>   Adaptive tuning preset: default, quick, or thorough");
        Console.WriteLine("  --ops-per-sample <n>   Pin ops-per-sample K (default: auto-calibrated)");
        Console.WriteLine("  --ci-target <0-1>      Target relative CI half-width for auto sampling (default: 0.025)");
        Console.WriteLine("  --min-samples <n>      Minimum measured samples in auto mode (default: 30)");
        Console.WriteLine("  --max-samples <n>      Maximum measured samples in auto mode (default: 100000)");
        Console.WriteLine("  --min-warmup <n>       Minimum warmup samples in auto mode (default: 8)");
        Console.WriteLine("  --max-warmup <n>       Maximum warmup samples in auto mode (default: 10000)");
        Console.WriteLine("  --max-tuning-time <s>  Wall-clock cap per benchmark, in seconds (default: 20)");
        Console.WriteLine("  --autotune-cap-behavior <mode>  Cap handling: warn (default) or error");
        Console.WriteLine("  --launch-count <n>      Repeat each benchmark N times as separate launches (default: 1)");
        Console.WriteLine("  --percentiles <list>    Custom percentile values (comma-separated, e.g. 0.50,0.95,0.99,0.999)");
        Console.WriteLine("  --no-histogram          Disable latency histogram computation");
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Run with 0 iterations; no measurement, no body invocation");
        Console.WriteLine("  --in-process           Run every benchmark in the host process (disables isolation)");
        Console.WriteLine("  --cross-class          Compute significance across all classes instead of per class");
        Console.WriteLine("  --runtimes <list>      Runtimes to compare (comma-separated, e.g. net8,net9,net10)");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --seed <n>             Seed for deterministic random ordering");
        Console.WriteLine("  --detail <level>       Report detail: simple, standard, or advanced (default: simple)");
        Console.WriteLine("  --threshold-pct <n>    Fail with exit code 1 if any benchmark regresses");
        Console.WriteLine("                        >N% vs baseline (median-based comparison; n >= 1).");
        Console.WriteLine("  --profile <mode>       Measurement profile: realistic (default) or independent");
        Console.WriteLine("  --force-gc             Force Gen0 GC before every iteration (overrides profile)");
        Console.WriteLine("  --no-allocations       Disable allocation tracking (overrides profile)");
        Console.WriteLine("  --diagnostics <mode>   Runtime diagnostics: none, gc, gcandcpu, all (default: gc)");
        Console.WriteLine("  --cpu-affinity <list>  Pin benchmark process to logical CPU cores (e.g. 0 or 2,3)");
        Console.WriteLine("  --priority <level>     Process priority: normal, idle, belownormal, abovenormal, high, realtime");
        Console.WriteLine("  --dedicated-host-guidance  Warn when the host looks noisy (low core count, unraisable priority, macOS throttling)");
        Console.WriteLine("  --help, -h             Show this help text");
    }
}
