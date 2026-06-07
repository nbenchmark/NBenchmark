using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkHost
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [];
    private bool _dryRun;
    private string? _filter;
    private bool _listOnly;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private string? _outputDir;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private int? _seed;
    private bool _showHelp;
    private bool _thresholdRejected;
    private Func<Type, object>? _instanceFactory;
    private Action? _postSuiteCleanup;

    private BenchmarkHost()
    {
    }

    public static BenchmarkHost Create(string[] args)
    {
        var host = new BenchmarkHost();
        host.ParseArgs(args);
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

    public BenchmarkHost WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    public BenchmarkHost WithInstanceFactory(Func<Type, object> factory)
    {
        _instanceFactory = factory;
        return this;
    }

    internal Action? PostSuiteCleanup
    {
        get => _postSuiteCleanup;
        set => _postSuiteCleanup = value;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_showHelp)
        {
            PrintHelp();
            return Array.Empty<BenchmarkResult>();
        }

        Console.WriteLine($"Timer resolution: {Stopwatch.Frequency:N0} ticks/s "
                          + $"({1_000_000_000.0 / Stopwatch.Frequency:F2} ns per tick)");

        Console.WriteLine();

        var discoverer = new BenchmarkDiscoverer();
        var allSuites = _assemblies.SelectMany(discoverer.Discover).ToList();

        if (allSuites.Count == 0)
        {
            Console.WriteLine("No benchmark classes found. Decorate methods with [Benchmark].");
            return Array.Empty<BenchmarkResult>();
        }

        var filtered = FilterSuites(allSuites);

        if (_listOnly)
        {
            foreach (var suite in filtered)
            {
                Console.WriteLine($"── {suite.Type.Name} ──");

                foreach (var b in suite.Benchmarks)
                {
                    Console.WriteLine($"    {b.DisplayName}"
                                      + (b.Attribute.Description is not null ? $" — {b.Attribute.Description}" : ""));
                }
            }

            return Array.Empty<BenchmarkResult>();
        }

        if (!_progressExplicitlySet)
            _progress = NullBenchmarkProgress.Instance;

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var totalBenchmarks = filtered.Sum(s => s.Benchmarks.Count);
        var runningIndex = 0;

        await _progress.OnSuiteStarting(
            filtered.SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.DisplayName}")).ToList(),
            totalBenchmarks).ConfigureAwait(false);

        foreach (var suite in filtered)
        {
            object? instance = null;
            var instanceFromFactory = _instanceFactory is not null;

            try
            {
                instance = _instanceFactory?.Invoke(suite.Type) ?? Activator.CreateInstance(suite.Type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var hint = _instanceFactory is null
                    ? "the type must have a public parameterless constructor, or be internal with "
                      + "a public constructor and InternalsVisibleTo. "
                    : "the instance factory threw during resolution. ";

                Console.WriteLine($"[Error] Could not instantiate {suite.Type.Name} — "
                                  + hint
                                  + $"Details: {ex.Message}");

                continue;
            }

            var typedInstance = instance!;

            try
            {
                suite.SetupDelegate?.Invoke(typedInstance);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Error] Setup failed for {suite.Type.Name}: {ex.Message}");

                foreach (var b in suite.Benchmarks)
                {
                    var name = $"{suite.Type.Name}.{b.Method.Name}";

                    allResults.Add(BenchmarkResult.CreateErrored(name,
                        $"Suite setup failed: {ex.Message}", b.Attribute.Description,
                        b.Attribute.Baseline, _options.OutlierMode));
                }

                continue;
            }

            try
            {
                var ordered = _runOrder == RunOrder.Random
                    ? ShuffleBenchmarks(suite.Benchmarks.ToList(), _seed ?? Random.Shared.Next())
                    : suite.Benchmarks;

                foreach (var benchmark in ordered)
                {
                    var benchmarkName = $"{suite.Type.Name}.{benchmark.DisplayName}";
                    runningIndex++;

                    var options = benchmark.Attribute.Iterations.HasValue
                        ? _options with { Iterations = benchmark.Attribute.Iterations.Value }
                        : _options;

                    if (_dryRun)
                    {
                        options = options with { Iterations = 0, WarmupIterations = 0 };
                    }

                    var spec = new RunSpec
                    {
                        Options = options,
                        Description = benchmark.Attribute.Description,
                        IsBaseline = benchmark.Attribute.Baseline,
                        IterationSetup = benchmark.IterationSetupDelegate is { } s ? () => s(typedInstance) : null,
                        IterationTeardown = benchmark.IterationTeardownDelegate is { } t ? () => t(typedInstance) : null,
                        Progress = _progress,
                    };

                    await _progress.OnBenchmarkStarting(benchmarkName, runningIndex, totalBenchmarks).ConfigureAwait(false);

                    BenchmarkResult result;

                    try
                    {
                        MeasurementOutcome outcome;

                        if (benchmark.AsyncDelegate is not null)
                        {
                            var asyncDel = benchmark.AsyncDelegate;
                            var resultExtractor = benchmark.ResultExtractor;

                            if (resultExtractor is not null)
                            {
                                Func<Task<object?>> body = async () =>
                                {
                                    var task = asyncDel(typedInstance);
                                    await task.ConfigureAwait(false);
                                    return resultExtractor(task);
                                };
                                outcome = await BenchmarkRunner.Instance.RunAsync(benchmarkName, body, spec, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                Func<Task> body = async () =>
                                {
                                    var task = asyncDel(typedInstance);
                                    await task.ConfigureAwait(false);
                                };
                                outcome = await BenchmarkRunner.Instance.RunAsync(benchmarkName, body, spec, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            var syncDel = benchmark.SyncDelegate!;
                            Func<object?> body = () => syncDel(typedInstance);
                            outcome = BenchmarkRunner.Instance.Run(benchmarkName, body, spec, cancellationToken);
                        }

                        result = outcome.Result;
                        rawSamples[benchmarkName] = outcome.RawSamples;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result = BenchmarkResult.CreateErrored(benchmarkName, ex.ToString(),
                            benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                            options.OutlierMode);
                    }

                    allResults.Add(result);
                    await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);

                    if (options.ForceGcBetweenBenchmarks)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            finally
            {
                try
                {
                    suite.TeardownDelegate?.Invoke(typedInstance);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[Warning] Teardown failed for {suite.Type.Name}: {ex.Message}");
                }

                _postSuiteCleanup?.Invoke();

                if (!instanceFromFactory)
                {
                    if (typedInstance is IAsyncDisposable ad)
                        await ad.DisposeAsync();
                    else if (typedInstance is IDisposable d)
                        d.Dispose();
                }
            }
        }

        await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

        if (_options.EnableSignificance && allResults.Count(r => !r.Errored) > 1)
            Significance.ComputeSignificance(allResults, rawSamples);

        if (!string.IsNullOrEmpty(_outputDir))
            ApplyOutputDirectory(_outputDir);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(allResults, cancellationToken).ConfigureAwait(false);
        }

        if (_thresholdRejected)
            Environment.ExitCode = 1;

        return allResults;
    }

    private void ApplyOutputDirectory(string outputDir)
    {
        for (var i = 0; i < _reporters.Count; i++)
        {
            _reporters[i] = _reporters[i] switch
            {
                JsonReporter j => new JsonReporter(outputDir),
                MarkdownReporter m => new MarkdownReporter(Path.Combine(outputDir, "benchmark-results.md")),
                CsvReporter c => new CsvReporter(Path.Combine(outputDir, "benchmark-results.csv")),
                var other => other,
            };
        }
    }

    private IReadOnlyList<BenchmarkSuiteDefinition> FilterSuites(
        IReadOnlyList<BenchmarkSuiteDefinition> suites)
    {
        if (_filter is null)
            return suites;

        return suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b => GlobMatch(_filter,
                        $"{s.Type.Name}.{b.DisplayName}"))
                    .ToList(),
            })
            .Where(s => s.Benchmarks.Count > 0)
            .ToList();
    }

    private static bool GlobMatch(string pattern, string input)
    {
        if (pattern == "*")
            return true;

        var parts = pattern.Split('*');

        if (parts.Length == 0)
            return true;

        var remaining = input;

        if (!pattern.StartsWith("*"))
        {
            var first = parts[0];

            if (!remaining.StartsWith(first, StringComparison.OrdinalIgnoreCase))
                return false;

            remaining = remaining[first.Length..];
        }

        for (var i = pattern.StartsWith("*") ? 0 : 1; i < parts.Length; i++)
        {
            var part = parts[i];

            if (i == parts.Length - 1 && !pattern.EndsWith("*"))
            {
                if (!remaining.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;

                break;
            }

            var idx = remaining.IndexOf(part, StringComparison.OrdinalIgnoreCase);

            if (idx < 0)
                return false;

            remaining = remaining[(idx + part.Length)..];
        }

        return true;
    }

    private static List<T> ShuffleBenchmarks<T>(List<T> items, int seed)
    {
        var rng = new Random(seed);
        var span = CollectionsMarshal.AsSpan(items);

        for (var i = span.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }

        return items;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: myapp.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --filter <pattern>     Run suites/methods matching glob (e.g., String*, *.Contains*)");
        Console.WriteLine("  --iterations <n>       Number of measured iterations (default: 200)");
        Console.WriteLine("  --warmup <n>           Number of warmup iterations (default: 25)");
        Console.WriteLine("  --reporter <type>      Set reporter: console, json, markdown, csv");
        Console.WriteLine("  --output <dir>         Set output directory for file-based reporters");
        Console.WriteLine("  --confidence <0-1>     Confidence level for the interval on the mean (default: 0.95)");
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Run with 0 iterations; no measurement, no body invocation");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --threshold-pct <n>    [NOT YET IMPLEMENTED] Will fail with exit code 1 if");
        Console.WriteLine("                        any benchmark regresses >N% vs baseline.");
        Console.WriteLine("  --help, -h             Show this help text");
    }

    private void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    _showHelp = true;
                    break;
                case "--filter" when i + 1 < args.Length:
                    _filter = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var iters)
                        && iters >= MeasurementOptions.MinIterations
                        && iters <= MeasurementOptions.MaxIterations)
                        _options = _options with { Iterations = iters };
                    else
                        Console.WriteLine(
                            $"Invalid --iterations value '{args[i]}'. Must be {MeasurementOptions.MinIterations}–{MeasurementOptions.MaxIterations}.");

                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var warmup) && warmup >= 0 && warmup <= MeasurementOptions.MaxWarmupIterations)
                        _options = _options with { WarmupIterations = warmup };
                    else
                        Console.WriteLine($"Invalid --warmup value '{args[i]}'. Must be 0–{MeasurementOptions.MaxWarmupIterations}.");

                    break;
                case "--output" when i + 1 < args.Length:
                    _outputDir = PathValidation.ValidateOutputPath(args[++i]);
                    break;
                case "--reporter" when i + 1 < args.Length:
                    switch (args[++i]?.ToLowerInvariant())
                    {
                        case "json":
                            _reporters.Add(new JsonReporter());
                            break;
                        case "markdown":
                            _reporters.Add(new MarkdownReporter());
                            break;
                        case "csv":
                            _reporters.Add(new CsvReporter());
                            break;
                        case "console":
                            Console.WriteLine("The 'console' reporter requires the NBenchmark.Console package.");
                            Console.WriteLine("Add the NBenchmark.Console NuGet package and use AddReporter(new ConsoleReporter()).");
                            break;
                        default:
                            Console.WriteLine($"Unknown reporter: '{args[i]}'. Valid: json, markdown, csv (console requires NBenchmark.Console package)");
                            break;
                    }

                    break;
                case "--confidence" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], CultureInfo.InvariantCulture, out var conf)
                        && conf is > 0 and < 1)
                        _options = _options with { ConfidenceLevel = conf };
                    else
                        Console.WriteLine($"Invalid --confidence value '{args[i]}'. Must be a fraction strictly between 0 and 1 (e.g. 0.95).");

                    break;
                case "--order" when i + 1 < args.Length:
                    _runOrder = args[++i]?.ToLowerInvariant() == "declaration"
                        ? RunOrder.Declaration
                        : RunOrder.Random;

                    break;
                case "--threshold-pct" when i + 1 < args.Length:
                    Console.Error.WriteLine(
                        "--threshold-pct is not yet implemented. Remove the flag to continue; "
                        + "the run will exit with code 1 until it ships.");

                    _thresholdRejected = true;
                    i++;
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var seed))
                        _seed = seed;
                    else
                        Console.WriteLine($"Invalid --seed value '{args[i]}'. Must be an integer.");

                    break;
                case "--list":
                    _listOnly = true;
                    break;
                case "--dry-run":
                    _dryRun = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown flag: '{args[i]}'. Use --help to see available options.");
                    Environment.ExitCode = 1;
                    break;
            }
        }
    }
}
