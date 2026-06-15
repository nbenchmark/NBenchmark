using System.Runtime.CompilerServices;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEnvelope> _benchmarks = [];

    private readonly List<IReporter> _reporters = [];
    private string? _baselineName;
    private ReportDetail _detail;
    private bool _isolated;
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

    private BenchmarkSuite AddEnvelope(
        string name,
        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync)
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

    /// <summary>
    ///     Uses a custom <see cref="IOutlierDetector" /> for trimming, overriding
    ///     <see cref="WithOutlierMode" />. Pass one of the built-ins from
    ///     <see cref="OutlierDetectors" /> or your own implementation.
    /// </summary>
    public BenchmarkSuite WithOutlierDetector(IOutlierDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _options = _options with { OutlierDetector = detector };
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

    public BenchmarkSuite WithSignificanceLevel(double level)
    {
        _options = _options with { SignificanceLevel = level };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="ISignificanceTest" /> strategy, overriding the engine
    ///     default (Mann-Whitney U for two groups, Kruskal-Wallis for three or more). Pass
    ///     one of the built-ins from <see cref="NBenchmark.Stats" /> or your own implementation.
    /// </summary>
    public BenchmarkSuite WithSignificanceTest(ISignificanceTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        _options = _options with { SignificanceTest = test };
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
        reporter.Detail = _detail;
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkSuite WithDetail(ReportDetail detail)
    {
        _detail = detail;

        foreach (var reporter in _reporters)
        {
            reporter.Detail = detail;
        }

        return this;
    }

    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    /// <summary>
    ///     Runs the whole suite in a dedicated child process for a clean-room reading,
    ///     rather than in the current process. The suite's setup, every benchmark, and the
    ///     suite's teardown all execute together in that one child; the parent process
    ///     reads the per-benchmark samples back and computes significance and reports as
    ///     usual. Defaults to enabled when called with no argument.
    /// </summary>
    public BenchmarkSuite WithIsolation(bool enabled = true)
    {
        _isolated = enabled;
        return this;
    }

    /// <summary>
    ///     Runs every benchmark in the suite and returns their results. When
    ///     <see cref="WithIsolation" /> is enabled the suite runs in a dedicated child
    ///     process; otherwise it runs in the current process.
    /// </summary>
    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => IsolatedRunContext.WithCurrentRequestAsync(() =>
            RunCoreAsync(callerFilePath, callerLineNumber, callerMemberName, cancellationToken));

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        ValidateBaseline();

        var invocationOrdinal = IsolatedRunContext.NextSuiteInvocationOrdinal();

        // Inside an isolated child: run the suite in-process and quietly. Only the suite
        // call the parent requested writes its samples back; any other suite call sharing
        // this child runs without emitting output or a payload.
        if (IsolatedRunContext.IsActive)
        {
            var isTarget = IsolatedRunContext.IsSuiteRequestMatch(
                invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, Name);

            return await RunInProcessCoreAsync(
                NullBenchmarkProgress.Instance,
                RunOrder.Declaration,
                applySignificance: false,
                applyReporters: false,
                writeChildPayload: isTarget,
                cancellationToken).ConfigureAwait(false);
        }

        if (_isolated)
        {
            return await RunIsolatedParentAsync(
                invocationOrdinal, callerFilePath, callerLineNumber, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        return await RunInProcessCoreAsync(
            _progress,
            _runOrder,
            applySignificance: true,
            applyReporters: true,
            writeChildPayload: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunInProcessCoreAsync(
        IBenchmarkProgress progress,
        RunOrder order,
        bool applySignificance,
        bool applyReporters,
        bool writeChildPayload,
        CancellationToken cancellationToken)
    {
        var envelopeNames = _benchmarks.Select(b => b.Name).ToList();
        await progress.OnSuiteStarting(envelopeNames, _benchmarks.Count).ConfigureAwait(false);

        _suiteSetup?.Invoke();

        var envelopes = _benchmarks
            .Select(b => b with { IsBaseline = _baselineName is not null && b.Name == _baselineName })
            .ToList();

        List<BenchmarkResult> results;
        Dictionary<string, double[]> rawSamples;

        try
        {
            (results, rawSamples) = await SuiteRunner.RunAsync(
                envelopes, order, null, _options, 0,
                _benchmarks.Count, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Teardown is guaranteed once setup has succeeded - including on
            // cancellation - mirroring host mode's per-class lifecycle.
            _suiteTeardown?.Invoke();
        }

        await progress.OnSuiteCompleted(results).ConfigureAwait(false);

        if (writeChildPayload)
        {
            await IsolatedRunContext.WriteChildPayloadIfRequestedAsync(results, rawSamples, cancellationToken)
                .ConfigureAwait(false);
        }

        if (applySignificance)
            Significance.ApplyIfEnabled(results, rawSamples, _options);

        if (applyReporters)
        {
            foreach (var reporter in _reporters)
            {
                await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunIsolatedParentAsync(
        int invocationOrdinal,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        var displayNames = _benchmarks.Select(b => b.Name).ToList();
        await _progress.OnSuiteStarting(displayNames, _benchmarks.Count).ConfigureAwait(false);

        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            InvocationOrdinal = invocationOrdinal,
            CallerFilePath = callerFilePath,
            CallerLineNumber = callerLineNumber,
            CallerMemberName = callerMemberName,
            SuiteName = Name,
            BenchmarkDisplayNames = displayNames,
        };

        var items = await ChildProcessLauncher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var byName = items.ToDictionary(item => item.Result.Name, StringComparer.Ordinal);

        var results = new List<BenchmarkResult>(_benchmarks.Count);
        var rawSamples = new Dictionary<string, double[]>(_benchmarks.Count);

        for (var i = 0; i < _benchmarks.Count; i++)
        {
            var envelope = _benchmarks[i];
            var isBaseline = _baselineName is not null && envelope.Name == _baselineName;

            await _progress.OnBenchmarkStarting(envelope.Name, i + 1, _benchmarks.Count).ConfigureAwait(false);

            BenchmarkResult result;
            double[] raw;

            if (byName.TryGetValue(envelope.Name, out var item))
            {
                result = item.Result with { IsBaseline = isBaseline, Description = envelope.Description };
                raw = item.RawSamples;
            }
            else
            {
                var message = $"Isolated child did not return a result for '{envelope.Name}'.";

                result = OutcomeBuilder.Build(
                    new RunOutcome.Errored(new InvalidOperationException(message), message),
                    envelope.Name, envelope.Description, isBaseline,
                    _options, TimeSpan.Zero, TimeSpan.Zero).Result;

                raw = [];
            }

            results.Add(result);
            rawSamples[envelope.Name] = raw;

            await _progress.OnBenchmarkCompleted(result).ConfigureAwait(false);
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        Significance.ApplyIfEnabled(results, rawSamples, _options);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private void ValidateBaseline()
    {
        if (_baselineName is not null && _benchmarks.All(b => b.Name != _baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));
        }
    }
}
