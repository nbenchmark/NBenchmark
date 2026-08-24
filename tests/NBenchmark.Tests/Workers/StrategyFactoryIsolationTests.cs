using System.Reflection;
using NBenchmark.Stats;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A custom statistical strategy needing constructor arguments no longer costs the run its
///     isolation.
/// </summary>
/// <remarks>
///     <para>
///         Only a type name used to cross the boundary, which reaches a parameterless constructor and
///         nothing else. A configured detector - <c>new KeepFastest(0.9)</c> - could not be rebuilt, and
///         the coordinator correctly declined to isolate rather than let the worker silently substitute
///         the built-in one and score the results under a method nobody chose. The cost was that
///         <c>samples/ExtensibleStats</c>' whole second suite ran in the host process.
///     </para>
///     <para>
///         A factory is addressable, so the worker runs it and gets the caller's own object with its own
///         arguments. These tests prove both halves: that the run is isolated, and that the strategy that
///         actually scored it was the caller's.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class StrategyFactoryIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public StrategyFactoryIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithIterations(16)
        .WithWarmup(1)
        .WithOpsPerSample(1)
        .WithAutoTune(AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        });

    /// <summary>
    ///     A configured detector supplied as a factory keeps the suite isolated, and the detector the
    ///     worker used is the caller's.
    /// </summary>
    /// <remarks>
    ///     The name assertion is what makes this more than a status check. <c>OutlierDetector.Name</c>
    ///     travels back on the result, and <c>TrimFraction</c> is encoded into it - so a worker that had
    ///     fallen back to the built-in detector, or built one with a default argument, would report a
    ///     different name. That is the silent substitution the refusal existed to prevent, now asserted
    ///     absent rather than avoided.
    /// </remarks>
    [Fact]
    public async Task ConfiguredDetector_AsFactory_IsIsolated_AndUsesTheCallersDetector()
    {
        var results = await Fast(new BenchmarkSuite("detector-factory")
                .Add("a", () => Thread.SpinWait(2_000))
                .Add("b", () => Thread.SpinWait(4_000))
                .WithOutlierDetector(static () => new TrimFractionDetector(0.25)))
            .RunAsync();

        Assert.Equal(2, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

            Assert.Equal(TrimFractionDetector.NameFor(0.25), result.OutlierDetector);
        }
    }

    /// <summary>
    ///     The same detector passed as a live instance still refuses, so the contrast is the factory and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     This is the control. Without it, the test above could pass because something unrelated started
    ///     isolating configured detectors, and the factory would be carrying no weight.
    /// </remarks>
    [Fact]
    public async Task ConfiguredDetector_AsInstance_StillRefuses()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("detector-instance")
                    .Add("a", () => Thread.SpinWait(2_000))
                    .WithOutlierDetector(new TrimFractionDetector(0.25)))
                .WithRequireIsolation(false)
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.All(results, r => Assert.NotEqual(IsolationStatus.Isolated, r.IsolationStatus));
        Assert.Contains("parameterless constructor", stderr.ToString());
    }

    /// <summary>
    ///     A capturing factory is refused, and says so as a capture.
    /// </summary>
    [Fact]
    public async Task DetectorFactory_ThatCaptures_IsIsolated_AndUsesTheCapturedArgument()
    {
        var fraction = 0.25;

        var results = await Fast(new BenchmarkSuite("captured-detector")
                .Add("a", () => Thread.SpinWait(2_000))
                .WithOutlierDetector(() => new TrimFractionDetector(fraction)))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

        // The captured fraction reached the worker: the detector encodes it into the name it reports,
        // so a run that had rebuilt the detector from a default would say something else here.
        Assert.Equal(TrimFractionDetector.NameFor(fraction), result.OutlierDetector);
    }

    /// <summary>
    ///     A configured significance test supplied as a factory keeps the suite isolated.
    /// </summary>
    [Fact]
    public async Task ConfiguredSignificanceTest_AsFactory_IsIsolated()
    {
        var results = await Fast(new BenchmarkSuite("significance-factory")
                .Add("a", () => Thread.SpinWait(2_000))
                .Add("b", () => Thread.SpinWait(8_000))
                .WithBaseline("a")
                .WithSignificanceTest(static () => new AlwaysSignificantTest(0.001)))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        // The caller's test ran in the worker: it reports a p-value nothing else would produce.
        var candidate = results.Single(r => r.Name == "b");
        Assert.Equal(0.001, candidate.PValue);
    }

    /// <summary>
    ///     A factory that is perfectly addressable and fails once it runs in the worker leaves the
    ///     substitution on the results.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The coordinator can only check that a factory can be <i>found</i>; whether it works is
    ///         not knowable until it runs in the process that measures. When it does not, the group is
    ///         still measurable and the engine falls back to its built-in detector - which is the right
    ///         call, and used to be announced on stderr and nowhere else.
    ///     </para>
    ///     <para>
    ///         That is not loud, it just looks it. The coordinator redirects worker stderr into a
    ///         rolling buffer read only on the worker-died and timed-out paths, so a group that
    ///         completed normally threw the warning away and reported an ordinary isolated row. What
    ///         the reader was owed is on the row: these numbers were scored by a method they did not
    ///         choose.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task DetectorFactory_ThatFailsInTheWorker_WarnsOnEveryResult()
    {
        var results = await Fast(new BenchmarkSuite("substituted")
                .Add("work", static () => _ = Guid.NewGuid())
                .WithOutlierDetector(FailingDetector))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

        var warning = Assert.Single(result.Warnings, w => w.Contains(nameof(IOutlierDetector), StringComparison.Ordinal));

        Assert.Contains("built-in", warning, StringComparison.Ordinal);
        Assert.Contains("no detector is available here", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Addressable from the coordinator, and unable to produce anything once it runs in the worker,
    ///     which is the split this test exists for.
    /// </summary>
    /// <remarks>
    ///     Keyed on the entry assembly rather than throwing outright because
    ///     <see cref="BenchmarkSuite.WithOutlierDetector(Func{IOutlierDetector})" /> invokes the factory
    ///     where it is registered - so a factory that always threw would fail at registration and never
    ///     reach a worker at all. This stands in for the real shape: a factory that reads something
    ///     present beside the benchmark and absent beside <c>nbworker</c>.
    /// </remarks>
    private static IOutlierDetector FailingDetector()
        => Assembly.GetEntryAssembly()?.GetName().Name == "nbworker"
            ? throw new InvalidOperationException("no detector is available here")
            : new TrimFractionDetector(0.25);

    /// <summary>
    ///     A detector whose trimming fraction is a constructor argument, so the argument is visible in the
    ///     name it reports and a substituted instance cannot masquerade as it.
    /// </summary>
    internal sealed class TrimFractionDetector(double fraction) : IOutlierDetector
    {
        public static string NameFor(double fraction) => $"trim-fraction {fraction:0.###}";

        public string Name => NameFor(fraction);

        public OutlierClassification Classify(double[] sortedSamples)
        {
            ArgumentNullException.ThrowIfNull(sortedSamples);

            // Trims the slowest fraction. The behaviour is incidental; what matters is that the
            // fraction came from the constructor, so the name identifies which instance was used.
            var trim = (int)(sortedSamples.Length * fraction);

            return new OutlierClassification
            {
                Kept = sortedSamples[..(sortedSamples.Length - trim)],
                Discarded = sortedSamples[(sortedSamples.Length - trim)..],
            };
        }
    }

    /// <summary>
    ///     A significance test reporting a fixed p-value taken from its constructor, so the reported value
    ///     identifies which instance did the scoring.
    /// </summary>
    internal sealed class AlwaysSignificantTest(double pValue) : ISignificanceTest
    {
        public string Name => "always-significant";

        public SignificanceReport Analyze(SignificanceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return new SignificanceReport
            {
                Pairwise =
                [
                    .. context.Candidates.Select(candidate =>
                        new PairwiseComparison(candidate.Name, pValue, SignificanceVerdict.Significant)),
                ],
            };
        }
    }
}
