using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkHost
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [];
    private CliArgs _cliArgs = new();
    private Func<Type, object>? _instanceFactory;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private ReportDetail _detail;

    private BenchmarkHost()
    {
    }

    internal Action? PostSuiteCleanup { get; set; }

    public static BenchmarkHost Create(string[] args)
    {
        var cliArgs = CliArgs.Parse(args);
        var host = new BenchmarkHost();
        host._cliArgs = cliArgs;
        host._detail = cliArgs.Detail;
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
        reporter.Detail = _detail;
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

    public BenchmarkHost WithDetail(ReportDetail detail)
    {
        _detail = detail;
        foreach (var reporter in _reporters)
            reporter.Detail = detail;
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

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_cliArgs.ShowHelp)
        {
            CliArgs.PrintHelp();
            return Array.Empty<BenchmarkResult>();
        }

        if (_cliArgs.IsolatedRun is not null)
            return await RunIsolatedChildAsync(cancellationToken).ConfigureAwait(false);

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
            var inProcess = suite.Benchmarks.Where(b => !b.IsolatedProcess).ToList();
            var isolated = suite.Benchmarks.Where(b => b.IsolatedProcess).ToList();

            if (inProcess.Count > 0)
            {
                await RunInProcessSuiteAsync(
                    suite with { Benchmarks = inProcess }, suiteOptions, runningIndex, totalBenchmarks,
                    allResults, rawSamples, cancellationToken).ConfigureAwait(false);
            }

            runningIndex += inProcess.Count;

            foreach (var benchmark in isolated)
            {
                var name = $"{suite.Type.Name}.{benchmark.DisplayName}";

                await _progress.OnBenchmarkStarting(name, runningIndex + 1, totalBenchmarks).ConfigureAwait(false);

                var outcome = _cliArgs.DryRun
                    ? OutcomeBuilder.Build(
                        new RunOutcome.DryRun(), name, benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                        suiteOptions, TimeSpan.Zero, TimeSpan.Zero)
                    : await IsolatedProcessRunner.RunAsync(name, BuildIsolatedChildArgs(_cliArgs), cancellationToken)
                        .ConfigureAwait(false);

                allResults.Add(outcome.Result);
                rawSamples[name] = outcome.RawSamples;

                await _progress.OnBenchmarkCompleted(outcome.Result).ConfigureAwait(false);

                runningIndex++;
            }
        }

        await _progress.OnSuiteCompleted(allResults).ConfigureAwait(false);

        Significance.ApplyIfEnabled(allResults, rawSamples, suiteOptions);

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

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(allResults, cancellationToken).ConfigureAwait(false);
        }

        return allResults;
    }

    private async Task RunInProcessSuiteAsync(
        BenchmarkSuiteDefinition suite,
        MeasurementOptions suiteOptions,
        int startIndex,
        int totalBenchmarks,
        List<BenchmarkResult> allResults,
        Dictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        var instance = PerClassLifecycle.TryCreateInstance(suite.Type, _instanceFactory);

        if (instance is null)
            return;

        var instanceFromFactory = _instanceFactory is not null;

        try
        {
            var (setupSuccess, setupErrors) = PerClassLifecycle.TryRunSetup(suite, instance, suiteOptions);

            if (!setupSuccess)
            {
                allResults.AddRange(setupErrors!);
                return;
            }

            var envelopes = suite.Benchmarks
                .Select(b => BenchmarkEnvelope.FromDiscovered(b, suite.Type.Name, instance))
                .ToList();

            var (results, samples) = await SuiteRunner.RunAsync(
                envelopes, _cliArgs.RunOrder ?? _runOrder, _cliArgs.Seed, suiteOptions,
                startIndex, totalBenchmarks, _progress, cancellationToken).ConfigureAwait(false);

            allResults.AddRange(results);

            foreach (var kvp in samples)
            {
                rawSamples[kvp.Key] = kvp.Value;
            }
        }
        finally
        {
            await PerClassLifecycle.RunTeardown(suite, instance, instanceFromFactory, PostSuiteCleanup);
        }
    }

    /// <summary>
    ///     Child-process entry point: run exactly one benchmark (identified by full name)
    ///     in this fresh CLR and write its serialized outcome to the output file the parent
    ///     supplied. No banner, progress, or reporters - the parent owns presentation.
    /// </summary>
    private async Task<IReadOnlyList<BenchmarkResult>> RunIsolatedChildAsync(CancellationToken cancellationToken)
    {
        var fullName = _cliArgs.IsolatedRun!;
        var options = _cliArgs.DryRun
            ? _options with { Iterations = 0, WarmupIterations = 0 }
            : MergeCliOptions(_options, _cliArgs);

        var discoverer = new BenchmarkDiscoverer();

        foreach (var suite in _assemblies.SelectMany(discoverer.Discover))
        {
            var match = suite.Benchmarks
                .FirstOrDefault(b => $"{suite.Type.Name}.{b.DisplayName}" == fullName);

            if (match is null)
                continue;

            var instance = PerClassLifecycle.TryCreateInstance(suite.Type, _instanceFactory);

            if (instance is null)
            {
                Environment.ExitCode = 1;
                return Array.Empty<BenchmarkResult>();
            }

            var instanceFromFactory = _instanceFactory is not null;
            MeasurementOutcome outcome;

            try
            {
                var singleSuite = suite with { Benchmarks = [match] };
                var (setupSuccess, setupErrors) = PerClassLifecycle.TryRunSetup(singleSuite, instance, options);

                if (!setupSuccess)
                {
                    outcome = new MeasurementOutcome { Result = setupErrors![0], RawSamples = [] };
                }
                else
                {
                    var envelope = BenchmarkEnvelope.FromDiscovered(match, suite.Type.Name, instance);

                    var (results, samples) = await SuiteRunner.RunAsync(
                        [envelope], RunOrder.Declaration, _cliArgs.Seed, options,
                        0, 1, NullBenchmarkProgress.Instance, cancellationToken).ConfigureAwait(false);

                    var result = results[0];
                    var raw = samples.TryGetValue(result.Name, out var rs) ? rs : [];
                    outcome = new MeasurementOutcome { Result = result, RawSamples = raw };
                }
            }
            finally
            {
                await PerClassLifecycle.RunTeardown(suite, instance, instanceFromFactory, PostSuiteCleanup);
            }

            if (_cliArgs.IsolatedOutput is not null)
                await IsolatedProcessRunner.WriteResultAsync(_cliArgs.IsolatedOutput, outcome, cancellationToken).ConfigureAwait(false);

            return [outcome.Result];
        }

        Console.Error.WriteLine($"Isolated benchmark '{fullName}' was not found.");
        Environment.ExitCode = 1;
        return Array.Empty<BenchmarkResult>();
    }

    private void ApplyOutputDirectory(string outputDir)
    {
        for (var i = 0; i < _reporters.Count; i++)
        {
            if (ReporterRegistry.TryCreate(_reporters[i].Name, outputDir, _detail, out var rebuilt))
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

        if (cliArgs.Alpha.HasValue)
            result = result with { SignificanceLevel = cliArgs.Alpha.Value };

        return result;
    }

    /// <summary>
    ///     Builds the minimal argument list forwarded to an isolated child process.
    ///     Only the flags the child actually consumes (those that shape the measurement)
    ///     are reconstructed from the parsed args - never the raw user args. Forwarding
    ///     presentation/discovery flags such as <c>--reporter</c>, <c>--output</c>, or
    ///     <c>--filter</c> would risk the child failing to re-parse them (for example a
    ///     reporter assembly that is not loaded in the child) and exiting with a non-zero
    ///     code, which the parent would surface as a misleading benchmark error.
    /// </summary>
    private static List<string> BuildIsolatedChildArgs(CliArgs cliArgs)
    {
        var args = new List<string>();

        if (cliArgs.Iterations.HasValue)
        {
            args.Add("--iterations");
            args.Add(cliArgs.Iterations.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (cliArgs.WarmupIterations.HasValue)
        {
            args.Add("--warmup");
            args.Add(cliArgs.WarmupIterations.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (cliArgs.ConfidenceLevel.HasValue)
        {
            args.Add("--confidence");
            args.Add(cliArgs.ConfidenceLevel.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (cliArgs.Alpha.HasValue)
        {
            args.Add("--alpha");
            args.Add(cliArgs.Alpha.Value.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }
}
