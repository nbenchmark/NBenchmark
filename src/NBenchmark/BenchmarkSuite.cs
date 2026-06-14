using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;
using System.Runtime.CompilerServices;

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
    private ReportDetail _detail;

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
            reporter.Detail = detail;
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
            _progress = new DefaultConsoleProgress();

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

        List<BenchmarkResult> results;
        Dictionary<string, double[]> rawSamples;

        try
        {
            (results, rawSamples) = await SuiteRunner.RunAsync(
                envelopes, _runOrder, null, _options, 0,
                _benchmarks.Count, _progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Teardown is guaranteed once setup has succeeded - including on
            // cancellation - mirroring host mode's per-class lifecycle.
            _suiteTeardown?.Invoke();
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        Significance.ApplyIfEnabled(results, rawSamples, _options);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    ///     Runs each benchmark in this suite in a dedicated child process.
    /// </summary>
    /// <remarks>
    ///     In isolated suite mode, suite setup/teardown execute inside each benchmark's
    ///     child process rather than once for the whole suite.
    ///     <para>
    ///         If multiple <c>RunIsolated*</c> callsites execute on the same child startup
    ///         path, only the requested invocation runs as the isolated target. Non-target
    ///         callsites still execute in-process in that child CLR and can influence that
    ///         child's runtime state.
    ///     </para>
    /// </remarks>
    public Task<IReadOnlyList<BenchmarkResult>> RunIsolatedAsync(
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => IsolatedRunContext.WithCurrentRequestAsync(async () =>
        {
            var invocationOrdinal = IsolatedRunContext.NextInvocationOrdinal(IsolatedRunMode.Suite);

            if (IsolatedRunContext.IsRequestMatch(
                    IsolatedRunMode.Suite,
                    invocationOrdinal,
                    callerFilePath,
                    callerLineNumber,
                    callerMemberName,
                    suiteName: Name))
            {
                return await RunRequestedIsolatedChildAsync(cancellationToken).ConfigureAwait(false);
            }

            if (IsolatedRunContext.IsActive)
            {
                if (IsolatedRunContext.IsRequestedInvocation(IsolatedRunMode.Suite, invocationOrdinal)
                    && IsolatedRunContext.TryGetActiveRequest(out var request))
                {
                    var mismatch = IsolatedRunContext.BuildCallsiteMismatchOutcome(
                        Name,
                        IsolatedRunContext.ResolveOptions(_options),
                        request,
                        callerFilePath,
                        callerLineNumber,
                        callerMemberName,
                        invocationOrdinal,
                        Name);

                    await IsolatedRunContext.WriteChildOutcomeIfRequestedAsync(mismatch, cancellationToken)
                        .ConfigureAwait(false);

                    return (IReadOnlyList<BenchmarkResult>)[mismatch.Result];
                }

                return await RunAsync(cancellationToken).ConfigureAwait(false);
            }

            return await RunIsolatedEntryAsync(
                    callerFilePath,
                    callerLineNumber,
                    callerMemberName,
                    invocationOrdinal,
                    cancellationToken)
                .ConfigureAwait(false);
        });

    /// <summary>
    ///     Synchronous wrapper for <see cref="RunIsolatedAsync" />.
    /// </summary>
    /// <remarks>
    ///     This synchronous overload blocks the calling thread until all child processes
    ///     complete.
    /// </remarks>
    public IReadOnlyList<BenchmarkResult> RunIsolated(
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerMemberName = "")
        => RunIsolatedAsync(cancellationToken, callerFilePath, callerLineNumber, callerMemberName).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<BenchmarkResult>> RunIsolatedEntryAsync(
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        int invocationOrdinal,
        CancellationToken cancellationToken)
    {
        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        if (_baselineName is not null && !_benchmarks.Any(b => b.Name == _baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));
        }

        var envelopeNames = _benchmarks.Select(b => b.Name).ToList();
        await _progress.OnSuiteStarting(envelopeNames, _benchmarks.Count).ConfigureAwait(false);

        var envelopes = _benchmarks
            .Select(b => b with { IsBaseline = _baselineName is not null && b.Name == _baselineName })
            .ToList();

        List<BenchmarkResult> results;
        Dictionary<string, double[]> rawSamples;

        (results, rawSamples) = await SuiteRunner.RunIsolatedAsync(
            envelopes,
            _runOrder,
            _options,
            0,
            _benchmarks.Count,
            _progress,
            Name,
            callerFilePath,
            callerLineNumber,
            callerMemberName,
            invocationOrdinal,
            cancellationToken).ConfigureAwait(false);

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        Significance.ApplyIfEnabled(results, rawSamples, _options);

        foreach (var reporter in _reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunRequestedIsolatedChildAsync(CancellationToken cancellationToken)
    {
        if (!IsolatedRunContext.TryGetActiveRequest(out var request))
            return await RunAsync(cancellationToken).ConfigureAwait(false);

        var options = IsolatedRunContext.ResolveOptions(_options);
        var envelope = _benchmarks.FirstOrDefault(b => b.Name == request.BenchmarkName);

        if (envelope is null)
        {
            var message = $"Isolated suite benchmark '{request.BenchmarkName}' was not found in suite '{Name}'.";

            var missingOutcome = OutcomeBuilder.Build(
                new RunOutcome.Errored(new InvalidOperationException(message), message),
                request.BenchmarkName,
                description: null,
                isBaseline: false,
                options,
                TimeSpan.Zero,
                TimeSpan.Zero);

            await IsolatedRunContext.WriteChildOutcomeIfRequestedAsync(missingOutcome, cancellationToken)
                .ConfigureAwait(false);

            return [missingOutcome.Result];
        }

        var isBaseline = _baselineName is not null && envelope.Name == _baselineName;

        var spec = new RunSpec
        {
            Options = options,
            Description = envelope.Description,
            IsBaseline = isBaseline,
            Progress = NullBenchmarkProgress.Instance,
        };

        MeasurementOutcome outcome;

        _suiteSetup?.Invoke();

        try
        {
            try
            {
                outcome = await envelope.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = OutcomeBuilder.Build(
                    new RunOutcome.Errored(ex),
                    envelope.Name,
                    envelope.Description,
                    isBaseline,
                    spec.Options,
                    TimeSpan.Zero,
                    TimeSpan.Zero);
            }
        }
        finally
        {
            _suiteTeardown?.Invoke();
        }

        await IsolatedRunContext.WriteChildOutcomeIfRequestedAsync(outcome, cancellationToken)
            .ConfigureAwait(false);

        return [outcome.Result];
    }
}
