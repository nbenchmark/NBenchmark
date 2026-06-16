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
    public ReportDetail Detail { get; init; } = ReportDetail.Simple;

    /// <summary>
    ///     When true, every benchmark runs in the host process, overriding Host mode's
    ///     isolated-by-default execution and any <c>[IsolatedProcess]</c> attributes.
    /// </summary>
    public bool InProcess { get; init; }

    public MeasurementProfile? Profile { get; init; }

    public bool? ForceGc { get; init; }

    public bool? NoAllocations { get; init; }

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
        var detail = ReportDetail.Simple;

        double? alpha = null;
        var inProcess = false;
        OutlierMode? outlierMode = null;
        MeasurementProfile? profile = null;
        bool? forceGc = null;
        bool? noAllocations = null;

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
                case "--reporter" when i + 1 < args.Length:
                    reporterNames.Add(args[++i]);
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
                    else if (string.Equals(detailStr, "advanced", StringComparison.OrdinalIgnoreCase))
                        detail = ReportDetail.Advanced;
                    else
                        errors.Add($"Invalid --detail value '{detailStr}'. Must be 'simple' or 'advanced'.");

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
                case "--filter" or "--iterations" or "--warmup" or "--output"
                    or "--reporter" or "--confidence" or "--order" or "--threshold-pct" or "--seed" or "--alpha"
                    or "--outlier" or "--detail" or "--profile":
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
            Detail = detail,
            InProcess = inProcess,
            Profile = profile,
            ForceGc = forceGc,
            NoAllocations = noAllocations,
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
                allErrors.Add($"Unknown reporter: '{name}'. Valid: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}. (NBenchmark.Reporters.Console package provides 'console'.)");
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

    internal static void PrintHelp()
    {
        Console.WriteLine("Usage: myapp.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --filter <pattern>     Run suites/methods matching glob (e.g., String*, *.Contains*)");
        Console.WriteLine("  --iterations <n>       Number of measured iterations (default: 200)");
        Console.WriteLine("  --warmup <n>           Number of warmup iterations (default: 25)");
        Console.WriteLine($"  --reporter <type>      Set reporter: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}");
        Console.WriteLine("  --output <dir>         Set output directory for file-based reporters");
        Console.WriteLine("  --confidence <0-1>     Confidence level for the interval on the mean (default: 0.95)");
        Console.WriteLine("  --alpha <0-1>          Significance level for the significance test (default: 0.05)");
        Console.WriteLine("  --outlier <mode>       Outlier trimming: none, top5, both5, iqr (default), mad");
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Run with 0 iterations; no measurement, no body invocation");
        Console.WriteLine("  --in-process           Run every benchmark in the host process (disables isolation)");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --seed <n>             Seed for deterministic random ordering");
        Console.WriteLine("  --detail <level>       Report detail: simple or advanced (default: simple)");
        Console.WriteLine("  --threshold-pct <n>    Fail with exit code 1 if any benchmark regresses");
        Console.WriteLine("                        >N% vs baseline (median-based comparison; n >= 1).");
        Console.WriteLine("  --profile <mode>       Measurement profile: realistic (default) or independent");
        Console.WriteLine("  --force-gc             Force Gen0 GC before every iteration (overrides profile)");
        Console.WriteLine("  --no-allocations       Disable allocation tracking (overrides profile)");
        Console.WriteLine("  --help, -h             Show this help text");
    }
}
