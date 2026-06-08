using System.Diagnostics;
using System.Reflection;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkHost
{
    private CliArgs _cliArgs = new();
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [];
    private MeasurementOptions _options = MeasurementOptions.Default;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private Func<Type, object>? _instanceFactory;
    private Action? _postSuiteCleanup;

    private BenchmarkHost()
    {
    }

    public static BenchmarkHost Create(string[] args)
    {
        var cliArgs = CliArgs.Parse(args);
        var host = new BenchmarkHost();
        host._cliArgs = cliArgs;
        host._reporters.InsertRange(0, cliArgs.CliReporters);
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
        if (_cliArgs.ShowHelp)
        {
            CliArgs.PrintHelp();
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

        var filtered = FilterSuites(allSuites, _cliArgs.Filter);

        if (_cliArgs.ListOnly)
        {
            foreach (var suite in filtered)
            {
                Console.WriteLine($"── {suite.Type.Name} ──");

                foreach (var b in suite.Benchmarks)
                {
                    Console.WriteLine($"    {b.DisplayName}"
                                      + (b.Attribute.Description is not null ? $" - {b.Attribute.Description}" : ""));
                }
            }

            return Array.Empty<BenchmarkResult>();
        }

        if (!_progressExplicitlySet)
            _progress = NullBenchmarkProgress.Instance;

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

        var runningIndex = 0;

        foreach (var suite in filtered)
        {
            var instance = PerClassLifecycle.TryCreateInstance(suite.Type, _instanceFactory);
            if (instance is null)
                continue;

            var instanceFromFactory = _instanceFactory is not null;

            try
            {
                var (setupSuccess, setupErrors) = PerClassLifecycle.TryRunSetup(suite, instance, suiteOptions);
                if (!setupSuccess)
                {
                    allResults.AddRange(setupErrors!);
                    continue;
                }

                var envelopes = suite.Benchmarks
                    .Select(b => BenchmarkEnvelope.FromDiscovered(b, suite.Type.Name, instance))
                    .ToList();

                var (results, samples) = await SuiteRunner.RunAsync(
                    envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                    runningIndex, totalBenchmarks, _progress, cancellationToken).ConfigureAwait(false);

                runningIndex += suite.Benchmarks.Count;

                allResults.AddRange(results);
                foreach (var kvp in samples)
                    rawSamples[kvp.Key] = kvp.Value;
            }
            finally
            {
                await PerClassLifecycle.RunTeardown(suite, instance, instanceFromFactory, _postSuiteCleanup);
            }
        }

        await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

        Significance.ApplyIfEnabled(allResults, rawSamples, suiteOptions);

        if (!string.IsNullOrEmpty(_cliArgs.OutputDir))
            ApplyOutputDirectory(_cliArgs.OutputDir);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(allResults, cancellationToken).ConfigureAwait(false);
        }

        if (_cliArgs.ThresholdRejected)
            Environment.ExitCode = 1;

        return allResults;
    }

    private void ApplyOutputDirectory(string outputDir)
    {
        for (var i = 0; i < _reporters.Count; i++)
        {
            if (ReporterRegistry.TryCreate(_reporters[i].Name, outputDir, out var rebuilt))
                _reporters[i] = rebuilt;
        }
    }

    private static IReadOnlyList<BenchmarkSuiteDefinition> FilterSuites(
        IReadOnlyList<BenchmarkSuiteDefinition> suites, string? filter)
    {
        if (filter is null)
            return suites;

        return suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b => GlobMatcher.Match(filter,
                        $"{s.Type.Name}.{b.DisplayName}"))
                    .ToList(),
            })
            .Where(s => s.Benchmarks.Count > 0)
            .ToList();
    }

    private static MeasurementOptions MergeCliOptions(MeasurementOptions options, CliArgs cliArgs)
    {
        var result = options;
        if (cliArgs.Iterations.HasValue)
            result = result with { Iterations = cliArgs.Iterations.Value };
        if (cliArgs.WarmupIterations.HasValue)
            result = result with { WarmupIterations = cliArgs.WarmupIterations.Value };
        if (cliArgs.ConfidenceLevel.HasValue)
            result = result with { ConfidenceLevel = cliArgs.ConfidenceLevel.Value };
        return result;
    }
}
