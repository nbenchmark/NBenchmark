using System.Reflection;
using System.Runtime.InteropServices;
using NBenchmark;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

    private readonly List<(
        string Name,
        Func<Task>? AsyncAction,
        Action? SyncAction,
        Action? Setup,
        Action? Teardown
    )> _benchmarks = [];
    private readonly List<IReporter> _reporters = [];
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private RunOrder _runOrder = RunOrder.Random;
    private string? _baselineName;
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    public BenchmarkSuite Add(string name, Action action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add((name, null, action, setup, teardown));
        return this;
    }

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add((name, action, null, setup, teardown));
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add((name, null, () => ResultSink.Consume(action()), setup, teardown));
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add((name, async () => ResultSink.Consume(await action()), null, setup, teardown));
        return this;
    }

    private void EnsureUniqueName(string name)
    {
        if (_benchmarks.Any(b => b.Name == name))
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique — significance testing keys raw samples by name.",
                nameof(name));
    }

    public BenchmarkSuite WithBaseline(string name)
    { _baselineName = name; return this; }

    public BenchmarkSuite WithIterations(int iterations)
    { _options = _options with { Iterations = iterations }; return this; }

    public BenchmarkSuite WithWarmup(int iterations)
    { _options = _options with { WarmupIterations = iterations }; return this; }

    public BenchmarkSuite WithMemory(bool enabled = true)
    { _options = _options with { MeasureAllocations = enabled }; return this; }

    public BenchmarkSuite WithOutlierMode(OutlierMode mode)
    { _options = _options with { OutlierMode = mode }; return this; }

    public BenchmarkSuite WithConfidenceLevel(double level)
    { _options = _options with { ConfidenceLevel = level }; return this; }

    public BenchmarkSuite WithSignificance(bool enabled)
    { _options = _options with { EnableSignificance = enabled }; return this; }

    public BenchmarkSuite WithRunOrder(RunOrder order)
    { _runOrder = order; return this; }

    public BenchmarkSuite WithSuiteSetup(Action setup)
    { _suiteSetup = setup; return this; }

    public BenchmarkSuite WithSuiteTeardown(Action teardown)
    { _suiteTeardown = teardown; return this; }

    public BenchmarkSuite WithReporter(IReporter reporter)
    { _reporters.Add(reporter); return this; }

    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    { _progress = progress; _progressExplicitlySet = true; return this; }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_progressExplicitlySet)
            _progress = NullBenchmarkProgress.Instance;

        if (_baselineName is not null && !_benchmarks.Any(b => b.Name == _baselineName))
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));

        var ordered = _runOrder == RunOrder.Random
            ? ShuffleBenchmarks(_benchmarks.ToList(), Random.Shared.Next())
            : _benchmarks;

        var results = new List<BenchmarkResult>(ordered.Count);
        var rawSamples = new Dictionary<string, double[]>();
        var total = ordered.Count;
        var index = 0;

        await _progress.OnSuiteStarting(
            ordered.Select(b => b.Name).ToList(), ordered.Count);

        _suiteSetup?.Invoke();

        foreach (var (benchmarkName, asyncAction, syncAction, setup, teardown) in ordered)
        {
            index++;

            await _progress.OnWarmupStarting(benchmarkName, _options.WarmupIterations);
            await _progress.OnBenchmarkStarting(benchmarkName, index, total);

            BenchmarkResult result;

            try
            {
                MeasurementOutcome outcome;
                if (syncAction is not null)
                {
                    outcome = MeasurementEngine.MeasureSync(
                        name: benchmarkName,
                        action: syncAction,
                        options: _options,
                        isBaseline: _baselineName is not null && benchmarkName == _baselineName,
                        iterationSetup: setup,
                        iterationTeardown: teardown,
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    outcome = await MeasurementEngine.MeasureAsync(
                        name: benchmarkName,
                        action: asyncAction!,
                        options: _options,
                        isBaseline: _baselineName is not null && benchmarkName == _baselineName,
                        iterationSetup: setup,
                        iterationTeardown: teardown,
                        cancellationToken: cancellationToken
                    );
                }

                result = outcome.Result;
                rawSamples[benchmarkName] = outcome.RawSamples;

                await _progress.OnWarmupCompleted(benchmarkName);
            }
            catch (OperationCanceledException) { throw; }
            catch (TargetInvocationException tiex)
            {
                var inner = tiex.InnerException ?? tiex;
                var isBaseline = _baselineName is not null && benchmarkName == _baselineName;
                result = BenchmarkResult.CreateErrored(benchmarkName, inner.ToString(),
                    isBaseline: isBaseline, outlierMode: _options.OutlierMode);
            }
            catch (Exception ex)
            {
                var isBaseline = _baselineName is not null && benchmarkName == _baselineName;
                result = BenchmarkResult.CreateErrored(benchmarkName, ex.ToString(),
                    isBaseline: isBaseline, outlierMode: _options.OutlierMode);
            }

            results.Add(result);
            await _progress.OnBenchmarkCompleted(result);

            if (_options.ForceGcBetweenBenchmarks)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }

        await _progress.OnSuiteCompleted(results);

        _suiteTeardown?.Invoke();

        if (_options.EnableSignificance && results.Any(r => !r.Errored) && results.Count > 1)
            Significance.ComputeSignificance(results, rawSamples);

        foreach (var reporter in _reporters)
            await reporter.ReportAsync(results, cancellationToken);

        return results;
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
}
