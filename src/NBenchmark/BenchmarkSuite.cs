using NBenchmark.Engine;
using NBenchmark.Reporters;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEnvelope> _benchmarks = [];

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
        => AddEnvelope(name, (spec, ct) =>
            Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null)
        => AddEnvelope(name, async (spec, ct) =>
            await BenchmarkRunner.Instance.RunAsync(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null)
        => AddEnvelope(name, (spec, ct) =>
            Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)));

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null)
        => AddEnvelope(name, async (spec, ct) =>
            await BenchmarkRunner.Instance.RunAsync(name, action,
                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false));

    private BenchmarkSuite AddEnvelope(string name, Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync)
    {
        EnsureUniqueName(name);
        _benchmarks.Add(new BenchmarkEnvelope(name, null, false, runAsync));
        return this;
    }

    private void EnsureUniqueName(string name)
    {
        if (_benchmarks.Any(b => b.Name == name))
        {
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique - significance testing keys raw samples by name.",
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

    public BenchmarkSuite WithAllocations(bool enabled = true)
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

        var envelopeNames = _benchmarks.Select(b => b.Name).ToList();
        await _progress.OnSuiteStarting(envelopeNames, _benchmarks.Count).ConfigureAwait(false);

        _suiteSetup?.Invoke();

        var envelopes = _benchmarks
            .Select(b => b with { IsBaseline = _baselineName is not null && b.Name == _baselineName })
            .ToList();

        var (results, rawSamples) = await SuiteRunner.RunAsync(
            envelopes, _runOrder, null, _options, 0,
            _benchmarks.Count, _progress, cancellationToken).ConfigureAwait(false);

        _suiteTeardown?.Invoke();

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        Significance.ApplyIfEnabled(results, rawSamples, _options);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
