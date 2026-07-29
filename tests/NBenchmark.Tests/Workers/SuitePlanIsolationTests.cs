using NBenchmark.Attributes;
using NBenchmark.Stats;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Suite mode measured through <see cref="BenchmarkSuite.RunPlanAsync" /> in a real worker.
///     <para>
///         The point of the plan approach is that the <i>factory</i> crosses the process boundary,
///         not the suite. Everything a suite holds is a live delegate - bodies, setup and teardown, a
///         custom detector or significance test - and none of that can be serialized honestly. These
///         tests prove the worker obtains them by running the user's own factory, which is what makes
///         the previous design's "cannot cross the boundary" list disappear rather than shrink.
///     </para>
/// </summary>
[Collection(nameof(RealWorkerCollection))]
public sealed class SuitePlanIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public SuitePlanIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
        ProbeDetector.Reset();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    /// <summary>
    ///     Short but real: enough samples for statistics to exist, few enough that the suite stays
    ///     quick. What these tests are about is the process boundary, not measurement quality.
    /// </summary>
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

    /// <summary>The factory the worker addresses. Static and non-capturing, so it can be located by token.</summary>
    private static BenchmarkSuite BuildComparison() =>
        Fast(new BenchmarkSuite("plan-comparison")
            .Add("fast", () => Thread.SpinWait(200))
            .Add("slow", () => Thread.SpinWait(2_000))
            .WithBaseline("fast"));

    /// <summary>A factory whose suite carries a custom strategy object that could never be serialized.</summary>
    private static BenchmarkSuite BuildWithLiveStrategy() =>
        Fast(new BenchmarkSuite("plan-live-strategy")
            .Add("only", () => Thread.SpinWait(200))
            .WithOutlierDetector(new ProbeDetector(sentinel: 4242)));

    /// <summary>A factory whose suite runs setup and teardown delegates.</summary>
    private static BenchmarkSuite BuildWithLifecycle() =>
        Fast(new BenchmarkSuite("plan-lifecycle")
            .Add("only", () => Thread.SpinWait(200))
            .WithSuiteSetup(() => LifecycleProbe.Setups++)
            .WithSuiteTeardown(() => LifecycleProbe.Teardowns++));

    /// <summary>
    ///     The suite is measured in a worker under the requested runtime profile, and the results come
    ///     back with their samples attached.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_MeasuresInAWorker()
    {
        var results = await BenchmarkSuite.RunPlanAsync(BuildComparison);

        Assert.Equal(2, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

            // Stamped by the measuring process from its own environment.
            Assert.Equal("steady-state", result.RuntimeProfileName);
            Assert.NotEmpty(result.RawSamples);
        }

        // The two bodies differ by an order of magnitude of spin count, so a worker that measured
        // them at all must separate them. Catches plausible numbers for the wrong bodies.
        var fast = results.Single(r => r.Name == "fast");
        var slow = results.Single(r => r.Name == "slow");

        Assert.True(
            slow.Median > fast.Median * 2,
            $"expected slow to be clearly slower: fast={fast.Median:F1}ns slow={slow.Median:F1}ns");
    }

    /// <summary>
    ///     The baseline set on the suite survives, and significance is computed in the coordinator
    ///     from the samples the worker sent. This is the end-to-end proof that the raw-sample defect
    ///     cannot recur on this path: the samples are attached to their own result on the wire.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_ComputesSignificanceFromWorkerSamples()
    {
        var results = await BenchmarkSuite.RunPlanAsync(BuildComparison);

        var baseline = results.Single(r => r.IsBaseline);
        Assert.Equal("fast", baseline.Name);

        var candidate = results.Single(r => !r.IsBaseline);
        Assert.NotNull(candidate.PValue);
        Assert.NotNull(candidate.Effect);
    }

    /// <summary>
    ///     A strategy object that no wire format could carry works, because the worker constructed it
    ///     by running the factory. Under the old design this had to be rebuilt by re-executing the
    ///     entire program; under a serialized-options design it could not be rebuilt at all.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_UsesLiveStrategyObjectsBuiltInTheWorker()
    {
        var results = await BenchmarkSuite.RunPlanAsync(BuildWithLiveStrategy);

        var result = Assert.Single(results);
        Assert.False(result.Errored, result.ErrorMessage);

        // The detector names itself with the sentinel it was constructed with, so this proves the
        // worker used *that* object rather than falling back to a built-in one.
        Assert.Equal(ProbeDetector.NameFor(4242), result.OutlierDetector);
    }

    /// <summary>
    ///     Suite setup and teardown run in the worker, around the measurement - not in the
    ///     coordinator, where they would prepare state the benchmarks never see.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_RunsSetupAndTeardownInTheWorker()
    {
        LifecycleProbe.Reset();

        var results = await BenchmarkSuite.RunPlanAsync(BuildWithLifecycle);

        Assert.False(Assert.Single(results).Errored);

        // Zero here: the counters live in this process, and the worker incremented its own copies.
        // That is the assertion - if these were non-zero, the lifecycle would have run on the wrong
        // side of the boundary and the benchmarks would have measured unprepared state.
        Assert.Equal(0, LifecycleProbe.Setups);
        Assert.Equal(0, LifecycleProbe.Teardowns);
    }

    /// <summary>
    ///     A capturing factory cannot be addressed, so the suite is measured here and labelled. The
    ///     numbers are still produced - refusing to isolate is not refusing to measure.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_CapturingFactory_FallsBackAndSaysSo()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await BenchmarkSuite.RunPlanAsync(
                () => Fast(new BenchmarkSuite("captured").Add("only", () => Thread.SpinWait(spins))));
        }
        finally
        {
            Console.SetError(priorError);
        }

        var result = Assert.Single(results);
        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessCapturedState, result.IsolationStatus);
        Assert.Equal("host", result.RuntimeProfileName);
        Assert.Contains("captures", stderr.ToString());
    }

    /// <summary>
    ///     With no worker deployed the plan still runs, in this process, and explains itself.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_WithNoWorkerDeployed_FallsBackAndSaysSo()
    {
        using var _ = FakeWorkerLauncher.InstallUnavailable();
        SimpleModeGuidance.ResetForTesting();

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await BenchmarkSuite.RunPlanAsync(BuildComparison);
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.InProcessNoWorker, r.IsolationStatus));
        Assert.Contains("nbworker", stderr.ToString());
    }

    /// <summary>
    ///     A factory that throws is reported as a fault the user can act on, not as a worker that
    ///     mysteriously vanished.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_FactoryThatThrows_IsReportedNotSwallowed()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            // The local build happens first and throws here, which is the right place: the user sees
            // their own exception rather than a process-boundary diagnostic wrapping it.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => BenchmarkSuite.RunPlanAsync(ThrowingPlan));
        }
        finally
        {
            Console.SetError(priorError);
        }
    }

    private static BenchmarkSuite ThrowingPlan()
        => throw new InvalidOperationException("the plan could not be built");

    /// <summary>
    ///     <c>[BenchmarkPlan]</c> discovery runs every marked factory on a type, each in its own
    ///     worker. Several suites in one program therefore cost one worker each - linear, where the
    ///     previous callsite-replay design re-ran the whole program per child and did M-squared work.
    /// </summary>
    [Fact]
    public async Task RunPlansAsync_DiscoversAndRunsEveryMarkedPlan()
    {
        // The Type overload, because a static holder class cannot be a generic type argument.
        var results = await BenchmarkSuite.RunPlansAsync(typeof(DeclaredPlans));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));
        Assert.Contains(results, r => r.Name == "alpha");
        Assert.Contains(results, r => r.Name == "beta");
    }

    /// <summary>
    ///     A plan shaped wrongly throws instead of being skipped. Skipping it would leave the author
    ///     with a suite that simply never ran and nothing to explain why.
    /// </summary>
    [Fact]
    public void RunPlansAsync_NonStaticPlan_ThrowsRatherThanSkipping()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BenchmarkPlanDiscovery.Find(typeof(BadPlans)));

        Assert.Contains("not static", ex.Message);
    }

    /// <summary>A type with no plans is an error, not an empty run.</summary>
    [Fact]
    public async Task RunPlansAsync_TypeWithNoPlans_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BenchmarkSuite.RunPlansAsync<SuitePlanIsolationTests>());

        Assert.Contains("no benchmark plans", ex.Message);
    }

    /// <summary>Counters proving which process ran the suite lifecycle.</summary>
    private static class LifecycleProbe
    {
        public static int Setups;
        public static int Teardowns;

        public static void Reset() => Setups = Teardowns = 0;
    }
}

