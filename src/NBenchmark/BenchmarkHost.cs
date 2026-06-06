using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NBenchmark;
using NBenchmark.Engine;
using NBenchmark.Discovery;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkHost
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [];
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private RunOrder _runOrder = RunOrder.Random;
    private string? _filter;
    private string? _outputDir;
    private bool _listOnly;
    private bool _dryRun;
    private bool _showHelp;
    private bool _thresholdRejected;
    private int? _seed;

    private BenchmarkHost() { }

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
    { _runOrder = order; return this; }

    public BenchmarkHost WithProgress(IBenchmarkProgress progress)
    { _progress = progress; _progressExplicitlySet = true; return this; }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_showHelp) { PrintHelp(); return Array.Empty<BenchmarkResult>(); }

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
                    Console.WriteLine($"    {b.DisplayName}"
                        + (b.Attribute.Description is not null ? $" — {b.Attribute.Description}" : ""));
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
            totalBenchmarks);

        foreach (var suite in filtered)
        {
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(suite.Type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Error] Could not instantiate {suite.Type.Name} — "
                    + "the type must have a public parameterless constructor, or be internal with "
                    + "a public constructor and InternalsVisibleTo. "
                    + $"Details: {ex.Message}");
                continue;
            }

            // instance is non-null here: the try above either assigns it or `continue`s.
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

                    await _progress.OnWarmupStarting(benchmarkName, options.WarmupIterations);
                    await _progress.OnBenchmarkStarting(benchmarkName, runningIndex, totalBenchmarks);

                    BenchmarkResult result;

                    try
                    {

                        Action? syncAction = null;
                        Func<Task>? asyncAction = null;
                        if (benchmark.AsyncDelegate is not null)
                        {
                            var asyncDel = benchmark.AsyncDelegate;
                            var resultExtractor = benchmark.ResultExtractor;
                            asyncAction = async () =>
                            {
                                var task = asyncDel(typedInstance);
                                await task;

                                if (resultExtractor is not null)
                                {
                                    var resultValue = resultExtractor(task);
                                    if (resultValue is not null)
                                        ResultSink.Consume(resultValue);
                                }
                            };
                        }
                        else
                        {
                            var syncDel = benchmark.SyncDelegate!;
                            syncAction = () =>
                            {
                                var r = syncDel(typedInstance);
                                if (r is not null) ResultSink.Consume(r);
                            };
                        }

                        Action? iterSetup = benchmark.IterationSetupDelegate is not null
                            ? () => benchmark.IterationSetupDelegate(typedInstance)
                            : null;
                        Action? iterTeardown = benchmark.IterationTeardownDelegate is not null
                            ? () => benchmark.IterationTeardownDelegate(typedInstance)
                            : null;

                        if (_dryRun)
                        {
                            if (syncAction is not null) syncAction();
                            else await asyncAction!();
                            result = new BenchmarkResult
                            {
                                Name = benchmarkName,
                                Description = benchmark.Attribute.Description,
                                Mean = 0,
                                Median = 0,
                                P95 = 0,
                                P99 = 0,
                                Min = 0,
                                Max = 0,
                                StandardDeviation = 0,
                                MeanAllocatedBytes = null,
                                PValue = null,
                                IsSignificant = null,
                                Errored = false,
                                ErrorMessage = null,
                                MeasuredIterations = 0,
                                WarmupIterations = 0,
                                RunAt = DateTimeOffset.UtcNow,
                                TotalDuration = TimeSpan.Zero,
                                IsBaseline = benchmark.Attribute.Baseline,
                                OutlierMode = _options.OutlierMode,
                            };
                        }
                        else if (syncAction is not null)
                        {
                            var outcome = MeasurementEngine.MeasureSync(
                                name: benchmarkName,
                                action: syncAction,
                                options: options,
                                description: benchmark.Attribute.Description,
                                isBaseline: benchmark.Attribute.Baseline,
                                iterationSetup: iterSetup,
                                iterationTeardown: iterTeardown,
                                cancellationToken: cancellationToken
                            );
                            result = outcome.Result;
                            rawSamples[benchmarkName] = outcome.RawSamples;
                        }
                        else
                        {
                            var outcome = await MeasurementEngine.MeasureAsync(
                                name: benchmarkName,
                                action: asyncAction!,
                                options: options,
                                description: benchmark.Attribute.Description,
                                isBaseline: benchmark.Attribute.Baseline,
                                iterationSetup: iterSetup,
                                iterationTeardown: iterTeardown,
                                cancellationToken: cancellationToken
                            );
                            result = outcome.Result;
                            rawSamples[benchmarkName] = outcome.RawSamples;
                        }

                        await _progress.OnWarmupCompleted(benchmarkName);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (TargetInvocationException tiex)
                    {
                        var inner = tiex.InnerException ?? tiex;
                        result = BenchmarkResult.CreateErrored(benchmarkName, inner.ToString(),
                            benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                            _options.OutlierMode);
                    }
                    catch (Exception ex)
                    {
                        result = BenchmarkResult.CreateErrored(benchmarkName, ex.ToString(),
                            benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                            _options.OutlierMode);
                    }

                    allResults.Add(result);
                    await _progress.OnBenchmarkCompleted(result);

                    if (_options.ForceGcBetweenBenchmarks)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
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
            }
        }

        await _progress.OnSuiteCompleted(allResults);

        if (_options.EnableSignificance && allResults.Count(r => !r.Errored) > 1)
            Significance.ComputeSignificance(allResults, rawSamples);

        if (!string.IsNullOrEmpty(_outputDir))
            ApplyOutputDirectory(_outputDir);

        foreach (var reporter in _reporters)
            await reporter.ReportAsync(allResults, cancellationToken);

        // Set the exit code only after reporters finish so a reporter failure cannot
        // clobber it. --threshold-pct is deliberately rejected (not silently accepted)
        // to prevent CI scripts from passing for the wrong reason before it ships.
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
        if (_filter is null) return suites;

        return suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b => GlobMatch(_filter,
                        $"{s.Type.Name}.{b.DisplayName}"))
                    .ToList()
            })
            .Where(s => s.Benchmarks.Count > 0)
            .ToList();
    }

    private static bool GlobMatch(string pattern, string input)
    {
        if (pattern == "*") return true;

        var parts = pattern.Split('*');
        if (parts.Length == 0) return true;

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
            if (idx < 0) return false;
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
        Console.WriteLine("  --dry-run              Invoke each benchmark once (skip measurement)");
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
                        Console.WriteLine($"Invalid --iterations value '{args[i]}'. Must be {MeasurementOptions.MinIterations}–{MeasurementOptions.MaxIterations}.");
                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var warmup) && warmup >= 1 && warmup <= MeasurementOptions.MaxWarmupIterations)
                        _options = _options with { WarmupIterations = warmup };
                    else
                        Console.WriteLine($"Invalid --warmup value '{args[i]}'. Must be 1–{MeasurementOptions.MaxWarmupIterations}.");
                    break;
                case "--output" when i + 1 < args.Length:
                    _outputDir = PathValidation.ValidateOutputPath(args[++i]);
                    break;
                case "--reporter" when i + 1 < args.Length:
                    switch (args[++i]?.ToLowerInvariant())
                    {
                        case "json": _reporters.Add(new JsonReporter()); break;
                        case "markdown": _reporters.Add(new MarkdownReporter()); break;
                        case "csv": _reporters.Add(new CsvReporter()); break;
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
                    if (double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var conf)
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
