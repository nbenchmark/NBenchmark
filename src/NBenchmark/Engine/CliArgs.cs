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
    public IReadOnlyList<IReporter> CliReporters { get; init; } = [];

    public static CliArgs Parse(string[] args)
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
        var cliReporters = new List<IReporter>();

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
                    {
                        Console.Error.WriteLine(
                            $"Invalid --iterations value '{args[i]}'. Must be {MeasurementOptions.MinIterations}–{MeasurementOptions.MaxIterations}.");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var warmup)
                        && warmup >= 0
                        && warmup <= MeasurementOptions.MaxWarmupIterations)
                        warmupIterations = warmup;
                    else
                    {
                        Console.Error.WriteLine(
                            $"Invalid --warmup value '{args[i]}'. Must be 0–{MeasurementOptions.MaxWarmupIterations}.");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--output" when i + 1 < args.Length:
                    outputDir = PathValidation.ValidateOutputPath(args[++i]);
                    break;
                case "--reporter" when i + 1 < args.Length:
                    var name = args[++i];
                    if (ReporterRegistry.TryCreate(name, null, out var reporter))
                        cliReporters.Add(reporter);
                    else
                    {
                        Console.Error.WriteLine(
                            $"Unknown reporter: '{name}'. Valid: {string.Join(", ", ReporterRegistry.Available.Select(r => r.Name))}. (NBenchmark.Reporters.Console package provides 'console'.)");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--confidence" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var conf)
                        && conf is > 0 and < 1)
                        confidenceLevel = conf;
                    else
                    {
                        Console.Error.WriteLine(
                            $"Invalid --confidence value '{args[i]}'. Must be a fraction strictly between 0 and 1 (e.g. 0.95).");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--order" when i + 1 < args.Length:
                    var order = args[++i];
                    if (string.Equals(order, "declaration", StringComparison.OrdinalIgnoreCase))
                        runOrder = NBenchmark.RunOrder.Declaration;
                    else if (string.Equals(order, "random", StringComparison.OrdinalIgnoreCase))
                        runOrder = NBenchmark.RunOrder.Random;
                    else
                    {
                        Console.Error.WriteLine(
                            $"Invalid --order value '{order}'. Must be 'random' or 'declaration'.");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--threshold-pct" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var tPct)
                        && tPct > 0)
                        thresholdPct = tPct;
                    else
                    {
                        Console.Error.WriteLine(
                            $"Invalid --threshold-pct value '{args[i]}'. Must be a positive integer (1 or greater).");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var seedVal))
                        seed = seedVal;
                    else
                    {
                        Console.Error.WriteLine($"Invalid --seed value '{args[i]}'. Must be an integer.");
                        Environment.ExitCode = 1;
                    }
                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--filter" or "--iterations" or "--warmup" or "--output"
                    or "--reporter" or "--confidence" or "--order" or "--threshold-pct" or "--seed":
                    Console.Error.WriteLine($"Missing value for '{args[i]}'.");
                    Environment.ExitCode = 1;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown flag: '{args[i]}'. Use --help to see available options.");
                    Environment.ExitCode = 1;
                    break;
            }
        }

        return new CliArgs
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
            CliReporters = cliReporters,
        };
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
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Run with 0 iterations; no measurement, no body invocation");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --seed <n>             Seed for deterministic random ordering");
        Console.WriteLine("  --threshold-pct <n>    Fail with exit code 1 if any benchmark regresses");
        Console.WriteLine("                        >N% vs baseline (median-based comparison; n >= 1).");
        Console.WriteLine("  --help, -h             Show this help text");
    }
}