/// <summary>
///     An outlier detector that names itself after a value it was constructed with, so a test can
///     tell whether the object the worker used was the one the factory built or a substitute.
/// </summary>
internal sealed class ProbeDetector(int sentinel) : IOutlierDetector
{
    public ProbeDetector() : this(0)
    {
    }

    public static string NameFor(int sentinel) => $"probe-{sentinel}";

    public static void Reset()
    {
    }

    public string Name => NameFor(sentinel);

    public OutlierClassification Classify(double[] sortedSamples) =>
        OutlierClassification.KeepAll(sortedSamples);
}

/// <summary>Two plans on one type, for discovery.</summary>
internal static class DeclaredPlans
{
    [BenchmarkPlan]
    public static BenchmarkSuite Alpha() => Shorten(new BenchmarkSuite("alpha-plan").Add("alpha", () => Thread.SpinWait(200)));

    [BenchmarkPlan]
    public static BenchmarkSuite Beta() => Shorten(new BenchmarkSuite("beta-plan").Add("beta", () => Thread.SpinWait(200)));

    private static BenchmarkSuite Shorten(BenchmarkSuite suite) => suite
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
}

/// <summary>A plan marked on an instance method, which cannot be invoked in a worker.</summary>
internal sealed class BadPlans
{
    [BenchmarkPlan]
    public BenchmarkSuite NotStatic() => new("bad");
}
