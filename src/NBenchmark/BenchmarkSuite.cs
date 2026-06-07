using System.Runtime.InteropServices;
using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEntry> _benchmarks = [];

    private readonly List<IReporter> _reporters = [];
    private string? _baselineName;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

    public BenchmarkSuite Add(string name, Action action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEntry
        {
            Name = name,
            Setup = setup,
            Teardown = teardown,
            RunAsync = (spec, ct) => Task.FromResult(
                BenchmarkRunner.Instance.Run(name, action, spec, ct)),
        });
        return this;
    }

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEntry
        {
            Name = name,
            Setup = setup,
            Teardown = teardown,
            RunAsync = (spec, ct) =>
                BenchmarkRunner.Instance.RunAsync(name, action, spec, ct),
        });
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEntry
        {
            Name = name,
            Setup = setup,
            Teardown = teardown,
            RunAsync = (spec, ct) => Task.FromResult(
                BenchmarkRunner.Instance.Run(name, action, spec, ct)),
        });
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEntry
        {
            Name = name,
            Setup = setup,
            Teardown = teardown,
            RunAsync = (spec, ct) =>
                BenchmarkRunner.Instance.RunAsync(name, action, spec, ct),
        });
        return this;
    }

    private void EnsureUniqueName(string name)
    {
        if (_benchmarks.Any(b => b.Name == name))
        {
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique — significance testing keys raw samples by name.",
                nameof(name));
        }
    }

    public BenchmarkSuite WithBaseline(string name)
    {
        _baselineName = name;
        return this;
    }

    public BenchmarkSuite WithIterations(int iterations)
    {
        _options = _options with { Iterations = iterations };
        return this;
    }

    public BenchmarkSuite WithWarmup(int iterations)
    {
        _options = _options with { WarmupIterations = iterations };
        return this;
    }

    public BenchmarkSuite WithMemory(bool enabled = true)
    {
        _options = _options with { MeasureAllocations = enabled };
        return this;
    }

    public BenchmarkSuite WithOutlierMode(OutlierMode mode)
    {
        _options = _options with { OutlierMode = mode };
        return this;
    }

    public BenchmarkSuite WithConfidenceLevel(double level)
    {
        _options = _options with { ConfidenceLevel = level };
        return this;
    }

    public BenchmarkSuite WithSignificance(bool enabled)
    {
        _options = _options with { EnableSignificance = enabled };
        return this;
    }

    public BenchmarkSuite WithRunOrder(RunOrder order)
    {
        _runOrder = order;
        return this;
    }

    public BenchmarkSuite WithSuiteSetup(Action setup)
    {
        _suiteSetup = setup;
        return this;
    }

    public BenchmarkSuite WithSuiteTeardown(Action teardown)
    {
        _suiteTeardown = teardown;
        return this;
    }

    public BenchmarkSuite WithReporter(IReporter reporter)
    {
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_progressExplicitlySet)
            _progress = NullBenchmarkProgress.Instance;

        if (_baselineName is not null && !_benchmarks.Any(b => b.Name == _baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));
        }

        var ordered = _runOrder == RunOrder.Random
            ? ShuffleBenchmarks(_benchmarks.ToList(), Random.Shared.Next())
            : _benchmarks;

        var results = new List<BenchmarkResult>(ordered.Count);
        var rawSamples = new Dictionary<string, double[]>();
        var total = ordered.Count;
        var index = 0;

        await _progress.OnSuiteStarting(
            ordered.Select(b => b.Name).ToList(), total).ConfigureAwait(false);

        _suiteSetup?.Invoke();

        foreach (var entry in ordered)
        {
            index++;

            await _progress.OnBenchmarkStarting(entry.Name, index, total).ConfigureAwait(false);

            var spec = new RunSpec
            {
                Options = _options,
                IsBaseline = _baselineName is not null && entry.Name == _baselineName,
                IterationSetup = entry.Setup,
                IterationTeardown = entry.Teardown,
                Progress = _progress,
            };

            var outcome = await entry.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            results.Add(outcome.Result);
            rawSamples[entry.Name] = outcome.RawSamples;

            await _progress.OnBenchmarkCompleted(outcome.Result).ConfigureAwait(false);

            if (_options.ForceGcBetweenBenchmarks)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        _suiteTeardown?.Invoke();

        if (_options.EnableSignificance && results.Any(r => !r.Errored) && results.Count > 1)
            Significance.ComputeSignificance(results, rawSamples);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private readonly record struct BenchmarkEntry(
        string Name,
        Action? Setup,
        Action? Teardown,
        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> RunAsync);

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
