using NBenchmark;
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
        SingleModeGuidance.ResetForTesting();
        ProbeDetector.Reset();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    /// <summary>
    ///     Short but real: enough samples for statistics to exist, few enough that the suite stays
    ///     quick. What these tests are about is the process boundary, not measurement quality.
    /// </summary>
    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithSamples(16)
        .WithWarmupSamples(1)
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

    /// <summary>
    ///     The same suite with the hard-error gate down, for the tests about what the labelled fallback
    ///     does. A separate factory rather than a flag on <see cref="BuildComparison" />, because a plan
    ///     factory is addressed by token and the isolated tests must keep measuring the unmodified one.
    /// </summary>
    private static BenchmarkSuite BuildComparisonAcceptingFallback() =>
        BuildComparison().WithIsolation(Isolation.Preferred);

    /// <summary>A factory whose suite carries a custom strategy object that could never be serialized.</summary>
    private static BenchmarkSuite BuildWithLiveStrategy() =>
        Fast(new BenchmarkSuite("plan-live-strategy")
            .Add("only", () => Thread.SpinWait(200))
            .WithOutlierDetector(static () => new ProbeDetector(sentinel: 4242)));

    /// <summary>A factory whose suite asks for replicates.</summary>
    private static BenchmarkSuite BuildReplicated() =>
        Fast(new BenchmarkSuite("plan-replicated")
            .Add("only", () => Thread.SpinWait(200))
            .WithLaunchCount(3));

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
            slow.MedianNs > fast.MedianNs * 2,
            $"expected slow to be clearly slower: fast={fast.MedianNs:F1}ns slow={slow.MedianNs:F1}ns");
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
        Assert.Equal(ProbeDetector.NameFor(4242), result.OutlierDetectorName);
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
            // RequireIsolation off: this is about what the labelled fallback says, and the fallback is
            // only reachable with the gate down. The gate itself is covered by RequiredIsolationTests.
            results = await BenchmarkSuite.RunPlanAsync(
                () => Fast(new BenchmarkSuite("captured").Add("only", () => Thread.SpinWait(spins)))
                    .WithIsolation(Isolation.Preferred));
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
    ///     A capturing factory whose <i>bodies</i> capture nothing is still isolated - as an inline
    ///     suite - and says so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The plan path falls back to <c>RunCoreAsync</c>, which addresses the bodies
    ///         individually, and that succeeds whenever only the factory was the problem. The results
    ///         used to be overwritten with the plan's refusal status regardless, so a run that had been
    ///         fully isolated came back marked host-measured. Three things went wrong at once: the row
    ///         was labelled with a refusal that had not applied to it, the reporters - invoked inside
    ///         the isolated path - wrote <c>Isolated</c> while the returned list said otherwise, and
    ///         <c>--strict-isolation</c> would have failed the build over it.
    ///     </para>
    ///     <para>
    ///         The stderr line is checked for what it no longer claims. Announcing "was measured in
    ///         this process" before attempting the inline path stated an outcome that had not happened
    ///         yet, and was false in exactly this case.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task RunPlanAsync_CapturingFactory_IsIsolated_AndTheCaptureReachesThePlan()
    {
        var label = $"captured-{Guid.NewGuid():N}";

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            // The factory captures `label`. It used to be unaddressable for that alone, and the run
            // fell back to addressing the bodies inline; now the capture travels and the plan itself
            // is what the worker runs.
            results = await BenchmarkSuite.RunPlanAsync(
                () => Fast(new BenchmarkSuite("plan-captures").Add(label, () => Thread.SpinWait(200))));
        }
        finally
        {
            Console.SetError(priorError);
        }

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.Equal("steady-state", result.RuntimeProfileName);

        // The name is the captured value. The plan ran in the worker, so this is only what the caller
        // wrote if `label` crossed with it.
        Assert.Equal(label, result.Name);

        var message = stderr.ToString();

        Assert.DoesNotContain("could not be addressed", message);
        Assert.DoesNotContain("was measured in this process", message);
    }

    /// <summary>
    ///     With no worker deployed the plan still runs, in this process, and explains itself.
    /// </summary>
    [Fact]
    public async Task RunPlanAsync_WithNoWorkerDeployed_FallsBackAndSaysSo()
    {
        using var _ = FakeWorkerLauncher.InstallUnavailable();
        SingleModeGuidance.ResetForTesting();

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await BenchmarkSuite.RunPlanAsync(BuildComparisonAcceptingFallback);
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
        var ex = Assert.Throws<BenchmarkConfigurationException>(
            () => BenchmarkPlanDiscovery.Find(typeof(BadPlans)));

        Assert.Contains("not static", ex.Message);
    }

    /// <summary>A type with no plans is an error, not an empty run.</summary>
    [Fact]
    public async Task RunPlansAsync_TypeWithNoPlans_Throws()
    {
        var ex = await Assert.ThrowsAsync<BenchmarkConfigurationException>(
            () => BenchmarkSuite.RunPlansAsync<SuitePlanIsolationTests>());

        Assert.Contains("no benchmark plans", ex.Message);
    }

    /// <summary>
    ///     One worker per replicate, from a launch count the <i>factory</i> set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The plan path is the only one where the same suite object exists on both sides of the
    ///         boundary: the factory runs here, so the coordinator can read what it asked for, and again
    ///         in every worker, where its <c>WithLaunchCount(3)</c> must be ignored or each replicate
    ///         would measure three times and the between-worker spread would be three within-worker
    ///         ones averaged. Ignoring it is structural rather than defensive - the worker's
    ///         measurement path does not read the count, and the request has no field carrying it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task RunPlanAsync_LaunchCount_SpawnsOneWorkerPerReplicate()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await BenchmarkSuite.RunPlanAsync(BuildReplicated);

        Assert.Equal(3, launcher.GroupsRun);

        var result = Assert.Single(results);
        Assert.False(result.Errored, result.ErrorMessage);
        Assert.NotNull(result.LaunchStatistics);
        Assert.Equal(3, result.LaunchStatistics!.LaunchCount);
    }

    /// <summary>Wraps the real launcher to count the groups a run actually launched.</summary>
    private sealed class CountingLauncher(string workerAssemblyPath) : IWorkerLauncher
    {
        private readonly RealWorkerLauncher _inner = new(workerAssemblyPath);

        public int GroupsRun { get; private set; }

        public bool IsAvailable => true;

        public Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            GroupsRun++;

            return _inner.RunGroupAsync(request, progress, observer, timeout, cancellationToken);
        }
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

    public OutlierClassification Classify(ReadOnlySpan<double> sortedSamples) =>
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
        .WithSamples(16)
        .WithWarmupSamples(1)
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
